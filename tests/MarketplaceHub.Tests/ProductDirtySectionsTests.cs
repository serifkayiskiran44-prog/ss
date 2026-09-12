using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #802 (DESIGN: Product workspace unsaved-change indicator). The guard that stops an operator losing edits
// already exists; what was missing is being able to see *what* is unsaved and undo one part of it. Which
// section a change belongs to is decided here, and the indicator names fields, never their values -- a price
// or a cost written into a "you changed X to Y" banner is a leak the operator never asked for.
[TestClass]
public sealed class ProductDirtySectionsTests
{
    static CatalogProduct Product() => new()
    {
        Id = "p1", Sku = "A", Barcode = "869", Name = "Ürün A", Description = "Açıklama", Brand = "Marka", Category = "Kategori",
        Price = 100m, Currency = "TRY", Cost = 60m, CostCurrency = "TRY", VatRate = 20m, Stock = 5,
        ImageUrls = "https://cdn.example/a.jpg", EtsyListingId = "", Active = true,
    };

    static ProductDirtyState Compare(CatalogProduct baseline, Action<CatalogProduct> change)
    {
        var current = ProductDirtySections.Clone(baseline); change(current);
        return ProductDirtySections.Compare(baseline, current);
    }

    [TestMethod]
    public void AnUntouchedProductHasNoDirtySections()
    {
        var state = Compare(Product(), _ => { });
        Assert.IsFalse(state.IsDirty);
        Assert.AreEqual(0, state.Sections.Count);
        Assert.AreEqual("", state.Summary);
    }

    [TestMethod]
    public void EachChangedFieldIsAttributedToItsOwnSection()
    {
        Assert.AreEqual("price-stock", Compare(Product(), p => p.Price = 120m).Sections.Single().Key);
        Assert.AreEqual("price-stock", Compare(Product(), p => p.Stock = 9).Sections.Single().Key);
        Assert.AreEqual("content", Compare(Product(), p => p.Description = "Yeni").Sections.Single().Key);
        Assert.AreEqual("media", Compare(Product(), p => p.ImageUrls = "https://cdn.example/b.jpg").Sections.Single().Key);
        Assert.AreEqual("channel", Compare(Product(), p => p.EtsyListingId = "12345").Sections.Single().Key);
        Assert.AreEqual("identity", Compare(Product(), p => p.Active = false).Sections.Single().Key);

        var priced = Compare(Product(), p => p.Price = 120m);
        CollectionAssert.AreEqual(new[] { "Satış fiyatı" }, priced.Sections.Single().Fields.ToArray(), "The indicator names the field, in the operator's words.");
    }

    [TestMethod]
    public void ManyDirtySectionsAreReportedInTheWorkspacesOwnOrderWithACountedSummary()
    {
        var state = Compare(Product(), p => { p.Price = 120m; p.Description = "Yeni"; p.ImageUrls = ""; p.Stock = 9; });

        CollectionAssert.AreEqual(new[] { "content", "price-stock", "media" }, state.Sections.Select(s => s.Key).ToArray(), "Sections are listed in the workspace's order, not in the order the fields happened to change.");
        Assert.IsTrue(state.IsDirty);
        Assert.AreEqual(2, state.Sections.Single(s => s.Key == "price-stock").Fields.Count);
        StringAssert.Contains(state.Summary, "3 bölüm");
        StringAssert.Contains(state.Summary, "4 alan");
    }

    [TestMethod]
    public void TheIndicatorNamesFieldsAndNeverTheirValues()
    {
        var state = Compare(Product(), p => { p.Cost = 1234.56m; p.Price = 4321.99m; p.Description = "gizli musteri@example.com"; });

        var rendered = state.Summary + " " + string.Join(" ", state.Sections.SelectMany(s => new[] { s.Label }.Concat(s.Fields)));
        foreach (var leak in new[] { "1234", "4321", "musteri@example.com", "gizli" })
            Assert.IsFalse(rendered.Contains(leak, StringComparison.Ordinal), $"'{leak}' must not appear in a dirty-state indicator: {rendered}");
        StringAssert.Contains(rendered, "Alış fiyatı");
        StringAssert.Contains(rendered, "Açıklama");
    }

    [TestMethod]
    public void ResettingOneSectionRestoresOnlyThatSectionsFields()
    {
        var baseline = Product();
        var edited = ProductDirtySections.Clone(baseline);
        edited.Price = 120m; edited.Stock = 9; edited.Description = "Yeni"; edited.ImageUrls = "";

        var reset = ProductDirtySections.ResetSection(baseline, edited, "price-stock");

        Assert.AreEqual(100m, reset.Price, "The reset section goes back to the saved values...");
        Assert.AreEqual(5, reset.Stock);
        Assert.AreEqual("Yeni", reset.Description, "...and every other section keeps the operator's unsaved work.");
        Assert.AreEqual("", reset.ImageUrls);
        Assert.AreEqual(baseline.Id, reset.Id);

        var after = ProductDirtySections.Compare(baseline, reset);
        CollectionAssert.AreEqual(new[] { "content", "media" }, after.Sections.Select(s => s.Key).ToArray());

        var unknown = ProductDirtySections.ResetSection(baseline, edited, "does-not-exist");
        Assert.AreEqual(120m, unknown.Price, "An unknown section resets nothing rather than silently reverting the lot.");
    }

    [TestMethod]
    public void EveryEditablePropertyBelongsToSomeSectionSoNoChangeCanGoUnreported()
    {
        var unmapped = ProductDirtySections.UnmappedEditableProperties().ToArray();
        Assert.AreEqual(0, unmapped.Length, "These product properties are not attributed to any workspace section, so a change to them would show no indicator: " + string.Join(", ", unmapped));
    }
}
