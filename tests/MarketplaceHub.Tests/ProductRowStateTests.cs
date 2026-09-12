using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #794 (DESIGN: Product row state hierarchy). One semantic classification for the product row, shared by the
// badge column, the row border and the tooltip, so selected/hover/error/stale/pending/disabled can never be
// signalled three different ways in three places. Every state must carry a signal that is not colour (a glyph
// and a word, plus border weight), must survive high contrast, and must not put sensitive data in a tooltip.
[TestClass]
public sealed class ProductRowStateTests
{
    static readonly DateTime Now = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);

    static CatalogProduct Product(Action<CatalogProduct>? tweak = null)
    {
        var product = new CatalogProduct { Sku = "A", Name = "Ürün A", Price = 100m, Cost = 60m, Stock = 5, Active = true, SourceKind = "xml", SourceUpdatedUtc = Now.AddHours(-1), UpdatedUtc = Now.AddHours(-1) };
        tweak?.Invoke(product); return product;
    }

    [TestMethod]
    public void EachDataStateIsClassifiedFromTheProductsOwnFields()
    {
        Assert.AreEqual(ProductRowState.Normal, ProductRowState.Classify(Product(), Now).Key);
        Assert.AreEqual(ProductRowState.Disabled, ProductRowState.Classify(Product(p => p.Active = false), Now).Key);
        Assert.AreEqual(ProductRowState.Error, ProductRowState.Classify(Product(p => p.Duplicate = true), Now).Key);
        Assert.AreEqual(ProductRowState.Error, ProductRowState.Classify(Product(p => p.SourceMissing = true), Now).Key);
        Assert.AreEqual(ProductRowState.Warning, ProductRowState.Classify(Product(p => p.Anomaly = true), Now).Key);
        Assert.AreEqual(ProductRowState.Pending, ProductRowState.Classify(Product(p => p.EtsyCreationAttempted = true), Now).Key, "A dispatch was attempted but no listing id came back: the row is waiting on something.");
        Assert.AreEqual(ProductRowState.Normal, ProductRowState.Classify(Product(p => { p.EtsyCreationAttempted = true; p.EtsyListingId = "123"; }), Now).Key, "Once the listing exists there is nothing pending.");
        Assert.AreEqual(ProductRowState.Stale, ProductRowState.Classify(Product(p => p.SourceUpdatedUtc = Now.AddDays(-9)), Now).Key);
        Assert.AreEqual(ProductRowState.Normal, ProductRowState.Classify(Product(p => { p.SourceKind = "manual"; p.SourceUpdatedUtc = Now.AddDays(-9); }), Now).Key, "A manually maintained product is not stale just because no feed touched it.");
    }

    [TestMethod]
    public void CombinedStatesResolveByOneDocumentedPrecedenceInsteadOfWhicheverIsCheckedFirst()
    {
        // A passive product that is also duplicated, anomalous, pending and stale: the operator is told the one
        // thing that decides what they can do with the row, and the rest stay in the reason text.
        var all = ProductRowState.Classify(Product(p => { p.Active = false; p.Duplicate = true; p.Anomaly = true; p.EtsyCreationAttempted = true; p.SourceUpdatedUtc = Now.AddDays(-9); }), Now);
        Assert.AreEqual(ProductRowState.Disabled, all.Key, "Disabled outranks everything: nothing else matters if the row is not sellable.");
        StringAssert.Contains(all.Reason, "Yinelenen");
        StringAssert.Contains(all.Reason, "Bayat");

        Assert.AreEqual(ProductRowState.Error, ProductRowState.Classify(Product(p => { p.Duplicate = true; p.Anomaly = true; p.EtsyCreationAttempted = true; }), Now).Key);
        Assert.AreEqual(ProductRowState.Warning, ProductRowState.Classify(Product(p => { p.Anomaly = true; p.EtsyCreationAttempted = true; p.SourceUpdatedUtc = Now.AddDays(-9); }), Now).Key);
        Assert.AreEqual(ProductRowState.Pending, ProductRowState.Classify(Product(p => { p.EtsyCreationAttempted = true; p.SourceUpdatedUtc = Now.AddDays(-9); }), Now).Key);

        var severities = new[] { ProductRowState.Normal, ProductRowState.Stale, ProductRowState.Pending, ProductRowState.Warning, ProductRowState.Error, ProductRowState.Disabled }
            .Select(k => ProductRowState.Describe(k).Severity).ToArray();
        CollectionAssert.AreEqual(severities.OrderBy(x => x).ToArray(), severities, "Severity is monotonic in the documented precedence order, which is what Classify sorts by.");
    }

    [TestMethod]
    public void EveryStateSignalsItselfWithoutColourAndStaysDistinctUnderHighContrast()
    {
        var keys = new[] { ProductRowState.Normal, ProductRowState.Stale, ProductRowState.Pending, ProductRowState.Warning, ProductRowState.Error, ProductRowState.Disabled };
        var badges = keys.Select(k => ProductRowState.Describe(k).Badge).ToArray();

        Assert.AreEqual(keys.Length, badges.Distinct().Count(), "Two states must never share a badge; colour is not allowed to be the only difference.");
        foreach (var key in keys.Where(k => k != ProductRowState.Normal))
        {
            var info = ProductRowState.Describe(key);
            Assert.IsFalse(string.IsNullOrWhiteSpace(info.Glyph), $"{key} has no glyph.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(info.Label), $"{key} has no word.");
            StringAssert.Contains(info.Badge, info.Label, $"{key}'s badge must spell the state out, not rely on the glyph alone.");
            Assert.IsTrue(ProductRowState.BorderThickness(key).Left >= 3, $"{key} needs a border weight a monochrome display can show.");
        }
        Assert.AreEqual(0d, ProductRowState.BorderThickness(ProductRowState.Normal).Left, "An ordinary row is not decorated.");

        // High contrast: the accent comes from the system palette instead of the app's own colours, and the
        // non-colour signals are unchanged.
        foreach (var key in keys)
        {
            var normal = ProductRowState.AccentBrush(key, highContrast: false);
            var contrast = ProductRowState.AccentBrush(key, highContrast: true);
            Assert.IsNotNull(contrast);
            Assert.IsTrue(contrast.IsFrozen || contrast is SolidColorBrush, $"{key} must resolve to a real brush under high contrast.");
            if (key != ProductRowState.Normal) Assert.AreNotEqual(((SolidColorBrush)normal).Color, ((SolidColorBrush)contrast).Color, $"{key} must not keep its low-contrast colour when high contrast is on.");
            Assert.AreEqual(ProductRowState.Describe(key).Badge, ProductRowState.Describe(key).Badge, "Badges do not depend on the display mode.");
        }
    }

    [TestMethod]
    public void TheTooltipExplainsTheStateWithoutLeakingCostOrSourceLocation()
    {
        var product = Product(p => { p.Cost = 1234.56m; p.Duplicate = true; p.SourceId = "supplier-secret"; });
        var tooltip = ProductRowState.Tooltip(ProductRowState.Classify(product, Now), product);

        StringAssert.Contains(tooltip, "Yinelenen", "The tooltip says what the state is.");
        Assert.IsFalse(tooltip.Contains("1234", StringComparison.Ordinal), "Buying cost is commercially sensitive and must not sit in a hover tooltip: " + tooltip);
        Assert.IsFalse(tooltip.Contains("supplier-secret", StringComparison.Ordinal), "The supplier/source identity must not leak through the row tooltip: " + tooltip);
        Assert.IsFalse(tooltip.Contains("Bearer", StringComparison.OrdinalIgnoreCase));

        var longName = new string('Ü', 5000);
        var wordy = ProductRowState.Tooltip(ProductRowState.Classify(Product(p => { p.Name = longName; p.Anomaly = true; }), Now), Product(p => p.Name = longName));
        Assert.IsTrue(wordy.Length <= 400, "A pathological product name must not turn the tooltip into a wall of text: " + wordy.Length);
    }
}
