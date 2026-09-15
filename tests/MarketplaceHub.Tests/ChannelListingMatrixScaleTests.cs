using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for #2596: ChannelListingMatrixService.Build() must use an
/// indexed latest-sync lookup (O(1) per row) instead of re-filtering and
/// re-sorting the whole sync list for every product/connection pair, while
/// preserving the exact same latest-sync selection semantics.
[TestClass]
public sealed class ChannelListingMatrixScaleTests
{
    static string NewRoot() => Path.Combine(Path.GetTempPath(), "matrix-scale-" + Guid.NewGuid().ToString("N"));

    static void WithRoot(Action<string> test)
    {
        var root = NewRoot();
        try { test(root); }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    static CatalogProduct Seed(CatalogStore catalog, string sku) =>
        catalog.CreateManual(new CatalogProduct { Sku = sku, Name = "Ürün " + sku, Price = 10, Currency = "USD" });

    [TestMethod]
    public void LargeCatalogAndSyncHistoryStillPicksTheCorrectLatestPerRow()
    {
        WithRoot(root =>
        {
            var catalog = new CatalogStore(root);
            var products = Enumerable.Range(0, 300).Select(i => Seed(catalog, "SKU-" + i)).ToList();
            var connections = new MarketplaceConnectionStore(root);
            connections.Save("etsy", "shop-a", "Shop A", true);

            var sync = new SyncStore(root);
            // Give every product a long history of superseded jobs plus one
            // genuinely-latest one, to exercise the indexed lookup at scale.
            foreach (var product in products)
            {
                for (var v = 0; v < 5; v++)
                {
                    var job = sync.Enqueue(new SyncRequest("etsy", "update", product.Id, "v" + v, "shop-a"));
                    sync.TryStart(job.Id, out var generation);
                    if (v == 4) sync.Fail(job.Id, generation, "final failure"); else sync.Succeed(job.Id, generation);
                }
            }

            var rows = new ChannelListingMatrixService(root).Build().Where(r => r.ShopId == "shop-a").ToList();
            Assert.AreEqual(300, rows.Count);
            foreach (var row in rows) Assert.AreEqual("Failed", row.SyncStatus, $"Product {row.ProductId} must reflect its most recent (v4, Failed) sync job.");
        });
    }

    [TestMethod]
    public void DisabledConnectionContributesNoRowsWithoutError()
    {
        WithRoot(root =>
        {
            var catalog = new CatalogStore(root);
            Seed(catalog, "SKU-1");
            var connections = new MarketplaceConnectionStore(root);
            var saved = connections.Save("etsy", "shop-a", "Shop A", true);
            connections.SetEnabled(saved.Id, false);
            var rows = new ChannelListingMatrixService(root).Build();
            Assert.IsFalse(rows.Any(r => r.ShopId == "shop-a"));
        });
    }

    [TestMethod]
    public void ProductWithoutAPlanIsMissingRegardlessOfSyncHistory()
    {
        WithRoot(root =>
        {
            var catalog = new CatalogStore(root);
            var product = Seed(catalog, "SKU-1");
            new MarketplaceConnectionStore(root).Save("etsy", "shop-a", "Shop A", true);
            var sync = new SyncStore(root);
            var job = sync.Enqueue(new SyncRequest("etsy", "update", product.Id, "v1", "shop-a"));
            sync.TryStart(job.Id, out var generation); sync.Succeed(job.Id, generation);

            var row = new ChannelListingMatrixService(root).Build().Single(r => r.ShopId == "shop-a");
            Assert.AreEqual("MISSING", row.MappingStatus, "No channel plan means MISSING regardless of unrelated sync history for the same product id.");
        });
    }

    [TestMethod]
    public void EqualSyncTimestampsStillProduceADeterministicSingleWinner()
    {
        WithRoot(root =>
        {
            var catalog = new CatalogStore(root);
            var product = Seed(catalog, "SKU-1");
            new MarketplaceConnectionStore(root).Save("etsy", "shop-a", "Shop A", true);
            var plans = new ChannelProductsStore(root);
            plans.Save(new ChannelProductPlan { ChannelId = "etsy", ShopId = "shop-a", ProductId = product.Id, ListingId = "L1", Currency = "USD" });

            var sync = new SyncStore(root);
            var byProductJob = sync.Enqueue(new SyncRequest("etsy", "update", product.Id, "v1", "shop-a"));
            sync.TryStart(byProductJob.Id, out var g1); sync.Succeed(byProductJob.Id, g1);
            var byListingJob = sync.Enqueue(new SyncRequest("etsy", "update", "L1", "v1", "shop-a"));
            sync.TryStart(byListingJob.Id, out var g2); sync.Succeed(byListingJob.Id, g2);

            // Both jobs succeeded; the exact tie-break just needs to be
            // deterministic and never throw/duplicate the row.
            var rows = new ChannelListingMatrixService(root).Build().Where(r => r.ShopId == "shop-a").ToList();
            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual("SYNCED", rows.Single().MappingStatus);
        });
    }

    [TestMethod]
    public void LatestSyncSelectionMatchesTheOldSemanticsAcrossProductAndListingKeys()
    {
        WithRoot(root =>
        {
            var catalog = new CatalogStore(root);
            var product = Seed(catalog, "SKU-1");
            new MarketplaceConnectionStore(root).Save("etsy", "shop-a", "Shop A", true);
            var plans = new ChannelProductsStore(root);
            plans.Save(new ChannelProductPlan { ChannelId = "etsy", ShopId = "shop-a", ProductId = product.Id, ListingId = "L1", Currency = "USD" });

            var sync = new SyncStore(root);
            // Older job keyed by product id.
            var older = sync.Enqueue(new SyncRequest("etsy", "update", product.Id, "v1", "shop-a"));
            sync.TryStart(older.Id, out var g1); sync.Succeed(older.Id, g1);
            // Newer job keyed by the plan's listing id - must win because it's
            // the most recent, even though it's under a different entity key.
            var newer = sync.Enqueue(new SyncRequest("etsy", "update", "L1", "v1", "shop-a"));
            sync.TryStart(newer.Id, out var g2); sync.Fail(newer.Id, g2, "listing-keyed failure");

            var row = new ChannelListingMatrixService(root).Build().Single(r => r.ShopId == "shop-a");
            Assert.AreEqual("ERROR", row.MappingStatus);
            Assert.AreEqual("listing-keyed failure", row.LastError);
        });
    }
}
