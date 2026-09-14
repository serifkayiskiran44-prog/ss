using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for issues #1867 (brand create/edit/active lifecycle) and #1868
/// (dependency guard against deleting a used brand).
[TestClass]
public sealed class BrandRegistryTests
{
    static (TaxonomyStore Taxonomy, CatalogStore Catalog, string Root) NewStores()
    {
        var root = Path.Combine(Path.GetTempPath(), "brand-registry-" + Guid.NewGuid().ToString("N"));
        return (new TaxonomyStore(root), new CatalogStore(root), root);
    }

    [TestMethod]
    public void CreateEditAndDeactivatePreserveIdentity()
    {
        var (taxonomy, _, root) = NewStores();
        try
        {
            var created = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "Nike" });
            var renamed = taxonomy.Save(new TaxonomyEntry { Id = created.Id, Kind = TaxonomyKind.Brand, Name = "Nike Inc.", Active = true });
            Assert.AreEqual(created.Id, renamed.Id, "Editing must keep the same canonical identity, not create a second entry.");
            Assert.AreEqual(1, taxonomy.List(TaxonomyKind.Brand).Count);

            var deactivated = taxonomy.Save(new TaxonomyEntry { Id = created.Id, Kind = TaxonomyKind.Brand, Name = "Nike Inc.", Active = false });
            Assert.IsFalse(deactivated.Active);
            Assert.AreEqual(created.Id, deactivated.Id);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void UnicodeNamesAreSupported()
    {
        var (taxonomy, _, root) = NewStores();
        try
        {
            var entry = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "Şık Çorap Ürünleri" });
            Assert.AreEqual("Şık Çorap Ürünleri", taxonomy.List(TaxonomyKind.Brand).Single(x => x.Id == entry.Id).Name);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DuplicateIdentityIsRejected()
    {
        var (taxonomy, _, root) = NewStores();
        try
        {
            taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "Nike" });
            Assert.ThrowsException<InvalidOperationException>(() => taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "nike" }));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestartReloadsSavedBrands()
    {
        var root = Path.Combine(Path.GetTempPath(), "brand-registry-" + Guid.NewGuid().ToString("N"));
        try
        {
            new TaxonomyStore(root).Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "Nike" });
            var reopened = new TaxonomyStore(root);
            Assert.AreEqual(1, reopened.List(TaxonomyKind.Brand).Count);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void UnusedBrandCanBeHardDeleted()
    {
        var (taxonomy, _, root) = NewStores();
        try
        {
            var entry = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "Unused Brand" });
            taxonomy.Delete(TaxonomyKind.Brand, entry.Id);
            Assert.AreEqual(0, taxonomy.List(TaxonomyKind.Brand).Count);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void BrandUsedByProductCannotBeDeleted()
    {
        var (taxonomy, catalog, root) = NewStores();
        try
        {
            var entry = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "Nike" });
            catalog.SaveSource(new XmlSource { Id = "src-1", Location = "https://example.test/feed.xml" });
            catalog.Import(new XmlSource { Id = "src-1", Location = "https://example.test/feed.xml" }, [new CatalogProduct { SourceId = "src-1", Sku = "SKU-1", Name = "Ürün", Brand = "Nike", Price = 10, Currency = "USD" }]);

            var usage = taxonomy.Usage(TaxonomyKind.Brand, entry);
            Assert.AreEqual(1, usage.Products);
            Assert.ThrowsException<InvalidOperationException>(() => taxonomy.Delete(TaxonomyKind.Brand, entry.Id));
            Assert.AreEqual(1, taxonomy.List(TaxonomyKind.Brand).Count, "Delete must fail closed and not remove the entry.");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void BrandUsedByChannelMappingCannotBeDeleted()
    {
        var (taxonomy, _, root) = NewStores();
        try
        {
            var entry = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "Nike" });
            taxonomy.Map(TaxonomyKind.Brand, "nike-external", entry.Id, "etsy", "shop-a");

            var usage = taxonomy.Usage(TaxonomyKind.Brand, entry);
            Assert.AreEqual(1, usage.Mappings);
            Assert.ThrowsException<InvalidOperationException>(() => taxonomy.Delete(TaxonomyKind.Brand, entry.Id));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DependencyAddedAfterUsageCheckStillBlocksDelete()
    {
        // Simulates the "concurrent new dependency" scenario: usage is re-checked
        // inside the same transaction as the delete, not from an earlier stale read.
        var (taxonomy, catalog, root) = NewStores();
        try
        {
            var entry = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "Nike" });
            var staleUsage = taxonomy.Usage(TaxonomyKind.Brand, entry);
            Assert.AreEqual(0, staleUsage.Total);

            catalog.SaveSource(new XmlSource { Id = "src-1", Location = "https://example.test/feed.xml" });
            catalog.Import(new XmlSource { Id = "src-1", Location = "https://example.test/feed.xml" }, [new CatalogProduct { SourceId = "src-1", Sku = "SKU-1", Name = "Ürün", Brand = "Nike", Price = 10, Currency = "USD" }]);

            Assert.ThrowsException<InvalidOperationException>(() => taxonomy.Delete(TaxonomyKind.Brand, entry.Id));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
