using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for issues #1947 (stock preview with before/after + revision guard) and
/// #1948 (bounded +N/-N stock delta, separate from absolute set).
[TestClass]
public sealed class BulkStockOpsTests
{
    static (CatalogStore Catalog, BulkProductOperations Ops, string Root) NewStores()
    {
        var root = Path.Combine(Path.GetTempPath(), "bulk-stock-" + Guid.NewGuid().ToString("N"));
        var catalog = new CatalogStore(root);
        return (catalog, new BulkProductOperations(catalog, new ChannelProductsStore(root)), root);
    }

    static CatalogProduct Seed(CatalogStore catalog, string sku, int stock, bool lockStock = false)
    {
        var source = new XmlSource { Id = "src", Location = "https://example.test/feed.xml" };
        catalog.SaveSource(source);
        catalog.Import(source, [new CatalogProduct { SourceId = "src", Sku = sku, Name = "Ürün " + sku, Price = 10, Currency = "USD", Stock = stock }]);
        var product = catalog.Products().Single(p => p.Sku == sku);
        if (lockStock) { product.LockStock = true; catalog.SaveProduct(product); product = catalog.Products().Single(p => p.Sku == sku); }
        return product;
    }

    [TestMethod]
    public void SetStockToZeroShowsOldAndNewValue()
    {
        var (catalog, ops, root) = NewStores();
        try
        {
            var product = Seed(catalog, "SKU-1", 5);
            var preview = ops.Preview([product], new BulkProductOperationRequest(BulkProductOperationKind.SetStock, "0"));
            var row = preview.Lines.Single();
            Assert.AreEqual("READY", row.Status);
            Assert.AreEqual("5", row.Before); Assert.AreEqual("0", row.After);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void SetStockToPositiveValueApplies()
    {
        var (catalog, ops, root) = NewStores();
        try
        {
            var product = Seed(catalog, "SKU-1", 5);
            var preview = ops.Preview([product], new BulkProductOperationRequest(BulkProductOperationKind.SetStock, "42"));
            ops.Apply(preview, true);
            Assert.AreEqual(42, catalog.Products().Single().Stock);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void InvalidNegativeSetStockIsRejectedAtValidation()
    {
        var (catalog, ops, root) = NewStores();
        try
        {
            var product = Seed(catalog, "SKU-1", 5);
            Assert.ThrowsException<ArgumentException>(() => ops.Preview([product], new BulkProductOperationRequest(BulkProductOperationKind.SetStock, "-3")));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void StaleRecordBlocksStockApply()
    {
        var (catalog, ops, root) = NewStores();
        try
        {
            var product = Seed(catalog, "SKU-1", 5);
            var preview = ops.Preview([product], new BulkProductOperationRequest(BulkProductOperationKind.SetStock, "9"));
            var live = catalog.Products().Single(); live.Name = "changed"; catalog.SaveProduct(live);
            Assert.ThrowsException<InvalidOperationException>(() => ops.Apply(preview, true));
            Assert.AreEqual(5, catalog.Products().Single().Stock);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void CancelBeforeCommitLeavesStockUnchanged()
    {
        var (catalog, ops, root) = NewStores();
        try
        {
            var product = Seed(catalog, "SKU-1", 5);
            var preview = ops.Preview([product], new BulkProductOperationRequest(BulkProductOperationKind.SetStock, "9"));
            using var cts = new System.Threading.CancellationTokenSource(); cts.Cancel();
            Assert.ThrowsException<OperationCanceledException>(() => ops.Apply(preview, true, cts.Token));
            Assert.AreEqual(5, catalog.Products().Single().Stock);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void PositiveDeltaIncreasesStock()
    {
        var (catalog, ops, root) = NewStores();
        try
        {
            var product = Seed(catalog, "SKU-1", 5);
            var preview = ops.Preview([product], new BulkProductOperationRequest(BulkProductOperationKind.AdjustStockDelta, "3"));
            ops.Apply(preview, true);
            Assert.AreEqual(8, catalog.Products().Single().Stock);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void NegativeDeltaDecreasesStock()
    {
        var (catalog, ops, root) = NewStores();
        try
        {
            var product = Seed(catalog, "SKU-1", 5);
            var preview = ops.Preview([product], new BulkProductOperationRequest(BulkProductOperationKind.AdjustStockDelta, "-3"));
            ops.Apply(preview, true);
            Assert.AreEqual(2, catalog.Products().Single().Stock);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DeltaBelowZeroProducesErrorRowAndIsNeverApplied()
    {
        var (catalog, ops, root) = NewStores();
        try
        {
            var product = Seed(catalog, "SKU-1", 5);
            var preview = ops.Preview([product], new BulkProductOperationRequest(BulkProductOperationKind.AdjustStockDelta, "-10"));
            var row = preview.Lines.Single();
            Assert.AreEqual("ERROR", row.Status);
            var result = ops.Apply(preview, true);
            Assert.AreEqual(0, result.Applied);
            Assert.AreEqual(5, catalog.Products().Single().Stock, "A row that would go below zero must never be applied.");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DeltaCausingIntOverflowProducesErrorRow()
    {
        var (catalog, ops, root) = NewStores();
        try
        {
            var product = Seed(catalog, "SKU-1", int.MaxValue - 1);
            var preview = ops.Preview([product], new BulkProductOperationRequest(BulkProductOperationKind.AdjustStockDelta, "100"));
            var row = preview.Lines.Single();
            Assert.AreEqual("ERROR", row.Status);
            var result = ops.Apply(preview, true);
            Assert.AreEqual(0, result.Applied);
            Assert.AreEqual(int.MaxValue - 1, catalog.Products().Single().Stock);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void StaleRecordBlocksDeltaApply()
    {
        var (catalog, ops, root) = NewStores();
        try
        {
            var product = Seed(catalog, "SKU-1", 5);
            var preview = ops.Preview([product], new BulkProductOperationRequest(BulkProductOperationKind.AdjustStockDelta, "5"));
            var live = catalog.Products().Single(); live.Name = "changed"; catalog.SaveProduct(live);
            Assert.ThrowsException<InvalidOperationException>(() => ops.Apply(preview, true));
            Assert.AreEqual(5, catalog.Products().Single().Stock);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void StockLockedProductIsSkippedForBothSetAndDelta()
    {
        var (catalog, ops, root) = NewStores();
        try
        {
            var product = Seed(catalog, "SKU-1", 5, lockStock: true);
            var setPreview = ops.Preview([product], new BulkProductOperationRequest(BulkProductOperationKind.SetStock, "50"));
            Assert.AreEqual("SKIP", setPreview.Lines.Single().Status);
            var deltaPreview = ops.Preview([product], new BulkProductOperationRequest(BulkProductOperationKind.AdjustStockDelta, "5"));
            Assert.AreEqual("SKIP", deltaPreview.Lines.Single().Status);
            Assert.AreEqual(5, catalog.Products().Single().Stock);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
