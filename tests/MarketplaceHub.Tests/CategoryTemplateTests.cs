using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for issues #1891 (versioned category field-template model) and #1892
/// (impact preview before applying a template).
[TestClass]
public sealed class CategoryTemplateTests
{
    static (TaxonomyStore Taxonomy, CatalogStore Catalog, CategoryTemplateStore Templates, string Root) NewStores()
    {
        var root = Path.Combine(Path.GetTempPath(), "category-template-" + Guid.NewGuid().ToString("N"));
        return (new TaxonomyStore(root), new CatalogStore(root), new CategoryTemplateStore(root), root);
    }

    [TestMethod]
    public void CreateReadUpdateIncrementsVersion()
    {
        var (taxonomy, _, templates, root) = NewStores();
        try
        {
            var category = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "Elektronik" });
            var created = templates.Save(new CategoryFieldTemplate { CategoryId = category.Id, Channel = "etsy", ShopId = "shop-a", VatRate = 20 });
            Assert.AreEqual(1, created.Version);

            var updated = templates.Save(new CategoryFieldTemplate { CategoryId = category.Id, Channel = "etsy", ShopId = "shop-a", VatRate = 18 });
            Assert.AreEqual(2, updated.Version);
            Assert.AreEqual(created.Id, updated.Id, "Same scope must update in place, not create a second row.");

            var read = templates.Get(category.Id, "etsy", "shop-a")!;
            Assert.AreEqual(18, read.VatRate);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ShopIsolationKeepsSeparateTemplates()
    {
        var (taxonomy, _, templates, root) = NewStores();
        try
        {
            var category = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "Elektronik" });
            templates.Save(new CategoryFieldTemplate { CategoryId = category.Id, Channel = "etsy", ShopId = "shop-a", VatRate = 20 });
            templates.Save(new CategoryFieldTemplate { CategoryId = category.Id, Channel = "etsy", ShopId = "shop-b", VatRate = 8 });

            Assert.AreEqual(20, templates.Get(category.Id, "etsy", "shop-a")!.VatRate);
            Assert.AreEqual(8, templates.Get(category.Id, "etsy", "shop-b")!.VatRate);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestartPreservesTemplate()
    {
        var root = Path.Combine(Path.GetTempPath(), "category-template-" + Guid.NewGuid().ToString("N"));
        try
        {
            var taxonomy = new TaxonomyStore(root);
            var category = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "Elektronik" });
            new CategoryTemplateStore(root).Save(new CategoryFieldTemplate { CategoryId = category.Id, VatRate = 20 });

            var reopened = new CategoryTemplateStore(root);
            Assert.AreEqual(20, reopened.Get(category.Id, "local", "default")!.VatRate);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void InvalidFieldValuesAreRejected()
    {
        var (taxonomy, _, templates, root) = NewStores();
        try
        {
            var category = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "Elektronik" });
            Assert.ThrowsException<InvalidOperationException>(() => templates.Save(new CategoryFieldTemplate { CategoryId = category.Id, VatRate = 150 }));
            Assert.ThrowsException<InvalidOperationException>(() => templates.Save(new CategoryFieldTemplate { CategoryId = category.Id, Currency = "US" }));
            Assert.ThrowsException<InvalidOperationException>(() => templates.Save(new CategoryFieldTemplate { CategoryId = "" }));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    static CatalogProduct Product(string sourceId, string sku, string category, string brand, string currency, decimal vat) => new()
    { SourceId = sourceId, Sku = sku, Name = "Ürün " + sku, Category = category, Brand = brand, Currency = currency, VatRate = vat, Price = 10 };

    [TestMethod]
    public void PreviewShowsFieldLevelOldAndNewValuesOnlyForChangingProducts()
    {
        var (taxonomy, catalog, templates, root) = NewStores();
        try
        {
            var category = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "Elektronik" });
            var source = new XmlSource { Id = "src-1", Location = "https://example.test/feed.xml" };
            catalog.SaveSource(source);
            catalog.Import(source, [
                Product("src-1", "SKU-1", "Elektronik", "Eski Marka", "USD", 18),
                Product("src-1", "SKU-2", "Elektronik", "Şablon Marka", "USD", 20),
                Product("src-1", "SKU-3", "Başka Kategori", "Eski Marka", "USD", 18),
            ]);
            var template = templates.Save(new CategoryFieldTemplate { CategoryId = category.Id, Brand = "Şablon Marka", VatRate = 20 });

            var preview = templates.PreviewImpact(template, taxonomy, catalog);

            Assert.AreEqual(2, preview.TotalInCategory, "Only products in this category count.");
            Assert.AreEqual(1, preview.Changed.Count);
            Assert.AreEqual(1, preview.UnaffectedCount);
            var row = preview.Changed.Single();
            Assert.AreEqual("SKU-1", row.Sku);
            var brandChange = row.Changes.Single(c => c.Field == "Brand");
            Assert.AreEqual("Eski Marka", brandChange.OldValue);
            Assert.AreEqual("Şablon Marka", brandChange.NewValue);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ZeroImpactPreviewApplyIsANoOp()
    {
        var (taxonomy, catalog, templates, root) = NewStores();
        try
        {
            var category = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "Elektronik" });
            var template = templates.Save(new CategoryFieldTemplate { CategoryId = category.Id, VatRate = 20 });
            var preview = templates.PreviewImpact(template, taxonomy, catalog);
            Assert.AreEqual(0, preview.TotalInCategory);
            var result = templates.ApplyApproved(preview, true, catalog);
            Assert.AreEqual(0, result.Applied);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void StaleProductChangedAfterPreviewIsSkippedNotOverwritten()
    {
        var (taxonomy, catalog, templates, root) = NewStores();
        try
        {
            var category = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "Elektronik" });
            var source = new XmlSource { Id = "src-1", Location = "https://example.test/feed.xml" };
            catalog.SaveSource(source);
            catalog.Import(source, [Product("src-1", "SKU-1", "Elektronik", "Eski Marka", "USD", 18)]);
            var template = templates.Save(new CategoryFieldTemplate { CategoryId = category.Id, Brand = "Şablon Marka" });
            var preview = templates.PreviewImpact(template, taxonomy, catalog);

            // Product changes after the preview was built (concurrent edit).
            var live = catalog.Products().Single();
            live.Name = "Elle değiştirildi";
            catalog.SaveProduct(live);

            var result = templates.ApplyApproved(preview, true, catalog);
            Assert.AreEqual(0, result.Applied);
            Assert.AreEqual(1, result.StaleProductIds.Count);
            Assert.AreEqual("Eski Marka", catalog.Products().Single().Brand, "Stale product must not be overwritten.");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void CancelDoesNotApplyAnything()
    {
        var (taxonomy, catalog, templates, root) = NewStores();
        try
        {
            var category = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "Elektronik" });
            var source = new XmlSource { Id = "src-1", Location = "https://example.test/feed.xml" };
            catalog.SaveSource(source);
            catalog.Import(source, [Product("src-1", "SKU-1", "Elektronik", "Eski Marka", "USD", 18)]);
            var template = templates.Save(new CategoryFieldTemplate { CategoryId = category.Id, Brand = "Şablon Marka" });
            _ = templates.PreviewImpact(template, taxonomy, catalog);
            // Simulates the user cancelling: ApplyApproved is simply never called.
            Assert.AreEqual("Eski Marka", catalog.Products().Single().Brand);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
