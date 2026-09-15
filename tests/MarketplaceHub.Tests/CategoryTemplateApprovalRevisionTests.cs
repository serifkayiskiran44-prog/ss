using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for #2662: a category-template ApplyApproved call must re-validate
/// the template's own persisted revision before touching any product, so a
/// preview approved against a template that has since changed, moved, or been
/// deleted can never partially or fully apply.
[TestClass]
public sealed class CategoryTemplateApprovalRevisionTests
{
    static string NewRoot() => Path.Combine(Path.GetTempPath(), "category-template-revision-" + Guid.NewGuid().ToString("N"));

    static void WithRoot(Action<string> test)
    {
        var root = NewRoot();
        try { test(root); }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    static (TaxonomyStore taxonomy, CatalogStore catalog, CategoryTemplateStore templates, TaxonomyEntry category) Seed(string root, string sku = "SKU-1")
    {
        var taxonomy = new TaxonomyStore(root);
        var category = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "Elektronik", Value = "Elektronik" });
        var catalog = new CatalogStore(root);
        catalog.ApplyMigration([new() { Sku = sku, Name = "Ürün", Category = "Elektronik", Brand = "Eski", Cost = 10, Price = 20, Currency = "TRY", VatRate = 20, Stock = 1 }], "seed");
        var templates = new CategoryTemplateStore(root);
        return (taxonomy, catalog, templates, category);
    }

    [TestMethod]
    public void NormalApplySucceedsWhenTemplateRevisionUnchanged()
    {
        WithRoot(root =>
        {
            var (taxonomy, catalog, templates, category) = Seed(root);
            var template = templates.Save(new CategoryFieldTemplate { CategoryId = category.Id, Brand = "Yeni" });
            var preview = templates.PreviewImpact(template, taxonomy, catalog);
            var result = templates.ApplyApproved(preview, true, catalog);
            Assert.AreEqual(1, result.Applied);
            Assert.AreEqual("Yeni", catalog.Products().Single().Brand);
        });
    }

    [TestMethod]
    public void StalePreviewAfterTemplateResavedAppliesZeroProducts()
    {
        WithRoot(root =>
        {
            var (taxonomy, catalog, templates, category) = Seed(root);
            var v1 = templates.Save(new CategoryFieldTemplate { CategoryId = category.Id, Brand = "V1" });
            var preview = templates.PreviewImpact(v1, taxonomy, catalog);
            templates.Save(new CategoryFieldTemplate { CategoryId = category.Id, Brand = "V2" }); // template changes after preview was built

            Assert.ThrowsException<CategoryTemplateStalePreviewException>(() => templates.ApplyApproved(preview, true, catalog));
            Assert.AreEqual("Eski", catalog.Products().Single().Brand, "No product may be mutated when the approval was taken against a stale template revision.");
        });
    }

    [TestMethod]
    public void DeletedOrMovedTemplateAppliesZeroProductsAndIsStale()
    {
        WithRoot(root =>
        {
            var (taxonomy, catalog, templates, category) = Seed(root);
            var v1 = templates.Save(new CategoryFieldTemplate { CategoryId = category.Id, Brand = "V1" });
            var preview = templates.PreviewImpact(v1, taxonomy, catalog);
            // Simulate the template no longer resolving under the same scope
            // (e.g. category/channel/shop no longer maps to this template).
            var forgedPreview = preview with { Template = new CategoryFieldTemplate { Id = v1.Id, CategoryId = "nonexistent-category", Channel = v1.Channel, ShopId = v1.ShopId, Version = v1.Version, Brand = v1.Brand } };

            Assert.ThrowsException<CategoryTemplateStalePreviewException>(() => templates.ApplyApproved(forgedPreview, true, catalog));
            Assert.AreEqual("Eski", catalog.Products().Single().Brand);
        });
    }

    [TestMethod]
    public void SingleProductStaleWhileTemplateRevisionUnchangedStillSkipsOnlyThatProduct()
    {
        WithRoot(root =>
        {
            var (taxonomy, catalog, templates, category) = Seed(root);
            var template = templates.Save(new CategoryFieldTemplate { CategoryId = category.Id, Brand = "Yeni" });
            var preview = templates.PreviewImpact(template, taxonomy, catalog);
            // The product itself changes after the preview but the template does not.
            var product = catalog.Products().Single();
            product.Description = "değişti";
            catalog.SaveProduct(product);

            var result = templates.ApplyApproved(preview, true, catalog);
            Assert.AreEqual(0, result.Applied);
            Assert.AreEqual(1, result.StaleProductIds.Count);
            Assert.AreEqual("Eski", catalog.Products().Single().Brand);
        });
    }

    [TestMethod]
    public void ConcurrentTemplateSaveRightBeforeApplyBlocksTheWholeOperation()
    {
        WithRoot(root =>
        {
            var (taxonomy, catalog, templates, category) = Seed(root, "SKU-1");
            catalog.ApplyMigration([new() { Sku = "SKU-2", Name = "Ürün 2", Category = "Elektronik", Brand = "Eski", Cost = 5, Price = 10, Currency = "TRY", VatRate = 20, Stock = 1 }], "seed2");
            var v1 = templates.Save(new CategoryFieldTemplate { CategoryId = category.Id, Brand = "V1" });
            var preview = templates.PreviewImpact(v1, taxonomy, catalog);
            Assert.AreEqual(2, preview.Changed.Count);
            templates.Save(new CategoryFieldTemplate { CategoryId = category.Id, Brand = "V2" }); // race: template updated right before apply

            Assert.ThrowsException<CategoryTemplateStalePreviewException>(() => templates.ApplyApproved(preview, true, catalog));
            Assert.IsTrue(catalog.Products().All(p => p.Brand == "Eski"), "Neither product may be updated - a stale preview never applies partially.");
        });
    }

    [TestMethod]
    public void RevisionCheckSurvivesRestart()
    {
        WithRoot(root =>
        {
            var (taxonomy, catalog, templates, category) = Seed(root);
            var v1 = templates.Save(new CategoryFieldTemplate { CategoryId = category.Id, Brand = "V1" });
            var preview = templates.PreviewImpact(v1, taxonomy, catalog);
            new CategoryTemplateStore(root).Save(new CategoryFieldTemplate { CategoryId = category.Id, Brand = "V2" });

            var reopened = new CategoryTemplateStore(root);
            Assert.ThrowsException<CategoryTemplateStalePreviewException>(() => reopened.ApplyApproved(preview, true, new CatalogStore(root)));
        });
    }
}
