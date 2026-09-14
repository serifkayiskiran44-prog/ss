using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for #2598 (stale channel-plan revision must block Apply) and
/// #2597 (bulk channel-mapping commit must be a single all-or-nothing
/// transaction).
[TestClass]
public sealed class BulkChannelMappingConcurrencyTests
{
    static (CatalogStore Catalog, ChannelProductsStore Plans, BulkProductOperations Ops) NewSetup(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "bulk-channel-" + Guid.NewGuid().ToString("N"));
        var catalog = new CatalogStore(root);
        var plans = new ChannelProductsStore(root);
        return (catalog, plans, new BulkProductOperations(catalog, plans));
    }

    static CatalogProduct Product(string sku) => new() { Sku = sku, Name = "Ürün " + sku, Price = 10, Stock = 1 };

    static BulkProductOperationRequest Request() => new(BulkProductOperationKind.SetChannelMapping, Channel: "etsy", ShopId: "shop1", ListingId: "L1", TargetCategory: "Cat1");

    [TestMethod]
    public void ApplySucceedsWhenNothingChangedSincePreview()
    {
        var (catalog, plans, ops) = NewSetup(out var root);
        try
        {
            var product = catalog.CreateManual(Product("SKU-1"));
            var preview = ops.Preview(new[] { product }, Request());
            var result = ops.Apply(preview, true);
            Assert.AreEqual(1, result.Applied);
            Assert.AreEqual("L1", plans.Find("etsy", "shop1", product.Id)!.ListingId);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void PlanChangedByAnotherProcessAfterPreviewBlocksApply()
    {
        var (catalog, plans, ops) = NewSetup(out var root);
        try
        {
            var product = catalog.CreateManual(Product("SKU-1"));
            var preview = ops.Preview(new[] { product }, Request());

            // Another process saves a competing plan for the same key in the meantime.
            plans.Save(new ChannelProductPlan { ChannelId = "etsy", ShopId = "shop1", ProductId = product.Id, ListingId = "OTHER", Currency = "USD" });

            Assert.ThrowsException<InvalidOperationException>(() => ops.Apply(preview, true));
            Assert.AreEqual("OTHER", plans.Find("etsy", "shop1", product.Id)!.ListingId, "The competing plan must not be overwritten by the stale preview.");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void PlanCreatedAfterPreviewWhenNonePreviouslyExistedBlocksApply()
    {
        var (catalog, plans, ops) = NewSetup(out var root);
        try
        {
            var product = catalog.CreateManual(Product("SKU-1"));
            var preview = ops.Preview(new[] { product }, Request()); // no existing plan -> Version 0 expected

            plans.Save(new ChannelProductPlan { ChannelId = "etsy", ShopId = "shop1", ProductId = product.Id, ListingId = "RACE", Currency = "USD" });

            Assert.ThrowsException<InvalidOperationException>(() => ops.Apply(preview, true));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MultiRowBatchWithOneStaleRowStopsTheWholeAtomicBatch()
    {
        var (catalog, plans, ops) = NewSetup(out var root);
        try
        {
            var p1 = catalog.CreateManual(Product("SKU-1"));
            var p2 = catalog.CreateManual(Product("SKU-2"));
            var preview = ops.Preview(new[] { p1, p2 }, Request());

            // Make p2's plan stale by saving a competing plan for it only.
            plans.Save(new ChannelProductPlan { ChannelId = "etsy", ShopId = "shop1", ProductId = p2.Id, ListingId = "RACE", Currency = "USD" });

            Assert.ThrowsException<InvalidOperationException>(() => ops.Apply(preview, true));

            // p1's plan must not have been written either - all-or-nothing.
            Assert.IsNull(plans.Find("etsy", "shop1", p1.Id));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void SuccessfulBatchAppliesAllRowsTogether()
    {
        var (catalog, plans, ops) = NewSetup(out var root);
        try
        {
            var p1 = catalog.CreateManual(Product("SKU-1"));
            var p2 = catalog.CreateManual(Product("SKU-2"));
            var preview = ops.Preview(new[] { p1, p2 }, Request());
            var result = ops.Apply(preview, true);

            Assert.AreEqual(2, result.Applied);
            Assert.IsNotNull(plans.Find("etsy", "shop1", p1.Id));
            Assert.IsNotNull(plans.Find("etsy", "shop1", p2.Id));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void SaveBatchRejectsStaleVersionWithoutWritingAnyRow()
    {
        var (catalog, plans, _) = NewSetup(out var root);
        try
        {
            var healthy = new ChannelProductPlan { ChannelId = "etsy", ShopId = "shop1", ProductId = "p1", ListingId = "A", Currency = "USD" };
            var staleVersion = new ChannelProductPlan { ChannelId = "etsy", ShopId = "shop1", ProductId = "p2", ListingId = "B", Currency = "USD", Version = 5 };

            Assert.ThrowsException<InvalidOperationException>(() => plans.SaveBatch(new[] { healthy, staleVersion }));
            Assert.IsNull(plans.Find("etsy", "shop1", "p1"), "The first (otherwise valid) row must not persist when a later row in the same batch fails.");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ReapplyingTheSamePreviewASecondTimeIsRejectedNotDuplicated()
    {
        var (catalog, plans, ops) = NewSetup(out var root);
        try
        {
            var product = catalog.CreateManual(Product("SKU-1"));
            var preview = ops.Preview(new[] { product }, Request());
            ops.Apply(preview, true);

            // Re-applying the exact same (now stale) preview must fail, not silently
            // re-save/duplicate - the plan's version has already moved on.
            Assert.ThrowsException<InvalidOperationException>(() => ops.Apply(preview, true));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestartPreservesCommittedBatchState()
    {
        var root = Path.Combine(Path.GetTempPath(), "bulk-channel-" + Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = new CatalogStore(root);
            var plans = new ChannelProductsStore(root);
            var ops = new BulkProductOperations(catalog, plans);
            var product = catalog.CreateManual(Product("SKU-1"));
            var preview = ops.Preview(new[] { product }, Request());
            ops.Apply(preview, true);

            var reopened = new ChannelProductsStore(root);
            Assert.AreEqual("L1", reopened.Find("etsy", "shop1", product.Id)!.ListingId);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
