using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for issues #1875 (category create/edit/active lifecycle) and #1876
/// (dependency guard against deleting a used category). The Taxonomy panel and
/// TaxonomyStore.Usage/Delete added for BRAND-REGISTRY (#1867/#1868) are kind-generic,
/// so TaxonomyKind.Category already goes through the exact same code path - these
/// tests lock that in explicitly rather than assuming it by inspection.
[TestClass]
public sealed class CategoryCoreTests
{
    static (TaxonomyStore Taxonomy, CatalogStore Catalog, string Root) NewStores()
    {
        var root = Path.Combine(Path.GetTempPath(), "category-core-" + Guid.NewGuid().ToString("N"));
        return (new TaxonomyStore(root), new CatalogStore(root), root);
    }

    [TestMethod]
    public void CreateEditAndDeactivatePreserveIdentity()
    {
        var (taxonomy, _, root) = NewStores();
        try
        {
            var created = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "Ayakkabı" });
            var renamed = taxonomy.Save(new TaxonomyEntry { Id = created.Id, Kind = TaxonomyKind.Category, Name = "Ayakkabı & Çanta", Active = true });
            Assert.AreEqual(created.Id, renamed.Id);
            var deactivated = taxonomy.Save(new TaxonomyEntry { Id = created.Id, Kind = TaxonomyKind.Category, Name = "Ayakkabı & Çanta", Active = false });
            Assert.IsFalse(deactivated.Active);
            Assert.AreEqual(1, taxonomy.List(TaxonomyKind.Category).Count);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void UnicodeDuplicateIdentityIsRejected()
    {
        var (taxonomy, _, root) = NewStores();
        try
        {
            taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "Şık Çanta" });
            Assert.ThrowsException<InvalidOperationException>(() => taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "şık çanta" }));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestartReloadsSavedCategories()
    {
        var root = Path.Combine(Path.GetTempPath(), "category-core-" + Guid.NewGuid().ToString("N"));
        try
        {
            new TaxonomyStore(root).Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "Elektronik" });
            Assert.AreEqual(1, new TaxonomyStore(root).List(TaxonomyKind.Category).Count);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void UnusedCategoryCanBeHardDeleted()
    {
        var (taxonomy, _, root) = NewStores();
        try
        {
            var entry = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "Kullanılmayan" });
            taxonomy.Delete(TaxonomyKind.Category, entry.Id);
            Assert.AreEqual(0, taxonomy.List(TaxonomyKind.Category).Count);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void CategoryUsedByProductCannotBeDeleted()
    {
        var (taxonomy, catalog, root) = NewStores();
        try
        {
            var entry = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "Elektronik" });
            catalog.SaveSource(new XmlSource { Id = "src-1", Location = "https://example.test/feed.xml" });
            catalog.Import(new XmlSource { Id = "src-1", Location = "https://example.test/feed.xml" }, [new CatalogProduct { SourceId = "src-1", Sku = "SKU-1", Name = "Ürün", Category = "Elektronik", Price = 10, Currency = "USD" }]);

            Assert.AreEqual(1, taxonomy.Usage(TaxonomyKind.Category, entry).Products);
            Assert.ThrowsException<InvalidOperationException>(() => taxonomy.Delete(TaxonomyKind.Category, entry.Id));
            Assert.AreEqual(1, taxonomy.List(TaxonomyKind.Category).Count);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ConcurrentlyAddedMappingDependencyStillBlocksDelete()
    {
        var (taxonomy, _, root) = NewStores();
        try
        {
            var entry = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "Elektronik" });
            Assert.AreEqual(0, taxonomy.Usage(TaxonomyKind.Category, entry).Total);
            taxonomy.Map(TaxonomyKind.Category, "electronics-external", entry.Id, "etsy", "shop-a");
            Assert.ThrowsException<InvalidOperationException>(() => taxonomy.Delete(TaxonomyKind.Category, entry.Id));
            Assert.AreEqual(1, taxonomy.List(TaxonomyKind.Category).Count, "Delete must be cancelled/fail closed, not partially applied.");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
