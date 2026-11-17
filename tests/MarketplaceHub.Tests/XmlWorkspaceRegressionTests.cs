using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

[TestClass]
public class XmlWorkspaceRegressionTests
{
    const string Feed = "<Products><Product><code>A</code><name>Food</name><price>100</price><stock>10</stock><cat>Pets</cat><image1>https://example.com/a.jpg</image1><image2>https://example.com/b.jpg</image2></Product></Products>";

    static XmlSource Source() => JsonSerializer.Deserialize<XmlSource>("""
        {"Name":"Supplier", "ItemPath":"/Products/Product", "Currency":"TRY", "MaximumStock":100,
         "Fields":{"Sku":"code","Name":"name","Cost":"price","Stock":"stock","Category":"cat","Image1":"image2"},
         "CategoryRules":[{"XmlCategory":"Pets","TargetCategory":"Petshop > Food","Enabled":false,
           "Prices":{"Trendyol":{"SaleFormula":"x*1.2","ListFormula":"x*1.5"},"eBay":{"SaleFormula":"x+50","ListFormula":""}}}]}
        """)!;

    [TestMethod]
    public void SelectedCategoryAndMarketplaceRulesAffectPreviewAndPersistedProduct()
    {
        var source = Source();
        var row = XmlCatalog.Preview(Feed, source).Single();
        Assert.AreEqual("Petshop > Food", row.Category);
        Assert.IsFalse(row.Active);
        Assert.AreEqual("https://example.com/b.jpg", row.ImageUrls);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(row));
        Assert.AreEqual(120m, doc.RootElement.GetProperty("ChannelPrices").GetProperty("Trendyol").GetProperty("SalePrice").GetDecimal());
        Assert.AreEqual(150m, doc.RootElement.GetProperty("ChannelPrices").GetProperty("Trendyol").GetProperty("ListPrice").GetDecimal());
        Assert.AreEqual(150m, doc.RootElement.GetProperty("ChannelPrices").GetProperty("eBay").GetProperty("SalePrice").GetDecimal());
        var directory = Path.Combine(Path.GetTempPath(), "xml-rules-" + Guid.NewGuid().ToString("N"));
        var store = new CatalogStore(directory);
        store.Import(source, new[] { row });
        var before = store.Products().Single();
        store.Import(source, XmlCatalog.Preview(Feed.Replace("<price>100", "<price>200"), source));
        var after = store.Products().Single();
        Assert.AreEqual(before.Id, after.Id);
        using var updated = JsonDocument.Parse(JsonSerializer.Serialize(after));
        Assert.AreEqual(240m, updated.RootElement.GetProperty("ChannelPrices").GetProperty("Trendyol").GetProperty("SalePrice").GetDecimal());
    }

    [TestMethod]
    public void StartupDoesNotOverwriteUserMappingOrSourcePrefixes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xml-preset-" + Guid.NewGuid().ToString("N"));
        var store = new CatalogStore(directory);
        PetshopTedarikXmlSource.Ensure(store);
        var source = store.Sources().Single();
        source.Fields["Name"] = "satisAd";
        source.Fields.Remove("Image2");
        source.SkuPrefix = "MY-";
        source.BarcodePrefix = "BAR-";
        source.GtinPrefix = "GTIN-";
        source.ImageUrlPrefix = "https://images.example/catalog/";
        store.SaveSource(source);
        PetshopTedarikXmlSource.Ensure(store);
        var reopened = store.Sources().Single();
        Assert.AreEqual("satisAd", reopened.Fields["Name"]);
        Assert.IsFalse(reopened.Fields.ContainsKey("Image2"));
        Assert.AreEqual("MY-", reopened.SkuPrefix);
        Assert.AreEqual("BAR-", reopened.BarcodePrefix);
        Assert.AreEqual("GTIN-", reopened.GtinPrefix);
        Assert.AreEqual("https://images.example/catalog/", reopened.ImageUrlPrefix);
    }

    [TestMethod]
    public void BuiltInSourceCanSaveEmptyAuthenticationWithoutChangingItsIdentity()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xml-auth-" + Guid.NewGuid().ToString("N"));
        XmlAuthStore.Save("petshoptedarik-amazon", new XmlAuth(), directory);
        Assert.AreEqual("", XmlAuthStore.Load("petshoptedarik-amazon", directory).User);
        Assert.ThrowsException<InvalidOperationException>(() => XmlAuthStore.Load("../outside", directory));
    }

    [TestMethod]
    public void InvalidDiscountRangeIsRejectedBeforeImport()
    {
        var source = Source();
        source.CategoryRules.Single().Prices["Trendyol"].ListFormula = "x";
        Assert.ThrowsException<InvalidOperationException>(() => XmlCatalog.Preview(Feed, source));
    }

    [TestMethod]
    public void CategoryRulesHonorProductPriceLockAndCanBeReloaded()
    {
        var source = Source();
        var directory = Path.Combine(Path.GetTempPath(), "xml-lock-" + Guid.NewGuid().ToString("N"));
        var store = new CatalogStore(directory);
        store.SaveSource(source);
        source = store.Sources().Single();
        store.Import(source, XmlCatalog.Preview(Feed, source));
        var product = store.Products().Single(); product.LockPrice = true; store.SaveProduct(product);
        source.CategoryRules.Single().Prices["Trendyol"].SaleFormula = "x+40";
        store.Import(source, XmlCatalog.Preview(Feed, source));
        Assert.AreEqual(120m, store.Products().Single().ChannelPrices["Trendyol"].SalePrice);
        Assert.IsTrue(XmlCatalog.ItemPaths(Feed).Contains("/Products/Product"));
    }
}
