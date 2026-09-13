using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #893 (SOURCE GOVERNANCE: immutable source revision snapshot). Every configuration change to a source is one
// numbered, immutable snapshot written with the source; state-only saves append nothing; a concurrent edit is
// refused before anything is written; a rollback restores a revision's configuration and the save that follows is
// a new revision naming it; runs record the revision they ran under; the ledger survives a restart.
[TestClass]
public sealed class SourceRevisionTests
{
    static XmlSource Source(string id, string name = "Besleme", string location = "https://feed.example.com/a.xml") => new()
    {
        Id = id, Name = name, Location = location, ItemPath = "/Products/Product", Currency = "TRY",
        Fields = new Dictionary<string, string> { ["Sku"] = "Code", ["Name"] = "Title" },
    };

    [TestMethod]
    public void TheFingerprintCoversConfigurationOnlyAndIgnoresOrderAndState()
    {
        var a = Source("s1"); var b = Source("s1");
        b.Fields = new Dictionary<string, string> { ["Name"] = "Title", ["Sku"] = "Code" };
        b.LastRunUtc = DateTime.UtcNow; b.LastStatus = "3 yeni"; b.LastHealthState = "HEALTHY"; b.MappingRevision = 7; b.LastSuccessfulFeedHash = "abc"; b.LastCredentialState = "VALID";
        Assert.AreEqual(SourceConfig.Of(a).Fingerprint(), SourceConfig.Of(b).Fingerprint(), "mapping order and state never make a revision");
        b.MarkupPercent = 55;
        Assert.AreNotEqual(SourceConfig.Of(a).Fingerprint(), SourceConfig.Of(b).Fingerprint(), "a rule change does");
        var restored = new XmlSource { Id = "s1", LastStatus = "state stays" };
        SourceConfig.Of(b).ApplyTo(restored);
        Assert.AreEqual(55, restored.MarkupPercent); Assert.AreEqual("state stays", restored.LastStatus); Assert.AreEqual("s1", restored.Id);
        Assert.AreEqual(SourceConfig.Of(b).Fingerprint(), SourceConfig.Of(restored).Fingerprint());
        Assert.IsNotNull(SourceConfig.FromJson(SourceConfig.Of(a).ToJson())); Assert.IsNull(SourceConfig.FromJson("nope"));
        Assert.AreEqual(SourceRevisionStore.SaveReason, SourceRevisionStore.SafeReason("")); Assert.AreEqual(80, SourceRevisionStore.SafeReason(new string('x', 200)).Length);
        Assert.IsFalse(SourceRevisionStore.SafeReason("geri alma ayse@example.com").Contains("example.com"));
    }

    [TestMethod]
    public void ConfigurationChangesAppendRevisionsStateOnlySavesDoNotAndTheLedgerSurvivesARestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "rev-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CatalogStore(root);
            var source = Source("s1");
            store.SaveSource(source);
            Assert.AreEqual(1, source.ConfigRevision, "the first save is revision 1");
            var loaded = store.Sources().Single();
            Assert.AreEqual(1, loaded.ConfigRevision);
            CollectionAssert.AreEqual(new[] { 1 }, store.SourceRevisions("s1").Select(r => r.Revision).ToArray());

            // State only: no new revision.
            loaded.LastRunUtc = DateTime.UtcNow; loaded.LastStatus = "3 yeni"; loaded.LastHealthState = "HEALTHY";
            store.SaveSource(loaded);
            Assert.AreEqual(1, loaded.ConfigRevision); Assert.AreEqual(1, store.SourceRevisions("s1").Count);

            // A configuration change: revision 2, the snapshot holds the new value, the old one is untouched.
            var edited = store.Sources().Single(); edited.MarkupPercent = 55; edited.Fields["Cost"] = "Price";
            store.SaveSource(edited, "kâr oranı ve maliyet alanı");
            Assert.AreEqual(2, edited.ConfigRevision);
            var revisions = store.SourceRevisions("s1");
            CollectionAssert.AreEqual(new[] { 2, 1 }, revisions.Select(r => r.Revision).ToArray());
            Assert.AreEqual(55, revisions[0].Config!.MarkupPercent); Assert.AreEqual(40, revisions[1].Config!.MarkupPercent); Assert.AreEqual("kâr oranı ve maliyet alanı", revisions[0].Reason);
            Assert.AreNotEqual(revisions[0].Fingerprint, revisions[1].Fingerprint);

            // Restart: a fresh store sees the same ledger and the same current revision.
            SqliteConnection.ClearAllPools();
            var again = new CatalogStore(root);
            Assert.AreEqual(2, again.Sources().Single().ConfigRevision); Assert.AreEqual(2, again.SourceRevisions("s1").Count);
            Assert.AreEqual(40, again.SourceRevision("s1", 1)!.Config!.MarkupPercent);
            Assert.IsNull(again.SourceRevision("s1", 9));
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void AConcurrentEditIsRefusedBeforeAnythingIsWrittenAndARollbackIsANewRevision()
    {
        var root = Path.Combine(Path.GetTempPath(), "rev-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CatalogStore(root);
            store.SaveSource(Source("s1"));
            // Two editors load revision 1; the first saves a change (revision 2); the second's save is a conflict.
            var first = store.Sources().Single(); var second = store.Sources().Single();
            first.IntervalMinutes = 60; store.SaveSource(first);
            Assert.AreEqual(2, first.ConfigRevision);
            second.Name = "Başka ad";
            var conflict = Assert.ThrowsException<SourceRevisionConflictException>(() => store.SaveSource(second));
            Assert.AreEqual(1, conflict.LoadedRevision); Assert.AreEqual(2, conflict.CurrentRevision); StringAssert.Contains(conflict.Message, "üzerine yazılmadı");
            var persisted = store.Sources().Single();
            Assert.AreEqual("Besleme", persisted.Name); Assert.AreEqual(60, persisted.IntervalMinutes); Assert.AreEqual(2, persisted.ConfigRevision); Assert.AreEqual(2, store.SourceRevisions("s1").Count);
            // The second editor reloads and saves: revision 3.
            var reloaded = store.Sources().Single(); reloaded.Name = "Başka ad"; store.SaveSource(reloaded);
            Assert.AreEqual(3, reloaded.ConfigRevision);

            // Rollback to revision 1: its configuration comes back, history is not rewritten, the save is revision 4 naming 1.
            var rolled = store.RestoreSourceRevision("s1", 1);
            Assert.AreEqual(4, rolled.ConfigRevision); Assert.AreEqual("Besleme", rolled.Name); Assert.AreEqual(30, rolled.IntervalMinutes);
            var ledger = store.SourceRevisions("s1");
            CollectionAssert.AreEqual(new[] { 4, 3, 2, 1 }, ledger.Select(r => r.Revision).ToArray());
            StringAssert.Contains(ledger[0].Reason, "rev. 1"); Assert.AreEqual(ledger[3].Fingerprint, ledger[0].Fingerprint, "the restored configuration is revision 1's");
            Assert.AreEqual("Başka ad", ledger[1].Config!.Name, "revision 3 stays what it was");
            Assert.ThrowsException<InvalidOperationException>(() => store.RestoreSourceRevision("s1", 99));
            Assert.ThrowsException<InvalidOperationException>(() => store.RestoreSourceRevision("nope", 1));

            // Runs record the revision they ran under.
            var runs = new XmlRunStore(root);
            var id = runs.Start("s1", "hash", TimeSpan.FromMinutes(5), sourceRevision: rolled.ConfigRevision); runs.Complete(id, new ImportSummary(0, 0, 0));
            Assert.AreEqual(4, runs.List("s1").Single().SourceRevision);
            var older = runs.Start("s1"); runs.Fail(older, "x");
            Assert.AreEqual(0, runs.List("s1").Single(r => r.Id == older).SourceRevision, "a run without a revision says 0");
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
