using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

[TestClass]
public class XmlDefinitionOptionsTests
{
    const string Feed = "<Items><Item><id>ref-1</id><sku>A</sku><name>Food</name><price>100</price><qty>10</qty><category>Pets</category><sub>Cat</sub><brand></brand><currency></currency><vat></vat><shelf>R2</shelf><weight>1.5</weight><image>https://example.org/cat.jpg</image></Item></Items>";
    static XmlSource Source(string extra = "") => JsonSerializer.Deserialize<XmlSource>("""
        {"ItemPath":"/Items/Item","Currency":"TRY","MarkupPercent":0,"SafetyStock":0,"MaximumStock":100,
         "Fields":{"SourceProductId":"id","Sku":"sku","Name":"name","Cost":"price","Stock":"qty","Category":"category","Category2":"sub","Brand":"brand","CostCurrency":"currency","VatRate":"vat","Shelf":"shelf","Weight":"weight","Image1":"image"},
         "DefaultVatRate":8,"DefaultBrand":"Petshop","DefaultCategory":"Other","FixedCategory":"","CostCurrency":"TRY"
        """ + extra + "}")!;

    [TestMethod]
    public void EmptyFieldsUseDefaultsAndCategoryLevelsAreJoined()
    {
        var product = XmlCatalog.Preview(Feed, Source()).Single();
        Assert.AreEqual("Pets > Cat", product.Category);
        Assert.AreEqual("Petshop", product.Brand);
        Assert.AreEqual(8m, product.VatRate);
        Assert.AreEqual("TRY", product.CostCurrency);
        Assert.AreEqual("R2", product.Shelf);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(product));
        Assert.AreEqual("1.5", doc.RootElement.GetProperty("XmlAttributes").GetProperty("Weight").GetString());
    }

    [TestMethod]
    public void FixedCategoryAndStockOverrideOnlyWhenEnabled()
    {
        var source = Source(",\"FixedCategory\":\"My category\",\"UseFixedStock\":true,\"FixedStock\":7");
        source.Fields.Remove("Stock");
        var row = XmlCatalog.Preview(Feed, source).Single();
        Assert.AreEqual("My category", row.Category);
        Assert.AreEqual("Pets > Cat", row.XmlCategory);
        Assert.AreEqual(7, row.Stock);
    }

    [TestMethod]
    public void XmlIdentityKeepsLocalIdWhenTheMappedSkuChanges()
    {
        var source = Source(); source.Fields.Remove("VatRate");
        var store = new CatalogStore(Path.Combine(Path.GetTempPath(), "xml-identity-" + Guid.NewGuid().ToString("N")));
        store.Import(source, XmlCatalog.Preview(Feed, source));
        var id = store.Products().Single().Id;
        store.Import(source, XmlCatalog.Preview(Feed.Replace("<sku>A</sku>", "<sku>B</sku>"), source));
        var row = store.Products().Single();
        Assert.AreEqual(id, row.Id);
        Assert.AreEqual("B", row.Sku);
    }

    [TestMethod]
    public void SamplesFollowSelectedProductAndKeepAttributePaths()
    {
        var samples = XmlCatalog.FieldSamples("<Items xmlns='urn:test'><Item code='A'><name>First</name><media><url>one</url></media></Item><Item code='B'><name>Second</name><media><url>two</url></media></Item></Items>", "/Items/Item");
        Assert.AreEqual("A", samples[0]["@code"]);
        Assert.AreEqual("Second | two", XmlCatalog.SampleValue(samples[1], "name|media/url|missing"));
        Assert.AreEqual("First", samples[0]["name"]);
    }

    [TestMethod]
    public void InvalidNonemptyVatIsNotReplacedByDefault()
    {
        Assert.ThrowsException<InvalidOperationException>(() => XmlCatalog.Preview(Feed.Replace("<vat></vat>", "<vat>wrong</vat>"), Source()));
    }

    [TestMethod]
    public void ExclusiveVatAndTextStockUseConfiguredRules()
    {
        var source = Source(); source.PriceIncludesVat = false; source.StockIsText = true; source.AvailableStockText = "var"; source.AvailableStockQuantity = 7; source.SafetyStock = 2;
        var row = XmlCatalog.Preview(Feed.Replace("<qty>10</qty>", "<qty>VAR</qty>"), source).Single();
        Assert.AreEqual(108m, row.Cost); Assert.AreEqual(5, row.Stock);
    }

    [TestMethod]
    public void ConflictingXmlIdentityRollsBackWholeImport()
    {
        var source = Source();
        var store = new CatalogStore(Path.Combine(Path.GetTempPath(), "xml-collision-" + Guid.NewGuid().ToString("N")));
        store.Import(source, XmlCatalog.Preview(Feed, source));
        var before = JsonSerializer.Serialize(store.Products());
        var incoming = XmlCatalog.Preview(Feed.Replace("ref-1", "ref-2"), source);
        Assert.ThrowsException<InvalidOperationException>(() => store.Import(source, incoming));
        Assert.AreEqual(before, JsonSerializer.Serialize(store.Products()));
    }
}
