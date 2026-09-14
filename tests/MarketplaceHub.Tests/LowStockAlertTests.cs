using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #941 (STOCK: low-stock alert profiles). A threshold per product, source or store; the most specific decides; a
// store profile judges the store's available figure through the real projection; the findings become dashboard
// alerts deduplicated by the ledger's fingerprint -- a repeat counts, a recovery or a disabled profile resolves, a
// relapse reopens -- and the ledger reads back after a restart.
[TestClass]
public sealed class LowStockAlertTests
{
    static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
    static LowStockAlertProfile Profile(long id, string scope, string scopeId, int threshold, bool enabled = true) => new(id, scope, scopeId, threshold, enabled, "", Now);

    [TestMethod]
    public void TheMostSpecificEnabledProfileDecidesAndAStoreProfileJudgesTheStoresAvailableFigure()
    {
        var products = new[]
        {
            new CatalogProduct { Id = "p1", Sku = "SKU-1", SourceId = "src-a", Stock = 2, Active = true },
            new CatalogProduct { Id = "p2", Sku = "SKU-2", SourceId = "src-a", Stock = 5, Active = true },
            new CatalogProduct { Id = "p3", Sku = "SKU-3", SourceId = "src-b", Stock = 0, Active = false },
            new CatalogProduct { Id = "p4", Sku = "SKU-4", SourceId = "src-b", Stock = 1, Active = true },
        };
        var profiles = new[] { Profile(1, "product", "p1", 5), Profile(2, "source", "src-a", 3), Profile(3, "store", "etsy/S1", 4), Profile(4, "source", "src-b", 10, enabled: false) };
        var alerts = LowStockAlerts.Evaluate(products, profiles, (channel, shop, id) => id == "p4" ? 0 : 9, Now);
        Assert.AreEqual(2, alerts.Count, string.Join(" | ", alerts.Select(a => a.Detail)));
        var own = alerts.Single(a => a.ProductId == "p1"); Assert.AreEqual(("product", 1L, 5, 2, ""), (own.Scope, own.ProfileId, own.Threshold, own.Observed, own.StoreKey), "the product's own profile beats its source's"); Assert.AreEqual("Düşük stok: SKU-1", own.Title); StringAssert.Contains(own.Detail, "stok 2 ≤ eşik 5 (ürün profili #1)");
        var store = alerts.Single(a => a.ProductId == "p4"); Assert.AreEqual(("store", 3L, 4, 0, "etsy|S1"), (store.Scope, store.ProfileId, store.Threshold, store.Observed, store.StoreKey), "no product or source profile covers p4, so the store's does, on the store's figure"); StringAssert.Contains(store.Detail, "etsy/S1: gösterilebilir 0 ≤ eşik 4 (mağaza profili #3)");
        Assert.IsFalse(alerts.Any(a => a.ProductId == "p2"), "5 is above its source's threshold of 3, and a source-covered product gets no store alert"); Assert.IsFalse(alerts.Any(a => a.ProductId == "p3"), "an inactive product raises nothing");
        Assert.AreEqual(0, LowStockAlerts.Evaluate(products, new[] { Profile(1, "product", "p1", 5, enabled: false) }, null, Now).Count, "a disabled profile raises nothing");
        Assert.AreEqual(1, LowStockAlerts.Evaluate(products, new[] { Profile(3, "store", "etsy/S1", 1) }, null, Now).Count, "without a store figure the record's stock is judged: only p4 (1) is at the threshold");
        Assert.AreEqual(("etsy", "S1"), LowStockAlerts.SplitStore(" Etsy/S1 ")); Assert.AreEqual(("", ""), LowStockAlerts.SplitStore("nostore"));
    }

    [TestMethod]
    public void TheDashboardRaisesTheAlertOnceRepeatsCountRecoveryAndADisabledProfileResolveARelapseReopensAndItReadsBackAfterARestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "low-stock-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CatalogStore(root); var runs = new XmlRunStore(root); var profiles = new LowStockAlertProfileStore(root); var ledger = new NotificationStore(root);
            var source = new XmlSource { Id = "src", Name = "Kaynak", ItemPath = "/p", Fields = new Dictionary<string, string> { ["Sku"] = "s", ["Name"] = "n" }, IntervalMinutes = 30 }; store.SaveSource(source);
            var run = runs.Start("src", "h-" + Guid.NewGuid().ToString("N")[..8], TimeSpan.FromMinutes(5), source.ConfigRevision);
            store.Import(source, new[] { new CatalogProduct { SourceId = "src", SourceKind = "xml", Sku = "SKU-1", Name = "Bir", Price = 10, Currency = "TRY", Cost = 4, Stock = 2, Active = true } }, CancellationToken.None, new XmlImportContext { RunId = run, SourceRevision = source.ConfigRevision, ObservedAtUtc = DateTime.UtcNow.AddMinutes(-5) });
            runs.Complete(run, new ImportSummary(1, 0, 0));
            var product = store.Products().Single();
            var profile = profiles.Save("product", product.Id, 3, true, "", Now); Assert.AreEqual(profile.Id, profiles.Save("product", product.Id, 3, true, "aynı", Now).Id, "one profile per scope and id");
            StringAssert.Contains(Assert.ThrowsException<ArgumentException>(() => profiles.Save("store", "etsy", 1, true, "", Now)).Message, "pazaryeri/mağaza");

            // The real dashboard snapshot carries the finding; the ledger adds it once and counts the repeat.
            IEnumerable<AlertInput> Live() => new DashboardDataService(root).Load(bypassCache: true).Notifications.Where(NotificationCenter.IsAlert).Select(NotificationCenter.ToAlert).Where(a => a.Source == LowStockAlerts.Source); // isolate the low-stock alerts from the dashboard's other default notifications
            Assert.IsTrue(Live().Any(a => a.Title == "Düşük stok: SKU-1" && a.Source == LowStockAlerts.Source), "the dashboard raises the low-stock alert");
            var first = ledger.Sync(Live(), Now); Assert.AreEqual(1, first.Added);
            var second = ledger.Sync(Live(), Now.AddMinutes(5)); Assert.AreEqual((0, 1), (second.Added, second.Repeated));
            var alert = ledger.List().Single(a => a.Title == "Düşük stok: SKU-1"); Assert.AreEqual((2, "Open"), (alert.Occurrences, alert.State)); StringAssert.Contains(alert.Detail, "stok 2 ≤ eşik 3");

            // Recovery: the stock rises above the threshold -- the alert resolves on the next sync; a relapse reopens it.
            var edited = store.Products().Single(); edited.Stock = 10; store.SaveProduct(edited);
            Assert.AreEqual(1, ledger.Sync(Live(), Now.AddMinutes(10)).Resolved); Assert.AreEqual("Resolved", ledger.List().Single(a => a.Title == "Düşük stok: SKU-1").State);
            var back = store.Products().Single(); back.Stock = 1; store.SaveProduct(back);
            Assert.AreEqual(1, ledger.Sync(Live(), Now.AddMinutes(15)).Reopened); Assert.AreEqual((1, 3), (ledger.List().Single(a => a.Title == "Düşük stok: SKU-1").Reopened, ledger.List().Single(a => a.Title == "Düşük stok: SKU-1").Occurrences));

            // A disabled profile raises nothing: the open alert resolves; a store profile judges the projection with its holds.
            profiles.Save("product", product.Id, 3, false, "", Now);
            Assert.AreEqual(1, ledger.Sync(Live(), Now.AddMinutes(20)).Resolved);
            store.SaveStockPolicy(new StockPolicy { Channel = "etsy", Shop = "S1", SafetyStock = 0, MaximumStock = null, Enabled = true });
            var again = store.Products().Single(); again.Stock = 10; store.SaveProduct(again); store.Reserve(product.Id, "etsy", "S1", "o-1", 6, Now, TimeSpan.FromHours(48));
            profiles.Save("store", "etsy/S1", 5, true, "", Now);
            var storeAlert = store.LowStockAlerts(Now.AddMinutes(25)).Single(); Assert.AreEqual((4, 5, "etsy|S1"), (storeAlert.Observed, storeAlert.Threshold, storeAlert.StoreKey)); StringAssert.Contains(storeAlert.Detail, "gösterilebilir 4 ≤ eşik 5");
            var third = ledger.Sync(Live(), Now.AddMinutes(25)); Assert.AreEqual(1, third.Added, "a store-scoped alert is its own fingerprint"); Assert.AreEqual(("etsy", "S1"), (ledger.List().Single(a => a.Title == "Düşük stok: SKU-1" && a.IsOpen).Channel, ledger.List().Single(a => a.Title == "Düşük stok: SKU-1" && a.IsOpen).ShopId));

            // Restart: the profiles and the ledger read back.
            SqliteConnection.ClearAllPools();
            Assert.AreEqual(2, new LowStockAlertProfileStore(root).List().Count); Assert.AreEqual(2, new NotificationStore(root).List().Count(a => a.Title == "Düşük stok: SKU-1"));
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
