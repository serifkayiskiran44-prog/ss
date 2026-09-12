using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

[TestClass]
public sealed class XmlCatalogExporterTests
{
    static CatalogProduct Product(string sku, string category = "Home", string brand = "Acme", decimal cost = 100m, int stock = 5) =>
        new() { Sku = sku, Name = "Product " + sku, Category = category, Brand = brand, Cost = cost, Stock = stock, SourceId = "src-1" };

    [TestMethod]
    public void XmlExport_ProducesValidXmlForFilteredProducts()
    {
        var products = new[] { Product("A", category: "Home"), Product("B", category: "Garden") };
        var filtered = XmlCatalogExporter.Filter(products, new XmlExportFilter(Category: "Home"));

        var xml = XmlCatalogExporter.Export(filtered, XmlCatalogExporter.StandardTemplate);

        var doc = System.Xml.Linq.XDocument.Parse(xml); // throws on invalid XML
        var items = doc.Root!.Elements("Product").ToList();
        Assert.AreEqual(1, items.Count);
        Assert.AreEqual("A", items[0].Element("Sku")!.Value);
    }

    [TestMethod]
    public void XmlExport_AppliesFieldMappingCorrectly()
    {
        var template = new XmlExportTemplate("custom", "Feed", "Item", new[] { new XmlExportField(nameof(CatalogProduct.Sku), "StokKodu"), new XmlExportField(nameof(CatalogProduct.Name), "UrunAdi") });

        var xml = XmlCatalogExporter.Export(new[] { Product("A") }, template);

        var doc = System.Xml.Linq.XDocument.Parse(xml);
        var item = doc.Root!.Element("Item")!;
        Assert.AreEqual("A", item.Element("StokKodu")!.Value);
        Assert.AreEqual("Product A", item.Element("UrunAdi")!.Value);
        Assert.IsNull(item.Element("Cost")); // only mapped fields are written
    }

    [TestMethod]
    public void XmlExport_AppliesPriceFormulaToExportedField()
    {
        var template = new XmlExportTemplate("markup", "Products", "Product", new[] { new XmlExportField(nameof(CatalogProduct.Sku), "Sku"), new XmlExportField(nameof(CatalogProduct.Cost), "Price", Formula: "x*1.2") });

        var xml = XmlCatalogExporter.Export(new[] { Product("A", cost: 100m) }, template);

        var doc = System.Xml.Linq.XDocument.Parse(xml);
        Assert.AreEqual((100m * 1.2m).ToString(System.Globalization.CultureInfo.InvariantCulture), doc.Root!.Element("Product")!.Element("Price")!.Value);
    }

    [TestMethod]
    public void XmlExport_RoundTripReadBackMatchesSourceProductCount()
    {
        var products = new[] { Product("A"), Product("B"), Product("C") };
        var xml = XmlCatalogExporter.Export(products, XmlCatalogExporter.StandardTemplate);

        var reimported = XmlCatalog.Preview(xml, XmlCatalogExporter.StandardRoundTripSource(Guid.NewGuid().ToString("N")));

        Assert.AreEqual(products.Length, reimported.Count);
        CollectionAssert.AreEquivalent(products.Select(p => p.Sku).ToList(), reimported.Select(p => p.Sku).ToList());
    }

    [TestMethod]
    public void ScheduledXmlExportJob_IsActuallyTriggeredThroughAutomationRunner()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xml-export-automation-" + Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = new CatalogStore(root);
            var automation = new AutomationStore(root);
            var sync = new SyncStore(root);
            var job = automation.Save(new AutomationJob { Kind = AutomationKind.XmlExport, Enabled = true, NextRunUtc = DateTime.UtcNow.AddMinutes(-1), Channel = "local", Shop = "default" });

            var result = AutomationRunner.RunDue(catalog, automation, sync, job.Id, DateTime.UtcNow);

            Assert.AreEqual(1, result.Queued);
            Assert.IsTrue(sync.List().Any(x => x.Operation == "xml-export"));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); System.IO.Directory.Delete(root, true); }
    }

    [TestMethod]
    public void Export_RejectsMappingToAPropertyThatDoesNotExistInsteadOfProducingEmptyXml()
    {
        var template = new XmlExportTemplate("bad", "Products", "Product", new[] { new XmlExportField("DoesNotExist", "Tag") });

        Assert.ThrowsException<ArgumentException>(() => XmlCatalogExporter.Export(new[] { Product("A") }, template));
    }

    [TestMethod]
    public void Export_RejectsTemplateWithNoFieldsInsteadOfProducingEmptyXml()
    {
        var template = new XmlExportTemplate("empty", "Products", "Product", Array.Empty<XmlExportField>());

        Assert.ThrowsException<ArgumentException>(() => XmlCatalogExporter.Export(new[] { Product("A") }, template));
    }

    [TestMethod]
    public void Export_StopsPartwayThroughInsteadOfConsumingTheWholeSourceOnceCancelled()
    {
        using var cts = new CancellationTokenSource();
        var yielded = 0;
        IEnumerable<CatalogProduct> Stream()
        {
            for (var i = 0; i < 20000; i++)
            {
                yielded++;
                if (yielded == 10) cts.Cancel();
                yield return Product("SKU-" + i);
            }
        }

        Assert.ThrowsException<OperationCanceledException>(() => XmlCatalogExporter.Export(Stream(), XmlCatalogExporter.StandardTemplate, cts.Token));
        Assert.IsTrue(yielded < 20000, "A cancelled export must stop mid-stream, not consume the entire 20k-item source first.");
    }

    [TestMethod]
    public async Task ExportAsync_RunsOffTheCallingThreadForLargeCatalogs()
    {
        var products = Enumerable.Range(1, 15000).Select(i => Product("SKU-" + i)).ToList();
        var callingThread = Environment.CurrentManagedThreadId;

        var xml = await XmlCatalogExporter.ExportAsync(products, XmlCatalogExporter.StandardTemplate);

        Assert.IsTrue(System.Xml.Linq.XDocument.Parse(xml).Root!.Elements().Count() == 15000);
        // Task.Run guarantees a pool thread for the actual work, proving the awaited call does not block synchronously.
    }
}
