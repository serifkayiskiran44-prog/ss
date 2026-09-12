using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop.Catalog;

[TestClass]
public sealed class ProductListRepairTests
{
    [TestMethod]
    public void SearchUsesSqliteFiltersSortAndPagesAcrossAllRows()
    {
        var dir = Path.Combine(Path.GetTempPath(), "monobridge-list-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new CatalogStore(dir);
            var source = new XmlSource { Id = "list-source", Name = "Fixture", Currency = "TRY", Fields = new() };
            var rows = new[] { 1, 2, 3, 4, 5, 6 }.Select(i => new CatalogProduct { SourceId = source.Id, SourceKind = "xml", Sku = $"SKU-{i}", Name = $"Ürün {i}", Category = i % 2 == 0 ? "Ev" : "Moda", Cost = i * 10, Price = i * 100, Stock = i, Currency = "TRY" }).ToArray();
            store.Import(source, rows);
            var page = store.Search("", 0, 2, new CatalogFilter { Categories = ["Ev"], SortBy = "Price", SortDescending = true });
            Assert.AreEqual(3, page.Total);
            Assert.AreEqual(2, page.Items.Count);
            Assert.IsTrue(page.Items[0].Price >= page.Items[1].Price);
            Assert.AreEqual("SKU-6", page.Items[0].Sku);
        }
        finally { /* CatalogStore owns pooled SQLite handles; unique temp fixture is disposable by the OS. */ }
    }
}
