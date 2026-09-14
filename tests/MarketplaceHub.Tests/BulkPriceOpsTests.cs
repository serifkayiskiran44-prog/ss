using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for issues #1939 (price preview with before/after + revision guard) and
/// #1940 (bounded percentage price adjustment).
[TestClass]
public sealed class BulkPriceOpsTests
{
    static (CatalogStore Catalog, BulkProductOperations Ops, string Root) NewStores()
    {
        var root = Path.Combine(Path.GetTempPath(), "bulk-price-" + Guid.NewGuid().ToString("N"));
        var catalog = new CatalogStore(root);
        return (catalog, new BulkProductOperations(catalog, new ChannelProductsStore(root)), root);
    }

    static CatalogProduct Seed(CatalogStore catalog, string sku, decimal price, bool lockPrice = false)
    {
        var source = new XmlSource { Id = "src", Location = "https://example.test/feed.xml" };
        catalog.SaveSource(source);
        catalog.Import(source, [new CatalogProduct { SourceId = "src", Sku = sku, Name = "Ürün " + sku, Price = price, Currency = "USD" }]);
        var product = catalog.Products().Single(p => p.Sku == sku);
        if (lockPrice) { product.LockPrice = true; catalog.SaveProduct(product); product = catalog.Products().Single(p => p.Sku == sku); }
        return product;
    }

    [TestMethod]
    public void SetPriceShowsOldAndNewValue()
    {
        var (catalog, ops, root) = NewStores();
        try
        {
            var product = Seed(catalog, "SKU-1", 10m);
            var preview = ops.Preview([product], new BulkProductOperationRequest(BulkProductOperationKind.SetPrice, "15.50"));
            var row = preview.Lines.Single();
            Assert.AreEqual("READY", row.Status);
            StringAssert.Contains(row.Before, "10.00");
            StringAssert.Contains(row.After, "15.50");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void InvalidPriceValueIsRejectedAtRequestValidation()
    {
        var (catalog, ops, root) = NewStores();
        try
        {
            var product = Seed(catalog, "SKU-1", 10m);
            Assert.ThrowsException<ArgumentException>(() => ops.Preview([product], new BulkProductOperationRequest(BulkProductOperationKind.SetPrice, "not-a-number")));
            Assert.ThrowsException<ArgumentException>(() => ops.Preview([product], new BulkProductOperationRequest(BulkProductOperationKind.SetPrice, "-5")));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void StaleProductChangedAfterPreviewCancelsApply()
    {
        var (catalog, ops, root) = NewStores();
        try
        {
            var product = Seed(catalog, "SKU-1", 10m);
            var preview = ops.Preview([product], new BulkProductOperationRequest(BulkProductOperationKind.SetPrice, "20"));
            var live = catalog.Products().Single(); live.Name = "changed"; catalog.SaveProduct(live);
            Assert.ThrowsException<InvalidOperationException>(() => ops.Apply(preview, true));
            Assert.AreEqual(10m, catalog.Products().Single().Price, "Stale product must block the price change.");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void CancellingApplyLeavesPriceUnchanged()
    {
        var (catalog, ops, root) = NewStores();
        try
        {
            var product = Seed(catalog, "SKU-1", 10m);
            var preview = ops.Preview([product], new BulkProductOperationRequest(BulkProductOperationKind.SetPrice, "20"));
            using var cts = new System.Threading.CancellationTokenSource(); cts.Cancel();
            Assert.ThrowsException<OperationCanceledException>(() => ops.Apply(preview, true, cts.Token));
            Assert.AreEqual(10m, catalog.Products().Single().Price);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void PercentIncreaseComputesCorrectNewPrice()
    {
        var (catalog, ops, root) = NewStores();
        try
        {
            var product = Seed(catalog, "SKU-1", 100m);
            var preview = ops.Preview([product], new BulkProductOperationRequest(BulkProductOperationKind.AdjustPricePercent, "10"));
            var applied = ops.Apply(preview, true);
            Assert.AreEqual(1, applied.Applied);
            Assert.AreEqual(110m, catalog.Products().Single().Price);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void PercentDecreaseComputesCorrectNewPrice()
    {
        var (catalog, ops, root) = NewStores();
        try
        {
            var product = Seed(catalog, "SKU-1", 100m);
            var preview = ops.Preview([product], new BulkProductOperationRequest(BulkProductOperationKind.AdjustPricePercent, "-25"));
            ops.Apply(preview, true);
            Assert.AreEqual(75m, catalog.Products().Single().Price);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ZeroPercentIsANoOpAndSkipped()
    {
        var (catalog, ops, root) = NewStores();
        try
        {
            var product = Seed(catalog, "SKU-1", 100m);
            var preview = ops.Preview([product], new BulkProductOperationRequest(BulkProductOperationKind.AdjustPricePercent, "0"));
            Assert.AreEqual("SKIP", preview.Lines.Single().Status);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void OneHundredPercentIncreaseDoublesPrice()
    {
        var (catalog, ops, root) = NewStores();
        try
        {
            var product = Seed(catalog, "SKU-1", 50m);
            var preview = ops.Preview([product], new BulkProductOperationRequest(BulkProductOperationKind.AdjustPricePercent, "100"));
            ops.Apply(preview, true);
            Assert.AreEqual(100m, catalog.Products().Single().Price);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MinusOneHundredPercentIsRejectedAtValidation()
    {
        var (catalog, ops, root) = NewStores();
        try
        {
            var product = Seed(catalog, "SKU-1", 50m);
            Assert.ThrowsException<ArgumentException>(() => ops.Preview([product], new BulkProductOperationRequest(BulkProductOperationKind.AdjustPricePercent, "-100")));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ExtremeOverflowPercentProducesErrorRowNotACrash()
    {
        var (catalog, ops, root) = NewStores();
        try
        {
            var product = Seed(catalog, "SKU-1", decimal.MaxValue / 2);
            var preview = ops.Preview([product], new BulkProductOperationRequest(BulkProductOperationKind.AdjustPricePercent, "1000000000000000000"));
            var row = preview.Lines.Single();
            Assert.AreEqual("ERROR", row.Status);
            var result = ops.Apply(preview, true);
            Assert.AreEqual(0, result.Applied);
            Assert.AreEqual(decimal.MaxValue / 2, catalog.Products().Single().Price, "An overflow row must never be applied.");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void PriceLockedProductIsSkippedNotOverwritten()
    {
        var (catalog, ops, root) = NewStores();
        try
        {
            var product = Seed(catalog, "SKU-1", 10m, lockPrice: true);
            var preview = ops.Preview([product], new BulkProductOperationRequest(BulkProductOperationKind.SetPrice, "50"));
            Assert.AreEqual("SKIP", preview.Lines.Single().Status);
            var result = ops.Apply(preview, true);
            Assert.AreEqual(0, result.Applied);
            Assert.AreEqual(10m, catalog.Products().Single().Price);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
