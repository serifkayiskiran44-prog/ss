using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #927 (PRICING: missing fee safety gate). A required fee the rule lacks -- commission, shipping, payment -- is
// its own state, INCOMPLETE_COST, naming the fees: the money gate shows no net figure as an estimate, the live
// price write is blocked, the runner queues nothing dispatchable, and the product card says which fees are missing
// instead of an approximate margin. Every fee present computes; a zero fee is a legitimate fee, never a missing one.
[TestClass]
public sealed class MissingFeeGateTests
{
    static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
    static MoneyPriceInput Input(decimal? commission, decimal? shipping, decimal? transaction, decimal? vat = 0m) => new("SKU-1", "local", "s1", 200m, 100m, "TRY") { CommissionRatePercent = commission, EstimatedShipping = shipping, TransactionCost = transaction, VatRatePercent = vat, FxSnapshotUtc = new DateTimeOffset(Now), AsOfUtc = new DateTimeOffset(Now) };

    [TestMethod]
    public void EachMissingFeeIsNamedAllPresentComputesAndAZeroFeeIsLegitimate()
    {
        // All present: a net figure. Zero fees are fees: the net is the sale less the cost, and nothing is approximate.
        var all = MoneyPriceCalculator.Calculate(Input(10m, 5m, 1m));
        Assert.AreEqual(MoneyPriceStatus.Ready, all.Status); Assert.AreEqual("COMPLETE", all.CostCompleteness); Assert.AreEqual(200m - 20m - 5m - 1m - 100m, all.NetContribution);
        var zero = MoneyPriceCalculator.Calculate(Input(0m, 0m, 0m));
        Assert.AreEqual(MoneyPriceStatus.Ready, zero.Status); Assert.AreEqual(100m, zero.NetContribution); Assert.IsFalse(zero.IsApproximate); Assert.AreEqual("COMPLETE", zero.CostCompleteness);

        // Each missing fee alone, and all three: INCOMPLETE_COST naming them, zero figures, never an estimate.
        var noCommission = MoneyPriceCalculator.Calculate(Input(null, 5m, 1m));
        Assert.AreEqual(MoneyPriceStatus.IncompleteCost, noCommission.Status); CollectionAssert.AreEqual(new[] { "komisyon" }, noCommission.MissingFees!.ToList()); Assert.AreEqual("INCOMPLETE_COST", noCommission.CostCompleteness); Assert.AreEqual(0m, noCommission.NetContribution); Assert.IsTrue(noCommission.IsApproximate);
        CollectionAssert.AreEqual(new[] { "kargo" }, MoneyPriceCalculator.Calculate(Input(10m, null, 1m)).MissingFees!.ToList());
        CollectionAssert.AreEqual(new[] { "işlem/ödeme" }, MoneyPriceCalculator.Calculate(Input(10m, 5m, null)).MissingFees!.ToList());
        CollectionAssert.AreEqual(new[] { "komisyon", "kargo", "işlem/ödeme" }, MoneyPriceCalculator.Calculate(Input(null, null, null)).MissingFees!.ToList());
        CollectionAssert.AreEqual(new[] { "komisyon", "işlem/ödeme" }, MoneyPriceCalculator.MissingFees(null, 0m, null).ToList(), "zero is a fee; only null is missing");

        // The tax rate is not a fee: missing, it stays the input gap it was.
        Assert.AreEqual(MoneyPriceStatus.BlockedMissingInput, MoneyPriceCalculator.Calculate(Input(10m, 5m, 1m, vat: null)).Status);

        // The live-write gate names the fees.
        var refused = Assert.ThrowsException<InvalidOperationException>(() => PriceDispatchPreflight.EnsureReady(MoneyPriceCalculator.Calculate(Input(10m, null, null))));
        StringAssert.Contains(refused.Message, "IncompleteCost (eksik maliyet kalemleri: kargo, işlem/ödeme)"); StringAssert.Contains(refused.Message, "local/s1");
        PriceDispatchPreflight.EnsureReady(all);
    }

    [TestMethod]
    public void TheChainTheRunnerAndTheCardShowNoEstimateWhenARequiredFeeIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "missing-fee-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var catalog = new CatalogStore(root);
            catalog.Import(new XmlSource { Id = "src", Name = "Src" }, new[] { new CatalogProduct { SourceId = "src", Sku = "SKU-1", Name = "Product", Cost = 100m, CostCurrency = "TRY", Price = 150m, Currency = "TRY", Stock = 10, Active = true } });
            var id = catalog.Products().Single().Id;
            PricePolicy Rule(int version, decimal? commission, decimal? shipping, decimal? transaction) => new() { Channel = "local", Shop = "s1", Version = version, Formula = "x*2", Currency = "TRY", TryPerUnit = 1, Enabled = true, CommissionPercent = commission, EstimatedShippingTry = shipping, TransactionCostTry = transaction, VatRatePercent = 0 };

            // The chain: a rule without shipping is refused by name; the runner queues nothing dispatchable.
            catalog.SavePricePolicy(Rule(0, 10m, null, 1m));
            var refused = Assert.ThrowsException<InvalidOperationException>(() => catalog.PreviewPrice("local", "s1", id));
            StringAssert.Contains(refused.Message, "IncompleteCost"); StringAssert.Contains(refused.Message, "kargo");
            var automation = new AutomationStore(root); var sync = new SyncStore(root);
            automation.Save(new AutomationJob { Kind = AutomationKind.Price, Enabled = true, NextRunUtc = DateTime.UtcNow.AddMinutes(-1), Channel = "local", Shop = "s1" });
            var run = AutomationRunner.RunDue(catalog, automation, sync, automation.List().Single().Id, DateTime.UtcNow);
            StringAssert.Contains(JsonSerializer.Serialize(run), "IncompleteCost"); Assert.IsTrue(sync.List().Count > 0 && sync.List().All(j => j.Status == SyncStatus.Failed), "no live price write on an incomplete cost");

            // The card: with the rule, no margin and the fee named; with a complete rule of zero fees, the margin; without a rule, the approximate margin as before.
            var product = catalog.Products().Single();
            var incomplete = ProductPriceSummary.Build(product, Now, null, catalog.GetPricePolicy("local", "s1"));
            Assert.AreEqual("INCOMPLETE_COST", incomplete.CostCompleteness); Assert.AreEqual(ProductPriceSummary.IncompleteCost, incomplete.MarginLevel); Assert.AreEqual("—", incomplete.Margin); Assert.AreEqual("kargo", incomplete.MissingFees);
            CollectionAssert.Contains(incomplete.Warnings.ToList(), "Eksik maliyet kalemleri: kargo; net kâr tahmin edilmez.");
            catalog.SavePricePolicy(Rule(catalog.GetPricePolicy("local", "s1")!.Version, 0m, 0m, 0m));
            Assert.AreEqual(200m, catalog.PreviewPrice("local", "s1", id).Price, "zero fees are legitimate fees");
            var complete = ProductPriceSummary.Build(product, Now, null, catalog.GetPricePolicy("local", "s1"));
            Assert.AreEqual("COMPLETE", complete.CostCompleteness); Assert.AreEqual(ProductPriceSummary.Healthy, complete.MarginLevel); StringAssert.Contains(complete.Margin, "33"); Assert.AreEqual("", complete.MissingFees);
            var unknown = ProductPriceSummary.Build(product, Now);
            Assert.AreEqual("UNKNOWN", unknown.CostCompleteness); Assert.AreEqual(ProductPriceSummary.Healthy, unknown.MarginLevel, "without a rule to ask, the card keeps its approximate margin, labelled as such");
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
