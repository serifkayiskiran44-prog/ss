using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #928 (PRICING: net margin explanation breakdown). The money gate's result carries its own explanation -- selling
// price (with the FX leg), VAT if included, commission, shipping, payment, cost, a rounding difference when the
// rounded lines need one, net -- as immutable lines whose amounts add up to the net; the pricing chain's preview and
// the product card show the same lines from the same result; a negative margin is explained, a missing input has
// no explanation to give.
[TestClass]
public sealed class NetMarginBreakdownTests
{
    static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
    static MoneyPriceInput Input(decimal sale, decimal cost, decimal? commission = 10m, decimal? shipping = 5m, decimal? transaction = 1m, decimal vat = 0m, bool vatIncluded = false, string currency = "TRY", decimal? rate = null)
        => new("SKU-1", "local", "s1", sale, cost, currency) { CommissionRatePercent = commission, EstimatedShipping = shipping, TransactionCost = transaction, VatRatePercent = vat, VatIncludedInSale = vatIncluded, FxRateTryPerUnit = rate, FxSnapshotUtc = new DateTimeOffset(Now), AsOfUtc = new DateTimeOffset(Now) };
    static void AddsUp(MoneyPriceResult result)
    {
        var lines = result.Breakdown!; var net = lines.Single(l => l.Key == "net");
        Assert.AreEqual(net.AmountTry, lines.Where(l => l.Key != "net").Sum(l => l.AmountTry), "the lines add up to the net, exactly");
        Assert.AreEqual(result.NetContribution, net.AmountTry, "the net line is the gate's net");
        foreach (var rounding in lines.Where(l => l.Key == "rounding")) Assert.IsTrue(Math.Abs(rounding.AmountTry) <= 0.02m, "a rounding difference is at most two cents: " + rounding.Words);
    }

    [TestMethod]
    public void TheResultExplainsEveryStepAndTheLinesAddUpAcrossNegativeMultiCurrencyMissingAndRoundedCases()
    {
        // The plain case: sale, VAT not included, commission, shipping, payment, cost, net.
        var plain = MoneyPriceCalculator.Calculate(Input(200m, 100m));
        Assert.AreEqual(MoneyPriceStatus.Ready, plain.Status);
        CollectionAssert.AreEqual(new[] { "sale", "vat", "commission", "shipping", "transaction", "cost", "net" }, plain.Breakdown!.Select(l => l.Key).ToArray(), "the order the money is reached in");
        Assert.AreEqual(200m, plain.Breakdown.Single(l => l.Key == "sale").AmountTry); Assert.AreEqual(-20m, plain.Breakdown.Single(l => l.Key == "commission").AmountTry); Assert.AreEqual(-100m, plain.Breakdown.Single(l => l.Key == "cost").AmountTry); Assert.AreEqual(74m, plain.Breakdown.Single(l => l.Key == "net").AmountTry);
        CollectionAssert.Contains(plain.Explanation.ToList(), "Satış fiyatı: 200.00 TRY"); CollectionAssert.Contains(plain.Explanation.ToList(), "Net kâr: 74.00 TRY"); StringAssert.Contains(plain.Explanation.Single(w => w.StartsWith("KDV", StringComparison.Ordinal)), "dahil değil");
        AddsUp(plain);

        // Negative margin: explained the same way, the net below zero and the status saying so.
        var negative = MoneyPriceCalculator.Calculate(Input(200m, 250m));
        Assert.AreEqual(MoneyPriceStatus.BlockedNegativeMargin, negative.Status); Assert.AreEqual(-76m, negative.Breakdown!.Single(l => l.Key == "net").AmountTry); CollectionAssert.Contains(negative.Explanation.ToList(), "Net kâr: -76.00 TRY"); AddsUp(negative);

        // Multi-currency: the sale line carries the FX leg.
        var usd = MoneyPriceCalculator.Calculate(Input(10m, 100m, currency: "USD", rate: 30m));
        Assert.AreEqual(300m, usd.Breakdown!.Single(l => l.Key == "sale").AmountTry); Assert.AreEqual("Satış fiyatı: 10.00 USD × 30 = 300.00 TRY", usd.Breakdown.Single(l => l.Key == "sale").Words); AddsUp(usd);

        // Missing input: no explanation to give -- the state says what is missing instead.
        var missing = MoneyPriceCalculator.Calculate(Input(200m, 100m, commission: null));
        Assert.AreEqual(MoneyPriceStatus.IncompleteCost, missing.Status); Assert.IsNull(missing.Breakdown); Assert.AreEqual(0, missing.Explanation.Count);

        // VAT included and awkward numbers: every line is rounded to the cent, the lines still add up, a rounding line appears only when they need one and is tiny.
        var vat = MoneyPriceCalculator.Calculate(Input(100m, 50m, vat: 18m, vatIncluded: true));
        Assert.AreEqual(-15.25m, vat.Breakdown!.Single(l => l.Key == "vat").AmountTry); StringAssert.Contains(vat.Breakdown.Single(l => l.Key == "vat").Words, "satışa dahil"); AddsUp(vat);
        foreach (var sale in new[] { 10.01m, 33.33m, 99.99m, 123.45m, 0.07m }) AddsUp(MoneyPriceCalculator.Calculate(Input(sale, 0.03m, commission: 33.33m, shipping: 0.01m, transaction: 0.005m, vat: 18m, vatIncluded: true)));
        Assert.IsTrue(plain.Breakdown.All(l => l.AmountTry == decimal.Round(l.AmountTry, 2)), "amounts carry two decimals");
    }

    [TestMethod]
    public void ThePreviewAndTheProductCardShowTheSameExplanationFromTheSameResult()
    {
        var root = Path.Combine(Path.GetTempPath(), "net-margin-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var catalog = new CatalogStore(root);
            catalog.Import(new XmlSource { Id = "src", Name = "Src" }, new[] { new CatalogProduct { SourceId = "src", Sku = "SKU-1", Name = "Product", Cost = 100m, CostCurrency = "TRY", Price = 200m, Currency = "TRY", Stock = 10, Active = true } });
            var product = catalog.Products().Single();
            PricePolicy Rule(int version, string formula, string currency = "TRY", decimal tryPerUnit = 1m) => new() { Channel = "local", Shop = "s1", Version = version, Formula = formula, Currency = currency, TryPerUnit = tryPerUnit, Enabled = true, CommissionPercent = 10m, EstimatedShippingTry = 5m, TransactionCostTry = 1m, VatRatePercent = 0m, FxRateObservedUtc = currency == "TRY" ? null : new DateTimeOffset(Now) };
            catalog.SavePricePolicy(Rule(0, "x*2"));

            // The chain's preview carries the explanation; the card, given the same rule, shows the same lines and calls its margin net.
            var preview = catalog.PreviewPrice("local", "s1", product.Id, new DateTimeOffset(Now));
            Assert.AreEqual(200m, preview.Price); CollectionAssert.Contains(preview.Breakdown!.ToList(), "Net kâr: 74.00 TRY"); Assert.AreEqual(7, preview.Breakdown.Count);
            var card = ProductPriceSummary.Build(product, Now, null, catalog.GetPricePolicy("local", "s1"));
            Assert.AreEqual("net", card.MarginKind); StringAssert.Contains(card.Margin, "37", "74 on 200 is a 37% net margin"); Assert.AreEqual(ProductPriceSummary.Healthy, card.MarginLevel); CollectionAssert.AreEqual(preview.Breakdown.ToList(), card.Breakdown!.ToList(), "one result, two screens"); StringAssert.Contains(card.MarginCaveat, "Net");
            var approximate = ProductPriceSummary.Build(product, Now);
            Assert.AreEqual("approximate", approximate.MarginKind); StringAssert.Contains(approximate.Margin, "50"); Assert.IsNull(approximate.Breakdown); StringAssert.Contains(approximate.MarginCaveat, "Yaklaşık");

            // A negative net under the rule is said as such on the card; a rule in another currency than the product's falls back to the approximate margin.
            catalog.SavePricePolicy(new PricePolicy { Channel = "local", Shop = "s1", Version = catalog.GetPricePolicy("local", "s1")!.Version, Formula = "x*0.9", Currency = "TRY", TryPerUnit = 1, Enabled = true, CommissionPercent = 60m, EstimatedShippingTry = 5m, TransactionCostTry = 1m, VatRatePercent = 0m });
            var negative = ProductPriceSummary.Build(product, Now, null, catalog.GetPricePolicy("local", "s1"));
            Assert.AreEqual("net", negative.MarginKind); Assert.AreEqual(ProductPriceSummary.Negative, negative.MarginLevel); Assert.IsTrue(negative.Warnings.Any(w => w.StartsWith("Net kâr negatif", StringComparison.Ordinal)), string.Join(" | ", negative.Warnings)); CollectionAssert.Contains(negative.Breakdown!.ToList(), "Net kâr: -26.00 TRY");
            catalog.SavePricePolicy(Rule(catalog.GetPricePolicy("local", "s1")!.Version, "x*0.1", currency: "USD", tryPerUnit: 30m));
            var mismatch = ProductPriceSummary.Build(product, Now, null, catalog.GetPricePolicy("local", "s1"));
            Assert.AreEqual("approximate", mismatch.MarginKind); Assert.IsNull(mismatch.Breakdown);
        }
        finally { Cleanup(root); }
    }

    static void Cleanup(string root)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
            catch (IOException) { Thread.Sleep(300); }
            catch (UnauthorizedAccessException) { Thread.Sleep(300); }
        }
    }
}
