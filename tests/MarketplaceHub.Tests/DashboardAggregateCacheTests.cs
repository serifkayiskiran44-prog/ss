using System;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #783 (PERFORMANCE: Dashboard aggregate query cache). ENTRY: dashboard load (DashboardPanel -> DashboardDataService.Load).
// The cache is judged by identity: a served-from-cache load returns the very same immutable snapshot instance,
// a rebuilt load returns a new one. That is deterministic and machine-speed independent, unlike timing.
[TestClass]
public sealed class DashboardAggregateCacheTests
{
    static string NewRoot() => Path.Combine(Path.GetTempPath(), "dash-cache-" + Guid.NewGuid().ToString("N"));
    static void Cleanup(string root) { DashboardDataService.InvalidateCache(root); SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }

    static CatalogStore SeedOneProduct(string root)
    {
        var catalog = new CatalogStore(root);
        var source = new XmlSource { Id = "src", Name = "Src" };
        catalog.Import(source, new[] { new CatalogProduct { SourceId = source.Id, Sku = "A", Name = "A", Price = 10, Stock = 1 } });
        return catalog;
    }

    [TestMethod]
    public void AWarmLoadWithNoDataChangeReusesTheSnapshotInsteadOfReaggregatingEveryStore()
    {
        var root = NewRoot();
        try
        {
            SeedOneProduct(root);

            // Two service instances: every navigation to the dashboard constructs a fresh one, so a cache that
            // only lives inside one instance would never be warm on the real entry path.
            var cold = new DashboardDataService(root).Load();
            var warm = new DashboardDataService(root).Load();

            Assert.AreEqual(1, cold.TotalProducts);
            Assert.IsTrue(ReferenceEquals(cold, warm), "Nothing changed between the two loads, so the second must be served from the cache (same snapshot instance), not re-aggregated from every store.");
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void AProductImportOrASourceChangeInvalidatesTheCachedSnapshotImmediately()
    {
        var root = NewRoot();
        try
        {
            var catalog = SeedOneProduct(root);
            var service = new DashboardDataService(root);
            var before = service.Load();
            Assert.AreEqual(1, before.TotalProducts);
            Assert.AreEqual(1, before.XmlSources);

            // A second supplier's first feed (a fresh source has no previous run, so the dropship anomaly gate
            // has nothing to compare against and lets the +100% product count through).
            catalog.Import(new XmlSource { Id = "src-2", Name = "Second supplier" }, new[] { new CatalogProduct { SourceId = "src-2", Sku = "B", Name = "B", Price = 10, Stock = 1 } });
            var afterProducts = service.Load();
            Assert.IsFalse(ReferenceEquals(before, afterProducts), "A committed product write must invalidate the cached snapshot.");
            Assert.AreEqual(2, afterProducts.TotalProducts);
            Assert.AreEqual(2, afterProducts.XmlSources);

            catalog.SaveSource(new XmlSource { Id = "src-3", Name = "Third supplier" });
            var afterSource = service.Load();
            Assert.IsFalse(ReferenceEquals(afterProducts, afterSource), "A source change must invalidate the cached snapshot.");
            Assert.AreEqual(3, afterSource.XmlSources);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void SwitchingStoresServesEachDirectoryItsOwnSnapshotAndSwitchingBackIsStillWarm()
    {
        var rootA = NewRoot(); var rootB = NewRoot();
        try
        {
            SeedOneProduct(rootA);
            new CatalogStore(rootB); // an empty second store

            var a = new DashboardDataService(rootA).Load();
            var b = new DashboardDataService(rootB).Load();
            var aAgain = new DashboardDataService(rootA).Load();

            Assert.AreEqual(1, a.TotalProducts);
            Assert.AreEqual(0, b.TotalProducts, "A different store must never be served the other store's snapshot.");
            Assert.IsTrue(ReferenceEquals(a, aAgain), "Switching to another store and back must not evict the first store's still-valid snapshot.");
        }
        finally { Cleanup(rootA); Cleanup(rootB); }
    }

    [TestMethod]
    public void ASnapshotOlderThanMaxAgeIsRebuiltEvenWhenNothingOnDiskChanged()
    {
        var root = NewRoot();
        try
        {
            SeedOneProduct(root);
            var now = new DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc);
            var service = new DashboardDataService(root, TimeSpan.FromSeconds(10), () => now);

            var first = service.Load();
            now = now.AddSeconds(5);
            var withinMaxAge = service.Load();
            now = now.AddSeconds(6);
            var pastMaxAge = service.Load();

            Assert.IsTrue(ReferenceEquals(first, withinMaxAge));
            Assert.IsFalse(ReferenceEquals(first, pastMaxAge), "The cache is short-lived by contract: past MaxAge it must rebuild even without a detected change.");
            Assert.AreEqual(first.TotalProducts, pastMaxAge.TotalProducts);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void AnExplicitRefreshBypassesTheCacheAndBecomesTheNewCachedSnapshot()
    {
        var root = NewRoot();
        try
        {
            SeedOneProduct(root);
            var service = new DashboardDataService(root);

            var cached = service.Load();
            var forced = service.Load(bypassCache: true);
            var afterForced = service.Load();

            Assert.IsFalse(ReferenceEquals(cached, forced), "The user's explicit refresh must always re-read the stores.");
            Assert.IsTrue(ReferenceEquals(forced, afterForced), "The forced rebuild replaces the cache entry.");
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void AFailedLoadPropagatesIsNeverCachedAndNeverMasksACorruptedStoreWithAStaleSnapshot()
    {
        var root = NewRoot();
        try
        {
            SeedOneProduct(root);
            var service = new DashboardDataService(root);
            var good = service.Load();
            Assert.AreEqual(1, good.TotalProducts);

            // The store is destroyed underneath a warm cache: the next load must surface the failure, not the stale snapshot.
            SqliteConnection.ClearAllPools();
            File.WriteAllText(Path.Combine(root, "catalog.db"), new string('x', 4096));
            Assert.ThrowsException<SqliteException>(() => service.Load());

            // The failure was not cached: once the store is valid again, the next load succeeds from scratch.
            SqliteConnection.ClearAllPools();
            File.Delete(Path.Combine(root, "catalog.db"));
            new CatalogStore(root);
            var recovered = service.Load();
            Assert.AreEqual(0, recovered.TotalProducts);
            Assert.IsFalse(ReferenceEquals(good, recovered));
        }
        finally { Cleanup(root); }
    }
}
