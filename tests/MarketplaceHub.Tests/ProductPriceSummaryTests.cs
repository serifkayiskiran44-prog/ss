using System;
using System.Globalization;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #798 (DESIGN: Product card price information hierarchy). Four facts in one ranked block: the sale price, its
// currency, when the price was last calculated, and whether the margin is worth worrying about. No new pricing
// behaviour -- the numbers all already exist on the product, and the authoritative gate stays the dispatch-time
// money preflight (#285/#790); this summary is explicitly approximate and says so.
[TestClass]
public sealed class ProductPriceSummaryTests
{
    static readonly DateTime Now = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);

    static CatalogProduct Product(Action<CatalogProduct>? tweak = null)
    {
        var p = new CatalogProduct
        {
            Sku = "A", Name = "Ürün A", Price = 200m, Currency = "TRY", Cost = 120m, CostCurrency = "TRY",
            FormulaPriceTry = 200m, AppliedTryRate = 1m, FxRateDate = Now.AddHours(-2), UpdatedUtc = Now.AddHours(-2),
        };
        tweak?.Invoke(p); return p;
    }

    [TestMethod]
    public void ThePrimaryLineIsThePriceAndTheRestIsRankedBeneathIt()
    {
        var summary = ProductPriceSummary.Build(Product(), Now);

        Assert.AreEqual(200m.ToString("N2", CultureInfo.CurrentCulture), summary.SalePrice, "The price is the number, without the currency glued to it, so a long code cannot push it out of shape.");
        Assert.AreEqual("TRY", summary.Currency);
        StringAssert.Contains(summary.Calculated, "2 saat önce");
        Assert.AreEqual(ProductPriceSummary.Healthy, summary.MarginLevel);
        StringAssert.Contains(summary.Margin, "40", "80 TRY on a 200 TRY price is a 40% approximate margin.");
        Assert.AreEqual(0, summary.Warnings.Count);
        CollectionAssert.AreEqual(new[] { "Satış fiyatı", "Kâr (yaklaşık)", "Son hesaplama" }, summary.Order.ToArray(), "One hierarchy: price first, then margin, then when it was worked out.");
        Assert.IsTrue(summary.MarginCaveat.Contains("yaklaşık", StringComparison.CurrentCultureIgnoreCase), "The block never pretends to be the dispatch gate: " + summary.MarginCaveat);
    }

    [TestMethod]
    public void AMissingPriceIsSaidOutLoudRatherThanRenderedAsZero()
    {
        var summary = ProductPriceSummary.Build(Product(p => { p.Price = 0m; p.FormulaPriceTry = null; p.FxRateDate = null; }), Now);

        Assert.AreEqual("—", summary.SalePrice);
        Assert.AreEqual(ProductPriceSummary.Unknown, summary.MarginLevel, "With no price there is no margin to claim, not a -100%.");
        Assert.AreEqual("—", summary.Margin);
        Assert.AreEqual("Hesaplanmadı", summary.Calculated);
        CollectionAssert.Contains(summary.Warnings.ToList(), "Satış fiyatı girilmemiş.");
    }

    [TestMethod]
    public void AStaleCalculationIsFlaggedWithoutTouchingThePrice()
    {
        var fresh = ProductPriceSummary.Build(Product(p => p.FxRateDate = Now.AddHours(-20)), Now);
        Assert.IsFalse(fresh.IsCalculationStale);
        Assert.AreEqual(0, fresh.Warnings.Count);

        var stale = ProductPriceSummary.Build(Product(p => p.FxRateDate = Now.AddDays(-3)), Now);
        Assert.IsTrue(stale.IsCalculationStale, "Past the 24 hour window the figure on screen is not today's.");
        StringAssert.Contains(stale.Calculated, "3 gün önce");
        CollectionAssert.Contains(stale.Warnings.ToList(), "Fiyat hesabı 24 saatten eski; göndermeden önce yenileyin.");
        Assert.AreEqual(200m.ToString("N2", CultureInfo.CurrentCulture), stale.SalePrice, "Flagging staleness must not alter the number shown.");
    }

    [TestMethod]
    public void ANegativeOrThinApproximateMarginIsWarnedAboutInTheOperatorsTerms()
    {
        var negative = ProductPriceSummary.Build(Product(p => { p.Price = 100m; p.Cost = 130m; }), Now);
        Assert.AreEqual(ProductPriceSummary.Negative, negative.MarginLevel);
        StringAssert.Contains(negative.Margin, "-30");
        CollectionAssert.Contains(negative.Warnings.ToList(), "Yaklaşık kâr negatif: satış fiyatı alış fiyatının altında.");

        var thin = ProductPriceSummary.Build(Product(p => { p.Price = 100m; p.Cost = 96m; }), Now);
        Assert.AreEqual(ProductPriceSummary.Thin, thin.MarginLevel);
        Assert.IsTrue(thin.Warnings.Any(w => w.Contains("düşük", StringComparison.Ordinal)), string.Join(" | ", thin.Warnings));

        // Cost in another currency cannot be subtracted from a TRY price; saying "unknown" is the honest answer.
        var mixed = ProductPriceSummary.Build(Product(p => { p.CostCurrency = "USD"; }), Now);
        Assert.AreEqual(ProductPriceSummary.Unknown, mixed.MarginLevel);
        CollectionAssert.Contains(mixed.Warnings.ToList(), "Alış ve satış para birimleri farklı; yaklaşık kâr hesaplanamıyor.");
    }

    [TestMethod]
    public void ALongOrOddCurrencyCodeCannotDeformTheBlock()
    {
        var summary = ProductPriceSummary.Build(Product(p => p.Currency = "ABCDEFGHIJ"), Now);

        Assert.IsTrue(summary.Currency.Length <= 6, "A currency label is clamped so it cannot run over the price: " + summary.Currency);
        Assert.AreEqual(200m.ToString("N2", CultureInfo.CurrentCulture), summary.SalePrice, "The price keeps its own field regardless of the label beside it.");
    }
}
