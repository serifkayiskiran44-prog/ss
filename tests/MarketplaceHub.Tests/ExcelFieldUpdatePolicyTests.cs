using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class ExcelFieldUpdatePolicyTests
{
    [TestMethod]
    public void MergeUpdatesOnlySelectedFieldsAndKeepsXmlLockedValues()
    {
        var current = new CatalogProduct { Id = "p1", Sku = "SKU-1", Barcode = "B-1", Name = "XML adı", Price = 100, Stock = 4, LockName = true, LockStock = true };
        var excel = new CatalogProduct { Id = "p1", Sku = "SKU-1", Barcode = "B-1", Name = "Excel adı", Price = 125, Stock = 9 };

        var merged = ExcelFieldUpdatePolicy.Merge(current, excel, ["Name", "Price", "Stock"]);

        Assert.AreEqual("XML adı", merged.Name);
        Assert.AreEqual(125m, merged.Price);
        Assert.AreEqual(4, merged.Stock);
    }
}
