using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #911 (PRODUCT COMPLIANCE: document attachment metadata). A product's safety and manufacturer documents are kept
// with their kind, revision, expiry, size, hash and origin in the catalogue database and their bytes under the data
// directory the way the feed cache keeps files; the same bytes under the same kind are one revision; an expired
// document is not accepted and one that expires is said so; a missing or corrupt file is never served; a product id
// is validated before it becomes a folder; the picked path is never kept and never shown; all of it survives a restart.
[TestClass]
public sealed class ProductDocumentTests
{
    static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);

    static string Pick(string directory, string name, string content) { Directory.CreateDirectory(directory); var path = Path.Combine(directory, name); File.WriteAllText(path, content, Encoding.UTF8); return path; }

    [TestMethod]
    public void AttachRevisionExpiryAndStateFollowTheRulesAndNothingLeaksThePickedPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "docs-" + Guid.NewGuid().ToString("N")); var pick = Path.Combine(root, "picked-from-here");
        try
        {
            var store = new ProductDocumentStore(root);

            // Attach: bytes copied under an id-based name inside the store, the picked file's name kept as the display name, the path not.
            var first = store.Attach("p1", "safety", Pick(pick, "guvenlik-formu.pdf", "%PDF-1.4 first"), Now.AddDays(90), nowUtc: Now);
            Assert.AreEqual(1, first.Revision); Assert.AreEqual("guvenlik-formu.pdf", first.DisplayName); Assert.AreEqual(".pdf", first.Extension); Assert.AreEqual(FieldProvenance.ManualKind, first.Origin);
            var served = store.PathFor(first)!; Assert.IsTrue(served.StartsWith(store.Root, StringComparison.OrdinalIgnoreCase)); Assert.IsFalse(served.StartsWith(pick, StringComparison.OrdinalIgnoreCase)); Assert.IsTrue(File.Exists(served));
            var ok = store.State(first, Now); Assert.AreEqual(ProductDocumentState.Ok, ok.Status); StringAssert.Contains(ok.Words, "Güvenlik bilgi formu"); StringAssert.Contains(ok.Words, "rev. 1");

            // Duplicate revision: the same bytes under the same kind are refused by name of the revision they already are; different bytes are the next revision.
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => store.Attach("p1", "safety", Pick(pick, "copy.pdf", "%PDF-1.4 first"), null, nowUtc: Now)).Message, "rev. 1");
            var second = store.Attach("p1", "safety", Pick(pick, "guvenlik-formu-v2.pdf", "%PDF-1.4 second"), null, nowUtc: Now); Assert.AreEqual(2, second.Revision);
            Assert.AreEqual(2, store.List("p1").Count);

            // Expiry: an already expired document is not accepted; one about to expire says so; once past, it is expired and a finding.
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => store.Attach("p1", "certificate", Pick(pick, "old.pdf", "%PDF old"), Now.AddDays(-1), nowUtc: Now)).Message, "süresi dolmuş");
            var soon = store.Attach("p1", "certificate", Pick(pick, "cert.pdf", "%PDF cert"), Now.AddDays(10), nowUtc: Now);
            Assert.AreEqual(ProductDocumentState.Expiring, store.State(soon, Now).Status); Assert.AreEqual(ProductDocumentState.Expired, store.State(soon, Now.AddDays(11)).Status);
            Assert.IsTrue(store.Findings("p1", Now.AddDays(11)).Any(f => f.Contains("süresi doldu") && f.Contains("Uygunluk sertifikası"))); Assert.AreEqual(0, store.Findings("p1", Now).Count);

            // Missing and corrupt files are never served.
            File.Delete(served); Assert.AreEqual(ProductDocumentState.Missing, store.State(first, Now).Status); Assert.IsNull(store.PathFor(first)); Assert.IsTrue(store.Findings("p1", Now).Any(f => f.Contains("dosya eksik")));
            File.WriteAllText(store.PathFor(second)!, "tampered"); Assert.AreEqual(ProductDocumentState.Corrupt, store.State(second, Now).Status); Assert.IsNull(store.PathFor(second));
            var check = store.Check(Now); Assert.AreEqual("WARN", check.Status); StringAssert.Contains(check.Detail, "2 eksik/bozuk");

            // Security: a product id that could leave the folder, a disallowed extension, an oversized file and an unknown kind are refused; names are cleaned; audit rows carry no name and no path.
            Assert.ThrowsException<ArgumentException>(() => store.Attach("../p1", "safety", Pick(pick, "x.pdf", "%PDF x"), null, nowUtc: Now));
            Assert.ThrowsException<ArgumentException>(() => store.Attach("p1/../../p2", "safety", Pick(pick, "y.pdf", "%PDF y"), null, nowUtc: Now));
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => store.Attach("p1", "safety", Pick(pick, "run.exe", "MZ"), null, nowUtc: Now)).Message, "desteklenmiyor");
            var big = Path.Combine(pick, "big.pdf"); using (var f = File.Create(big)) f.SetLength(ProductDocumentStore.MaxBytes + 1);
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => store.Attach("p1", "safety", big, null, nowUtc: Now)).Message, "20 MB");
            Assert.ThrowsException<InvalidOperationException>(() => store.Attach("p1", "passport", Pick(pick, "z.pdf", "%PDF z"), null, nowUtc: Now));
            Assert.AreEqual("name.pdf", ProductDocumentStore.CleanName("..\\..\\evil\\name.pdf")); Assert.AreEqual("name.pdf", ProductDocumentStore.CleanName("/etc/name.pdf"));
            var audit = ProductDocumentStore.ToAudit(soon, ProductDocumentStore.AttachAction);
            Assert.AreEqual("catalog", audit.Module); Assert.AreEqual("p1", audit.ProductId); StringAssert.Contains(audit.Detail, "Uygunluk sertifikası · rev. 1"); Assert.IsFalse(audit.Detail.Contains("cert.pdf")); Assert.IsFalse(audit.Detail.Contains(root));
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "p2")), "nothing was written outside the product folder");

            // Remove: the row and the file go together.
            Assert.IsTrue(store.Remove(soon.Id)); Assert.IsNull(store.Find(soon.Id)); Assert.AreEqual(2, store.List("p1").Count); Assert.IsFalse(store.Remove(soon.Id));
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void TheStoreReadsTheSameDocumentsAfterARestartAndServesOnlyVerifiedFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "docs-" + Guid.NewGuid().ToString("N")); var pick = Path.Combine(root, "picked-from-here");
        try
        {
            var store = new ProductDocumentStore(root);
            var safety = store.Attach("p1", "safety", Pick(pick, "sds.pdf", "%PDF sds"), Now.AddDays(200), nowUtc: Now);
            var maker = store.Attach("p1", "manufacturer", Pick(pick, "beyan.png", "PNG bytes"), null, nowUtc: Now);
            store.Attach("p2", "other", Pick(pick, "note.jpg", "JPG bytes"), null, nowUtc: Now);

            SqliteConnection.ClearAllPools();
            var reopened = new ProductDocumentStore(root);
            var docs = reopened.List("p1"); Assert.AreEqual(2, docs.Count);
            var sds = docs.Single(d => d.Id == safety.Id); Assert.AreEqual(Now.AddDays(200).Date, sds.ExpiresOn); Assert.AreEqual(1, sds.Revision); Assert.AreEqual("sds.pdf", sds.DisplayName); Assert.AreEqual(safety.Sha256, sds.Sha256);
            Assert.AreEqual(".png", reopened.Find(maker.Id)!.Extension);
            Assert.IsNotNull(reopened.PathFor(sds)); Assert.IsNotNull(reopened.PathFor(reopened.Find(maker.Id)!));
            Assert.IsTrue(reopened.States("p1", Now).All(s => s.IsUsable)); Assert.AreEqual(1, reopened.List("p2").Count);
            var check = reopened.Check(Now); Assert.AreEqual("OK", check.Status); StringAssert.Contains(check.Detail, "3 belge");
            Assert.IsFalse(Directory.EnumerateFiles(reopened.Root, "*", SearchOption.AllDirectories).Any(f => f.Contains(".tmp-", StringComparison.Ordinal)), "no temporary file is left behind");
        }
        finally { Cleanup(root); }
    }

    static void Cleanup(string root)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
            catch (IOException) { Thread.Sleep(300); }
            catch (UnauthorizedAccessException) { Thread.Sleep(300); }
        }
    }
}
