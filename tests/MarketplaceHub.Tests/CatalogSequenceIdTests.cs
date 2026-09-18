using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

[TestClass]
public class CatalogSequenceIdTests
{
    [TestMethod]
    public void AutomaticNumbersAreSequentialStableAndSharedByBrandAndCategory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sequence-" + Guid.NewGuid().ToString("N"));
        var store = new CatalogStore(directory);
        var one = store.CreateManual(new() { Sku = "A", Name = "First", Brand = "Petshop", Category = "Pets > Cat" });
        var two = store.CreateManual(new() { Sku = "B", Name = "Second", Brand = "petshop", Category = "Pets > Cat" });
        Assert.AreEqual(1L, one.LocalNumber); Assert.AreEqual(2L, two.LocalNumber);
        Assert.AreEqual(one.BrandNumber, two.BrandNumber); Assert.AreEqual(one.CategoryNumber, two.CategoryNumber);
        Assert.AreEqual("1", one.ProductIdLabel);
        store.DeleteProduct(two);
        var reopened = new CatalogStore(directory);
        var next = reopened.CreateManual(new() { Sku = "C", Name = "Third", Brand = "Another", Category = "Pets > Dog" });
        Assert.AreEqual(3L, next.LocalNumber); Assert.AreEqual(2L, next.BrandNumber); Assert.AreEqual(2L, next.CategoryNumber);
        Assert.AreEqual(one.LocalNumber, reopened.Products().Single(p => p.Sku == "A").LocalNumber);
    }

    [TestMethod]
    public void ReimportPreservesNumbersAndMappedIdsRemainVisible()
    {
        var store = new CatalogStore(Path.Combine(Path.GetTempPath(), "sequence-" + Guid.NewGuid().ToString("N")));
        var source = new XmlSource();
        var row = new CatalogProduct { SourceId = source.Id, Sku = "A", Name = "Food", Brand = "Petshop", Category = "Pets" };
        store.Import(source, [row]); var before = store.Products().Single();
        row.SourceProductId = "supplier-44"; row.XmlAttributes["BrandId"] = "B22"; row.XmlAttributes["CategoryId1"] = "C12";
        store.Import(source, [row]); var after = store.Products().Single();
        Assert.AreEqual(before.LocalNumber, after.LocalNumber); Assert.AreEqual(before.Id, after.Id);
        Assert.AreEqual("supplier-44", after.ProductIdLabel); Assert.AreEqual("B22", after.BrandIdLabel); Assert.AreEqual("C12", after.CategoryIdLabel);
    }
}
