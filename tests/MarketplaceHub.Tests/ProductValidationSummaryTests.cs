using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #803 (DESIGN: Product workspace validation summary panel). Blocking / warning / info findings grouped by the
// workspace section that owns the field, with a deep link to the first blocker. The blocking rules are not
// re-implemented here: CatalogStore.Valid and this panel read the same evaluator, so the panel can never
// promise a save that the store will refuse (or vice versa).
[TestClass]
public sealed class ProductValidationSummaryTests
{
    static CatalogProduct Product(Action<CatalogProduct>? tweak = null)
    {
        var p = new CatalogProduct
        {
            Sku = "A", Barcode = "869", Name = "Ürün A", Description = "Açıklama", Brand = "Marka", Category = "Kategori",
            Price = 100m, Currency = "TRY", Cost = 60m, CostCurrency = "TRY", VatRate = 20m, Stock = 5,
            ImageUrls = "https://cdn.example/a.jpg", Active = true,
        };
        tweak?.Invoke(p); return p;
    }

    [TestMethod]
    public void ACompleteProductHasNoBlockersAndNothingToShoutAbout()
    {
        var result = ProductValidation.Evaluate(Product());
        Assert.IsFalse(result.HasBlocking);
        Assert.AreEqual(0, result.Findings.Count(f => f.Severity != ProductValidation.Info));
        Assert.AreEqual("", result.FirstBlockingRoute);
    }

    [TestMethod]
    public void BlockingFindingsAreGroupedBySectionAndTheFirstOneIsDeepLinked()
    {
        var result = ProductValidation.Evaluate(Product(p => { p.Name = ""; p.Price = -1m; p.VatRate = 120m; }));

        Assert.IsTrue(result.HasBlocking);
        var blocking = result.Findings.Where(f => f.Severity == ProductValidation.Blocking).ToList();
        Assert.IsTrue(blocking.Count >= 3, string.Join(" | ", blocking.Select(f => f.Message)));
        CollectionAssert.Contains(blocking.Select(f => f.Section).ToList(), "content");
        CollectionAssert.Contains(blocking.Select(f => f.Section).ToList(), "price-stock");
        Assert.AreEqual(ProductWorkspaceSections.Route(blocking[0].Section), result.FirstBlockingRoute, "The panel can send the operator straight to the first blocker.");
        Assert.AreEqual("content", blocking[0].Section, "Findings are ordered by the workspace's own section order, so 'first' is the topmost one.");
    }

    [TestMethod]
    public void MissingButNonBlockingDataIsAWarningRatherThanAnError()
    {
        var result = ProductValidation.Evaluate(Product(p => { p.Description = ""; p.ImageUrls = ""; }));

        Assert.IsFalse(result.HasBlocking, "A product with no image still saves; it is simply not ready to list.");
        var warnings = result.Findings.Where(f => f.Severity == ProductValidation.Warning).ToList();
        CollectionAssert.AreEquivalent(new[] { "content", "media" }, warnings.Select(f => f.Section).ToArray());
        Assert.IsTrue(warnings.All(w => w.Field.Length > 0));
    }

    [TestMethod]
    public void TheSummaryNamesFieldsAndNeverEchoesTheOffendingValue()
    {
        var secretive = Product(p => { p.Name = new string('x', 900); p.Description = "Bearer abc123secret musteri@example.com"; p.Sku = ""; p.Barcode = ""; });

        var result = ProductValidation.Evaluate(secretive);
        var rendered = string.Join("\n", result.Findings.Select(f => f.Section + " " + f.Field + " " + f.Message));

        Assert.IsFalse(rendered.Contains("abc123secret", StringComparison.Ordinal), rendered);
        Assert.IsFalse(rendered.Contains("musteri@example.com", StringComparison.Ordinal), rendered);
        Assert.IsFalse(rendered.Contains("xxxxxxxxxx", StringComparison.Ordinal), "A too-long value must be reported by name and limit, never quoted back: " + rendered);
        Assert.IsTrue(result.Findings.All(f => f.Message.Length <= 200));
        StringAssert.Contains(rendered, "SKU");
    }

    [TestMethod]
    public void TheStoreRefusesExactlyWhatTheSummaryCallsBlocking()
    {
        var root = Path.Combine(Path.GetTempPath(), "validation-summary-" + Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = new CatalogStore(root);
            var source = new XmlSource { Id = "src", Name = "Src" };
            catalog.Import(source, new[] { Product(p => p.SourceId = "src") });
            var saved = catalog.Products().Single();

            // Every blocking case the evaluator reports must be one the store actually refuses...
            foreach (var (change, label) in new (Action<CatalogProduct>, string)[]
            {
                (p => p.Name = "", "empty name"),
                (p => p.Price = -1m, "negative price"),
                (p => p.VatRate = 120m, "impossible VAT"),
                (p => p.Stock = -3, "negative stock"),
            })
            {
                var candidate = ProductDirtySections.Clone(saved); change(candidate);
                Assert.IsTrue(ProductValidation.Evaluate(candidate).HasBlocking, label + " must be reported as blocking");
                Assert.ThrowsException<InvalidOperationException>(() => catalog.SaveProduct(candidate), label + " must actually be refused by the store");
            }

            // ...and a product the evaluator passes must save.
            var fine = ProductDirtySections.Clone(saved); fine.Brand = "Yeni marka";
            Assert.IsFalse(ProductValidation.Evaluate(fine).HasBlocking);
            catalog.SaveProduct(fine);
            Assert.AreEqual("Yeni marka", catalog.Products().Single().Brand);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
