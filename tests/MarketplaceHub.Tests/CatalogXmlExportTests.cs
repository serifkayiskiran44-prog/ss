using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for issues #1923 (deterministic product XML schema) and #1924 (valid
/// UTF-8 XML output for Turkish/emoji/special characters).
[TestClass]
public sealed class CatalogXmlExportTests
{
    static string TempPath() => Path.Combine(Path.GetTempPath(), "xml-export-" + Guid.NewGuid().ToString("N") + ".xml");

    static CatalogProduct Product(string sku = "SKU-1") => new()
    {
        Sku = sku, Barcode = "0001", Name = "Ürün", Brand = "Marka", Category = "Kategori",
        Cost = 5, Price = 10, Currency = "USD", VatRate = 18, Stock = 3, Active = true, Gtin = "123",
    };

    [TestMethod]
    public void FirstVersionExportHasSchemaVersionAndAllColumns()
    {
        var path = TempPath();
        try
        {
            CatalogXmlExport.Export(path, [Product()]);
            var doc = XDocument.Load(path);
            Assert.AreEqual(CatalogXmlExport.SchemaVersion.ToString(), doc.Root!.Attribute("SchemaVersion")!.Value);
            var product = doc.Root!.Elements("Product").Single();
            foreach (var key in CatalogXmlExport.ExportElementKeys) Assert.IsNotNull(product.Element(key), $"missing element {key}");
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void EmptyOptionalFieldsExportAsEmptyElementsNotOmitted()
    {
        var path = TempPath();
        try
        {
            var product = Product(); product.Description = ""; product.Gtin = "";
            CatalogXmlExport.Export(path, [product]);
            var doc = XDocument.Load(path);
            var element = doc.Root!.Elements("Product").Single();
            Assert.AreEqual("", element.Element("Description")!.Value);
            Assert.AreEqual("", element.Element("Gtin")!.Value);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void OutputIsDeterministicRegardlessOfInputOrder()
    {
        var pathA = TempPath(); var pathB = TempPath();
        try
        {
            var a = Product("SKU-A"); var b = Product("SKU-B"); var c = Product("SKU-C");
            CatalogXmlExport.Export(pathA, [c, a, b]);
            CatalogXmlExport.Export(pathB, [b, c, a]);
            Assert.AreEqual(File.ReadAllText(pathA), File.ReadAllText(pathB));
        }
        finally { File.Delete(pathA); File.Delete(pathB); }
    }

    [TestMethod]
    public void RepeatedExportOfSameDataProducesByteIdenticalOutput()
    {
        var pathA = TempPath(); var pathB = TempPath();
        try
        {
            var products = new[] { Product("SKU-1"), Product("SKU-2") };
            CatalogXmlExport.Export(pathA, products);
            CatalogXmlExport.Export(pathB, products);
            CollectionAssert.AreEqual(File.ReadAllBytes(pathA), File.ReadAllBytes(pathB));
        }
        finally { File.Delete(pathA); File.Delete(pathB); }
    }

    [TestMethod]
    public void TurkishCharactersRoundTripExactly()
    {
        var path = TempPath();
        try
        {
            var product = Product(); product.Name = "Şık Çorap Üretici Ğıdışıöç";
            CatalogXmlExport.Export(path, [product]);
            var doc = XDocument.Load(path);
            Assert.AreEqual("Şık Çorap Üretici Ğıdışıöç", doc.Root!.Elements("Product").Single().Element("Name")!.Value);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void EmojiRoundTripsExactly()
    {
        var path = TempPath();
        try
        {
            var product = Product(); product.Description = "Yeni sezon 🎉🚀 ürün";
            CatalogXmlExport.Export(path, [product]);
            var doc = XDocument.Load(path);
            Assert.AreEqual("Yeni sezon 🎉🚀 ürün", doc.Root!.Elements("Product").Single().Element("Description")!.Value);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void XmlSpecialCharactersAreEscapedNotBrokenMarkup()
    {
        var path = TempPath();
        try
        {
            var product = Product(); product.Name = "A & B <Test> \"quoted\" 'value'";
            CatalogXmlExport.Export(path, [product]);
            var raw = File.ReadAllText(path);
            Assert.IsFalse(raw.Contains("<Test>", StringComparison.Ordinal), "Raw angle brackets must be escaped, not written literally as markup.");
            var doc = XDocument.Load(path);
            Assert.AreEqual("A & B <Test> \"quoted\" 'value'", doc.Root!.Elements("Product").Single().Element("Name")!.Value);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void ControlCharacterInFieldFailsWithAClearError()
    {
        var path = TempPath();
        try
        {
            var product = Product(); product.Description = "bad" + (char)1 + "value";
            Assert.ThrowsException<InvalidOperationException>(() => CatalogXmlExport.Export(path, [product]));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [TestMethod]
    public void OutputDeclaresUtf8Encoding()
    {
        var path = TempPath();
        try
        {
            CatalogXmlExport.Export(path, [Product()]);
            var firstLine = File.ReadLines(path).First();
            StringAssert.Contains(firstLine, "utf-8");
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void OnlyPermittedFieldsAreWrittenWhenRestricted()
    {
        var path = TempPath();
        try
        {
            CatalogXmlExport.Export(path, [Product()], ["Sku", "Price"]);
            var doc = XDocument.Load(path);
            var element = doc.Root!.Elements("Product").Single();
            CollectionAssert.AreEquivalent(new[] { "Sku", "Price" }, element.Elements().Select(e => e.Name.LocalName).ToArray());
        }
        finally { File.Delete(path); }
    }
}
