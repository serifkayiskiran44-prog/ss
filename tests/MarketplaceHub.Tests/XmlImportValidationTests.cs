using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for issues #1915 (root/item path sample validation with real match
/// count) and #1916 (required field mapping check before import).
[TestClass]
public sealed class XmlImportValidationTests
{
    const string DefaultNamespaceXml = """
        <?xml version="1.0"?>
        <Products xmlns="urn:test">
          <Product><Sku>SKU-1</Sku><Name>Ürün 1</Name><Cost>10</Cost><Stock>5</Stock></Product>
          <Product><Sku>SKU-2</Sku><Name>Ürün 2</Name><Cost>20</Cost><Stock>3</Stock></Product>
        </Products>
        """;

    const string PrefixedNamespaceXml = """
        <?xml version="1.0"?>
        <p:Products xmlns:p="urn:test">
          <p:Product><p:Sku>SKU-1</p:Sku><p:Name>Ürün 1</p:Name><p:Cost>10</p:Cost><p:Stock>5</p:Stock></p:Product>
        </p:Products>
        """;

    [TestMethod]
    public void DefaultNamespaceDoesNotBreakItemMatching()
    {
        var scan = XmlCatalog.Inspect(DefaultNamespaceXml, "/Products/Product");
        Assert.AreEqual(2, scan.MatchCount);
    }

    [TestMethod]
    public void PrefixedNamespaceDoesNotBreakItemMatching()
    {
        var scan = XmlCatalog.Inspect(PrefixedNamespaceXml, "/Products/Product");
        Assert.AreEqual(1, scan.MatchCount);
    }

    [TestMethod]
    public void ZeroMatchPathThrowsInsteadOfSilentlyProceeding()
    {
        Assert.ThrowsException<InvalidOperationException>(() => XmlCatalog.Inspect(DefaultNamespaceXml, "/Products/NoSuchItem"));
    }

    [TestMethod]
    public void ManyMatchesReportsTheRealTotalNotJustTheSampleCap()
    {
        var items = string.Join("", Enumerable.Range(1, 45).Select(i => $"<Product><Sku>SKU-{i}</Sku><Name>Urun {i}</Name><Cost>10</Cost><Stock>1</Stock></Product>"));
        var xml = $"<Products>{items}</Products>";
        var scan = XmlCatalog.Inspect(xml, "/Products/Product");
        Assert.AreEqual(45, scan.MatchCount, "MatchCount must be the true total, not capped at the 20-item sample used for field discovery.");
        Assert.IsTrue(scan.Sample.Count <= 3, "The preview sample itself stays small/bounded.");
    }

    [TestMethod]
    public void MalformedXmlFailsSafeInsteadOfPartialInspect()
    {
        Assert.ThrowsException<InvalidOperationException>(() => XmlCatalog.Inspect("<Products><Product>", "/Products/Product"));
    }

    [TestMethod]
    public void SamplePreviewReflectsActualItemFieldValues()
    {
        var scan = XmlCatalog.Inspect(DefaultNamespaceXml, "/Products/Product");
        Assert.IsTrue(scan.Sample.Count > 0);
        Assert.AreEqual("SKU-1", scan.Sample[0]["Sku"]);
    }

    static XmlSource Source(Action<XmlSource>? configure = null)
    {
        var source = new XmlSource { ItemPath = "/Products/Product", Currency = "USD" };
        configure?.Invoke(source);
        return source;
    }

    [TestMethod]
    public void MissingSkuAndBarcodeMappingBlocksImport()
    {
        var source = Source(s => { s.Fields["Name"] = "Name"; s.Fields["Cost"] = "Cost"; s.Fields["Stock"] = "Stock"; });
        var error = Assert.ThrowsException<InvalidOperationException>(() => XmlCatalog.Preview(DefaultNamespaceXml, source));
        StringAssert.Contains(error.Message, "SKU");
    }

    [TestMethod]
    public void MissingNameMappingBlocksImport()
    {
        var source = Source(s => { s.Fields["Sku"] = "Sku"; s.Fields["Cost"] = "Cost"; s.Fields["Stock"] = "Stock"; });
        Assert.ThrowsException<InvalidOperationException>(() => XmlCatalog.Preview(DefaultNamespaceXml, source));
    }

    [TestMethod]
    public void MissingCostMappingBlocksImport()
    {
        var source = Source(s => { s.Fields["Sku"] = "Sku"; s.Fields["Name"] = "Name"; s.Fields["Stock"] = "Stock"; });
        Assert.ThrowsException<InvalidOperationException>(() => XmlCatalog.Preview(DefaultNamespaceXml, source));
    }

    [TestMethod]
    public void MissingStockMappingBlocksImport()
    {
        var source = Source(s => { s.Fields["Sku"] = "Sku"; s.Fields["Name"] = "Name"; s.Fields["Cost"] = "Cost"; });
        Assert.ThrowsException<InvalidOperationException>(() => XmlCatalog.Preview(DefaultNamespaceXml, source));
    }

    [TestMethod]
    public void EmptyMappingValueIsTreatedAsMissingNotAsMapped()
    {
        var source = Source(s => { s.Fields["Sku"] = "Sku"; s.Fields["Name"] = "Name"; s.Fields["Cost"] = "Cost"; s.Fields["Stock"] = "   "; });
        Assert.ThrowsException<InvalidOperationException>(() => XmlCatalog.Preview(DefaultNamespaceXml, source));
    }

    [TestMethod]
    public void ZeroStockValueIsAllowedOnceRequiredFieldsAreMapped()
    {
        var xml = "<Products><Product><Sku>SKU-1</Sku><Name>Urun</Name><Cost>10</Cost><Stock>0</Stock></Product></Products>";
        var source = Source(s => { s.Fields["Sku"] = "Sku"; s.Fields["Name"] = "Name"; s.Fields["Cost"] = "Cost"; s.Fields["Stock"] = "Stock"; });
        var rows = XmlCatalog.Preview(xml, source);
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(0, rows[0].Stock);
    }

    [TestMethod]
    public void AllRequiredFieldsMappedAllowsImportToProceed()
    {
        var source = Source(s => { s.Fields["Sku"] = "Sku"; s.Fields["Name"] = "Name"; s.Fields["Cost"] = "Cost"; s.Fields["Stock"] = "Stock"; });
        var rows = XmlCatalog.Preview(DefaultNamespaceXml, source);
        Assert.AreEqual(2, rows.Count);
    }

    [TestMethod]
    public void RequiredFieldValidationDoesNotDependOnDocumentState()
    {
        // Same missing-mapping check must reject on a second, independent call
        // (i.e. it isn't a one-time/cached decision) - simulating a restart.
        var source = Source(s => { s.Fields["Name"] = "Name"; s.Fields["Cost"] = "Cost"; s.Fields["Stock"] = "Stock"; });
        Assert.ThrowsException<InvalidOperationException>(() => XmlCatalog.Preview(DefaultNamespaceXml, source));
        Assert.ThrowsException<InvalidOperationException>(() => XmlCatalog.Preview(DefaultNamespaceXml, source));
    }
}
