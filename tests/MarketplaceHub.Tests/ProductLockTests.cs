using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #904 (PRODUCT LOCKS: the scoped manual lock policy). A lock is per field, carries the operator's reason and its
// moment, is recorded in the audit trail when switched, is honoured by the import and by the bulk operations, is
// previewed before it is released, and survives a restart. Never a value in the words.
[TestClass]
public sealed class ProductLockTests
{
    static readonly DateTime Now = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);
    static XmlSource Source(string name, bool enabled = true, bool auto = true) => new()
    {
        Name = name, Location = "https://feeds.example.com/" + name.ToLowerInvariant() + ".xml?key=abc123", Enabled = enabled, AutoImport = auto, IntervalMinutes = 30, ItemPath = "/p",
        Fields = new Dictionary<string, string> { ["Sku"] = "s", ["Name"] = "n", ["Cost"] = "c", ["Stock"] = "q" },
    };

    [TestMethod]
    public void ALockHasAScopeAReasonAMomentAndAPreviewOfItsRelease()
    {
        Assert.AreEqual(5, ProductLocks.Scopes.Count);
        var product = new CatalogProduct { Id = "p1", Sku = "SKU-1", Name = "Kupa", SourceId = "a", SourceKind = "xml", LockPrice = true, LockStock = false, LockReasons = new Dictionary<string, LockNote>(StringComparer.Ordinal) { ["Price"] = new() { Reason = "kampanya fiyatı", SinceUtc = Now.AddHours(-2) } } };
        Assert.IsTrue(ProductLocks.IsLocked(product, "Price")); Assert.IsTrue(ProductLocks.IsLocked(product, "Currency"), "the currency travels with the price lock"); Assert.IsFalse(ProductLocks.IsLocked(product, "Stock")); Assert.IsFalse(ProductLocks.IsLocked(product, "Gtin"));
        var locks = ProductLocks.Of(product);
        var price = locks.Single(l => l.Field == "Price"); Assert.IsTrue(price.Locked); Assert.AreEqual("kampanya fiyatı", price.Reason); Assert.AreEqual(Now.AddHours(-2), price.SinceUtc); Assert.AreEqual("fiyat", price.Label);
        Assert.IsTrue(locks.Where(l => l.Field != "Price").All(l => !l.Locked && l.Reason.Length == 0));

        // Turning locks on and off between two records, with the edited record's reasons; the store's stamp adds the moment and drops stale notes.
        var edited = new CatalogProduct { Id = "p1", Sku = "SKU-1", Name = "Kupa", SourceId = "a", SourceKind = "xml", LockPrice = false, LockStock = true, LockReasons = new Dictionary<string, LockNote>(StringComparer.Ordinal) { ["Stock"] = new() { Reason = "  sayım sürüyor  " }, ["Price"] = new() { Reason = "eski" } } };
        var changes = ProductLocks.Diff(product, edited);
        Assert.AreEqual(2, changes.Count);
        Assert.IsTrue(changes.Single(c => c.Field == "Stock").Locked); Assert.AreEqual("sayım sürüyor", changes.Single(c => c.Field == "Stock").Reason);
        Assert.IsFalse(changes.Single(c => c.Field == "Price").Locked);
        ProductLocks.Stamp(product, edited, Now);
        Assert.AreEqual(Now, edited.LockReasons!["Stock"].SinceUtc); Assert.IsFalse(edited.LockReasons.ContainsKey("Price"), "a released lock drops its note");
        Assert.AreEqual(0, ProductLocks.Diff(edited, edited).Count);

        // The audit words.
        var on = ProductLocks.ToAudit(edited, changes.Single(c => c.Field == "Stock")); Assert.AreEqual(ProductLocks.LockAction, on.Action); Assert.AreEqual("p1", on.ProductId); Assert.AreEqual("stok kilitlendi · gerekçe: sayım sürüyor", on.Detail);
        var off = ProductLocks.ToAudit(edited, changes.Single(c => c.Field == "Price")); Assert.AreEqual(ProductLocks.UnlockAction, off.Action); Assert.AreEqual("fiyat kilidi açıldı", off.Detail);
        Assert.AreEqual(ProductLocks.ReasonLimit, ProductLocks.SafeReason(new string('x', 500)).Length);

        // The release preview: the reason, the origin, the last refused decision, and whether the source will run.
        var a = Source("Tedarikçi A"); a.Id = "a"; var off2 = Source("Tedarikçi B", enabled: false); off2.Id = "b";
        product.FieldOrigins = new Dictionary<string, FieldOrigin>(StringComparer.Ordinal) { ["Price"] = new() { Kind = FieldProvenance.ManualKind, ObservedUtc = Now.AddHours(-2), Decision = "elle kilitli · Tedarikçi A yazamadı" } };
        var preview = ProductLocks.PreviewUnlock(product, "Price", id => id == "a" ? a : id == "b" ? off2 : null, Now);
        StringAssert.Contains(preview.Headline, "Fiyat kilidi açılacak");
        Assert.IsTrue(preview.Lines.Any(l => l.Contains("Kilit gerekçesi: kampanya fiyatı") && l.Contains("2 sa önce")), string.Join(" | ", preview.Lines));
        Assert.IsTrue(preview.Lines.Any(l => l.StartsWith("Alan kökeni: elle", StringComparison.Ordinal)));
        Assert.IsTrue(preview.Lines.Any(l => l.Contains("Son karar: elle kilitli · Tedarikçi A yazamadı")));
        Assert.IsTrue(preview.Lines.Any(l => l.Contains("Tedarikçi A bir sonraki okumada") && l.Contains("zamanlayıcı açık")));
        product.SourceId = "b";
        Assert.IsTrue(ProductLocks.PreviewUnlock(product, "Price", id => id == "b" ? off2 : null, Now).Lines.Any(l => l.Contains("devre dışı")));
        product.SourceId = "";
        Assert.IsTrue(ProductLocks.PreviewUnlock(product, "Price", _ => null, Now).Lines.Any(l => l.Contains("kaynağı yok")));
        var text = string.Join(" ", preview.Lines.Append(preview.Headline).Append(on.Detail).Append(off.Detail));
        Assert.IsFalse(text.Contains("example.com") || text.Contains("abc123") || text.Contains("key="), text);
    }

    [TestMethod]
    public void TheStoreRecordsSwitchesTheImportAndTheBulkOperationsHonourTheLockAndARestartKeepsIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "locks-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CatalogStore(root); var runs = new XmlRunStore(root);
            var a = Source("Tedarikçi A"); store.SaveSource(a);
            void Import(decimal price, string name, DateTime at)
            {
                var run = runs.Start(a.Id, Guid.NewGuid().ToString("N"), TimeSpan.FromMinutes(5), a.ConfigRevision);
                store.Import(a, new[] { new CatalogProduct { SourceId = a.Id, SourceKind = "xml", Sku = "SKU-1", Name = name, Price = price, Currency = "TRY", Cost = 4, Stock = 3 } }, CancellationToken.None, new XmlImportContext { RunId = run, SourceRevision = a.ConfigRevision, ObservedAtUtc = at });
                runs.Complete(run, new ImportSummary(0, 0, 0));
            }
            Import(10, "Kupa", Now);

            // The operator locks the price with a reason: the store keeps the reason and the moment and writes the audit row.
            var edit = store.FindProduct(store.Products().Single().Id)!;
            edit.LockPrice = true; edit.Price = 11; edit.LockReasons = new Dictionary<string, LockNote>(StringComparer.Ordinal) { ["Price"] = new() { Reason = "kampanya" } };
            store.SaveProduct(edit);
            var saved = store.FindProduct(edit.Id)!;
            Assert.IsTrue(saved.LockPrice); Assert.AreEqual("kampanya", saved.LockReasons!["Price"].Reason); Assert.IsNotNull(saved.LockReasons["Price"].SinceUtc);
            var audit = new AuditStore(root).List(50);
            var locked = audit.Single(e => e.Action == ProductLocks.LockAction); Assert.AreEqual(edit.Id, locked.ProductId); StringAssert.Contains(locked.Detail, "fiyat kilitlendi"); StringAssert.Contains(locked.Detail, "kampanya");

            // An import attempt: the feed carries a new price, the lock holds it, and the field says why.
            Import(12, "Kupa", Now.AddMinutes(30));
            var afterImport = store.FindProduct(edit.Id)!;
            Assert.AreEqual(11, afterImport.Price); StringAssert.Contains(FieldProvenance.Of(afterImport, "Price")!.Decision, "elle kilitli"); Assert.IsTrue(afterImport.LockPrice);

            // A bulk operation: the title lock skips the rename, the description lock skips the description, an unlocked field goes through.
            var titleLocked = store.FindProduct(edit.Id)!; titleLocked.LockName = true; titleLocked.LockReasons!["Name"] = new LockNote { Reason = "SEO başlığı" }; store.SaveProduct(titleLocked);
            var bulk = new BulkProductOperations(store);
            var rename = bulk.Preview(new[] { store.FindProduct(edit.Id)! }, new BulkProductOperationRequest(BulkProductOperationKind.SetName, "Yeni başlık"));
            Assert.AreEqual("SKIP", rename.Lines.Single().Status); StringAssert.Contains(rename.Lines.Single().Error, "Başlık kilidi");
            var brand = bulk.Preview(new[] { store.FindProduct(edit.Id)! }, new BulkProductOperationRequest(BulkProductOperationKind.SetBrand, "Marka"));
            Assert.AreEqual("READY", brand.Lines.Single().Status);

            // Restart: the locks and their reasons are still there; releasing one drops its note and writes the unlock row.
            SqliteConnection.ClearAllPools();
            var reopened = new CatalogStore(root);
            var again = reopened.FindProduct(edit.Id)!;
            Assert.IsTrue(again.LockPrice && again.LockName); Assert.AreEqual("kampanya", again.LockReasons!["Price"].Reason); Assert.AreEqual("SEO başlığı", again.LockReasons["Name"].Reason);
            again.LockPrice = false; reopened.SaveProduct(again);
            var released = reopened.FindProduct(edit.Id)!;
            Assert.IsFalse(released.LockPrice); Assert.IsFalse(released.LockReasons!.ContainsKey("Price")); Assert.IsTrue(released.LockReasons.ContainsKey("Name"));
            Assert.AreEqual(1, new AuditStore(root).List(50).Count(e => e.Action == ProductLocks.UnlockAction));
        }
        finally
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
                catch (IOException) { Thread.Sleep(300); }
                catch (UnauthorizedAccessException) { Thread.Sleep(300); }
            }
        }
    }
}
