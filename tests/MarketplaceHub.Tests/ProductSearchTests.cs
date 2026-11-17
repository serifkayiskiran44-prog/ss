using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for issues #1859 (multi-source filter, SQLite-side) and #1860 (read-only
/// duplicate SKU/barcode review filter).
[TestClass]
public sealed class ProductSearchTests
{
    static CatalogStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "product-search-" + Guid.NewGuid().ToString("N"));
        return new CatalogStore(root);
    }

    static CatalogProduct Product(string sourceId, string sku) => new() { SourceId = sourceId, Sku = sku, Name = "Product " + sku, Price = 10, Currency = "USD" };

    static void Seed(CatalogStore store)
    {
        store.SaveSource(new XmlSource { Id = "src-a", Location = "https://example.test/a.xml" });
        store.SaveSource(new XmlSource { Id = "src-b", Location = "https://example.test/b.xml" });
        store.Import(new XmlSource { Id = "src-a", Location = "https://example.test/a.xml" }, [Product("src-a", "SKU-A1"), Product("src-a", "SKU-A2")]);
        store.Import(new XmlSource { Id = "src-b", Location = "https://example.test/b.xml" }, [Product("src-b", "SKU-B1")]);
        store.CreateManual(new CatalogProduct { Sku = "SKU-M1", Name = "Manual product", Price = 10, Currency = "USD" });
    }

    [TestMethod]
    public void SingleSourceFilterReturnsOnlyThatSource()
    {
        var store = NewStore(out var root);
        try
        {
            Seed(store);
            var page = store.Search("", filter: new CatalogFilter { SourceIds = ["src-a"] });
            Assert.AreEqual(2, page.Total);
            Assert.IsTrue(page.Items.All(p => p.SourceId == "src-a"));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MultiSourceFilterUnionsMatches()
    {
        var store = NewStore(out var root);
        try
        {
            Seed(store);
            var page = store.Search("", filter: new CatalogFilter { SourceIds = ["src-a", "src-b"] });
            Assert.AreEqual(3, page.Total);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void NoSourceFilterReturnsEverything()
    {
        var store = NewStore(out var root);
        try
        {
            Seed(store);
            var page = store.Search("", filter: new CatalogFilter());
            Assert.AreEqual(4, page.Total);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ManualSourceTokenSelectsOnlyManualProducts()
    {
        var store = NewStore(out var root);
        try
        {
            Seed(store);
            var page = store.Search("", filter: new CatalogFilter { SourceIds = [CatalogFilter.ManualSource] });
            Assert.AreEqual(1, page.Total);
            Assert.AreEqual("SKU-M1", page.Items.Single().Sku);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ManualPlusRealSourceCombinesBothWithoutMixingIncorrectly()
    {
        var store = NewStore(out var root);
        try
        {
            Seed(store);
            var page = store.Search("", filter: new CatalogFilter { SourceIds = [CatalogFilter.ManualSource, "src-b"] });
            Assert.AreEqual(2, page.Total);
            CollectionAssert.AreEquivalent(new[] { "SKU-M1", "SKU-B1" }, page.Items.Select(p => p.Sku).ToArray());
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void PagingWorksWithSourceFilterApplied()
    {
        var store = NewStore(out var root);
        try
        {
            store.SaveSource(new XmlSource { Id = "src-a", Location = "https://example.test/a.xml" });
            store.Import(new XmlSource { Id = "src-a", Location = "https://example.test/a.xml" }, Enumerable.Range(1, 5).Select(i => Product("src-a", $"SKU-{i}")).ToArray());
            var page1 = store.Search("", offset: 0, limit: 2, filter: new CatalogFilter { SourceIds = ["src-a"] });
            var page2 = store.Search("", offset: 2, limit: 2, filter: new CatalogFilter { SourceIds = ["src-a"] });
            Assert.AreEqual(5, page1.Total);
            Assert.AreEqual(2, page1.Items.Count);
            Assert.AreEqual(2, page2.Items.Count);
            Assert.IsFalse(page1.Items.Select(p => p.Id).Intersect(page2.Items.Select(p => p.Id)).Any());
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DuplicateSkuAcrossSourcesUpdatesExistingIdentityInsteadOfCreatingDuplicate()
    {
        var store = NewStore(out var root);
        try
        {
            store.SaveSource(new XmlSource { Id = "src-a", Location = "https://example.test/a.xml" });
            store.SaveSource(new XmlSource { Id = "src-b", Location = "https://example.test/b.xml" });
            store.Import(new XmlSource { Id = "src-a", Location = "https://example.test/a.xml" }, [Product("src-a", "SHARED-1"), Product("src-a", "UNIQUE-1")]);
            store.Import(new XmlSource { Id = "src-b", Location = "https://example.test/b.xml" }, [Product("src-b", "shared-1")]);

            var page = store.Search("", filter: new CatalogFilter { DuplicateIdentityOnly = true });
            Assert.AreEqual(0, page.Total);
            Assert.AreEqual(2, store.Products().Count);
            Assert.AreEqual(1, store.Products().Count(p => string.Equals(CatalogStore.NormalizeIdentityKey(p.Sku), "SHARED-1")));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void WhitespaceCollisionIsFoundButLeadingZerosOnUniqueSkusAreNotFalsePositives()
    {
        var store = NewStore(out var root);
        try
        {
            store.CreateManual(new CatalogProduct { Sku = "007", Name = "A", Price = 10, Currency = "USD" });
            store.CreateManual(new CatalogProduct { Sku = "0070", Name = "B", Price = 10, Currency = "USD" });
            var page = store.Search("", filter: new CatalogFilter { DuplicateIdentityOnly = true });
            Assert.AreEqual(0, page.Total, "007 and 0070 are different identities; leading zeros must not be numerically normalized away.");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ReviewFilterDetectsButNeverMergesOrDeletesDuplicates()
    {
        var store = NewStore(out var root);
        try
        {
            store.SaveSource(new XmlSource { Id = "src-a", Location = "https://example.test/a.xml" });
            store.SaveSource(new XmlSource { Id = "src-b", Location = "https://example.test/b.xml" });
            store.Import(new XmlSource { Id = "src-a", Location = "https://example.test/a.xml" }, [Product("src-a", "SHARED-1")]);
            var duplicate = Product("src-b", "SHARED-1"); duplicate.Id = Guid.NewGuid().ToString("N");
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(root, "catalog.db")))
            {
                connection.Open(); using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO CatalogProducts(Id,Json) VALUES($id,$json)";
                command.Parameters.AddWithValue("$id", duplicate.Id);
                command.Parameters.AddWithValue("$json", System.Text.Json.JsonSerializer.Serialize(duplicate));
                command.ExecuteNonQuery();
            }

            var before = store.Products().Count;
            var page = store.Search("", filter: new CatalogFilter { DuplicateIdentityOnly = true });
            var after = store.Products().Count;

            Assert.AreEqual(2, page.Total, "Both duplicate rows must be surfaced for review.");
            Assert.AreEqual(before, after, "Running the review filter must never delete or merge rows.");
            Assert.AreEqual(2, page.Items.Select(p => p.Id).Distinct().Count(), "Rows stay distinct - no merge.");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ZeroDuplicatesReturnsEmptyPageNotEverything()
    {
        var store = NewStore(out var root);
        try
        {
            Seed(store);
            var page = store.Search("", filter: new CatalogFilter { DuplicateIdentityOnly = true });
            Assert.AreEqual(0, page.Total);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
