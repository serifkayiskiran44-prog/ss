using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for #2664: ChannelListingMatrixService.Build() must attribute the
/// latest sync status to the exact (Channel, ShopId) of the connection row -
/// before the fix, the lookup filtered only by Channel + EntityId/ListingId,
/// so two stores on the same marketplace could leak each other's sync state.
[TestClass]
public sealed class ChannelListingMatrixStoreIsolationTests
{
    static string NewRoot() => Path.Combine(Path.GetTempPath(), "matrix-isolation-" + Guid.NewGuid().ToString("N"));

    static void WithRoot(Action<string> test)
    {
        var root = NewRoot();
        try { test(root); }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    static CatalogProduct Seed(CatalogStore catalog, string sku) =>
        catalog.CreateManual(new CatalogProduct { Sku = sku, Name = "Ürün " + sku, Price = 10, Currency = "USD" });

    [TestMethod]
    public void FailedSyncInShopADoesNotLeakIntoShopBForSameChannelAndProduct() => WithRoot(root =>
    {
        var catalog = new CatalogStore(root);
        var product = Seed(catalog, "SKU-1");
        var connections = new MarketplaceConnectionStore(root);
        connections.Save("etsy", "shop-a", "Shop A", true);
        connections.Save("etsy", "shop-b", "Shop B", true);

        var sync = new SyncStore(root);
        var jobA = sync.Enqueue(new SyncRequest("etsy", "update", product.Id, "v1", "shop-a"));
        sync.TryStart(jobA.Id, out var generationA);
        sync.Fail(jobA.Id, generationA, "eBay API 500");

        var rows = new ChannelListingMatrixService(root).Build();
        var rowA = rows.Single(r => r.ShopId == "shop-a" && r.ProductId == product.Id);
        var rowB = rows.Single(r => r.ShopId == "shop-b" && r.ProductId == product.Id);

        Assert.AreEqual("Failed", rowA.SyncStatus);
        Assert.AreEqual("None", rowB.SyncStatus, "Shop B must never inherit Shop A's sync status for the same channel/product.");
        Assert.AreEqual("", rowB.LastError);
    });

    [TestMethod]
    public void ShopWithNoSyncStaysNoneEvenWhenAnotherShopHasManyRuns() => WithRoot(root =>
    {
        var catalog = new CatalogStore(root);
        var product = Seed(catalog, "SKU-2");
        var connections = new MarketplaceConnectionStore(root);
        connections.Save("ozon", "shop-a", "Shop A", true);
        connections.Save("ozon", "shop-b", "Shop B", true);

        var sync = new SyncStore(root);
        for (var i = 0; i < 3; i++)
        {
            var job = sync.Enqueue(new SyncRequest("ozon", "update", product.Id, "v" + i, "shop-a"));
            sync.TryStart(job.Id, out var generation);
            sync.Succeed(job.Id, generation);
        }

        var rows = new ChannelListingMatrixService(root).Build();
        var rowB = rows.Single(r => r.ShopId == "shop-b" && r.ProductId == product.Id);
        Assert.AreEqual("None", rowB.SyncStatus);
        Assert.IsNull(rowB.LastSyncUtc);
    });

    [TestMethod]
    public void SameListingIdReusedAcrossShopsStaysIsolated() => WithRoot(root =>
    {
        var catalog = new CatalogStore(root);
        var productA = Seed(catalog, "SKU-A");
        var productB = Seed(catalog, "SKU-B");
        var connections = new MarketplaceConnectionStore(root);
        connections.Save("etsy", "shop-a", "Shop A", true);
        connections.Save("etsy", "shop-b", "Shop B", true);

        var plans = new ChannelProductsStore(root);
        plans.Save(new ChannelProductPlan { ChannelId = "etsy", ShopId = "shop-a", ProductId = productA.Id, ListingId = "999", Currency = "USD" });
        plans.Save(new ChannelProductPlan { ChannelId = "etsy", ShopId = "shop-b", ProductId = productB.Id, ListingId = "999", Currency = "USD" });

        var sync = new SyncStore(root);
        var job = sync.Enqueue(new SyncRequest("etsy", "update", "999", "v1", "shop-a"));
        sync.TryStart(job.Id, out var generation);
        sync.Fail(job.Id, generation, "shop-a failure");

        var rows = new ChannelListingMatrixService(root).Build();
        var rowA = rows.Single(r => r.ShopId == "shop-a" && r.ProductId == productA.Id);
        var rowB = rows.Single(r => r.ShopId == "shop-b" && r.ProductId == productB.Id);

        Assert.AreEqual("Failed", rowA.SyncStatus);
        Assert.AreEqual("None", rowB.SyncStatus, "A listing id reused across shops must not cross-attribute sync state.");
    });

    [TestMethod]
    public void DisabledConnectionsRemainExcludedFromTheMatrix() => WithRoot(root =>
    {
        var catalog = new CatalogStore(root);
        Seed(catalog, "SKU-3");
        var connections = new MarketplaceConnectionStore(root);
        var saved = connections.Save("etsy", "shop-c", "Shop C", true);
        connections.SetEnabled(saved.Id, false);

        var rows = new ChannelListingMatrixService(root).Build();
        Assert.IsFalse(rows.Any(r => r.ShopId == "shop-c"));
    });
}
