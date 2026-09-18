using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

[TestClass]
public class ProductWorkspaceFilterTests
{
    [TestMethod]
    public void RangesCategoryBranchAndBarcodeFilterIntersectAcrossPages()
    {
        var dir = Path.Combine(Path.GetTempPath(), "product-filters-" + Guid.NewGuid().ToString("N")); var store = new CatalogStore(dir);
        store.CreateManual(new() { Name = "Cat A", Sku = "A", Barcode = "10", Category = "Pets > Cat", Price = 100, Stock = 5, Currency = "TRY", LockStock = true });
        store.CreateManual(new() { Name = "Dog B", Sku = "B", Barcode = "20", Category = "Pets>Dog", Price = 200, Stock = 10, Currency = "TRY", LockStock = true });
        store.CreateManual(new() { Name = "Toy C", Sku = "C", Barcode = "30", Category = "Toys", Price = 150, Stock = 7, Currency = "USD" });
        var filter = new CatalogFilter { CategoryPrefix = "Pets", MinimumStock = 5, MaximumStock = 10, MinimumPrice = 90, MaximumPrice = 200, Currency = "TRY", StockLocked = true, MinimumId = 1, MaximumId = 2 };
        var page = store.Search("", 0, 1, filter); Assert.AreEqual(2, page.Total); Assert.AreEqual("A", page.Items.Single().Sku);
        Assert.AreEqual("B", store.Search("", 1, 1, filter).Items.Single().Sku);
        Assert.AreEqual("B", store.Search("", 0, 100, filter with { Barcodes = ["20"] }).Items.Single().Sku);
        var filters = new CatalogFilterStore(dir); filters.Save("Pets", filter);
        Assert.AreEqual(2, store.Search("", 0, 100, filters.List().Single().Filter).Total);
    }

    [TestMethod]
    public void InvertedRangesAreRejectedAndCategoryWildcardsAreLiteral()
    {
        var store = new CatalogStore(Path.Combine(Path.GetTempPath(), "product-filters-" + Guid.NewGuid().ToString("N")));
        store.CreateManual(new() { Sku = "A", Name = "One", Category = "Pets>Cat" });
        Assert.ThrowsException<ArgumentException>(() => store.Search("", filter: new() { MinimumStock = 10, MaximumStock = 1 }));
        Assert.AreEqual(0, store.Search("", filter: new() { CategoryPrefix = "%" }).Total);
    }
}
