using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class ProfitPriceRepairIntegrationTests
{
    [TestMethod]
    public void ExpenseAwarePreviewBlocksLossAndKeepsHealthyMargin()
    {
        var loss = MoneyPriceCalculator.Calculate(new MoneyPriceInput("SKU-1", "etsy", "shop-a", 100m, 3000m, "USD")
        {
            CommissionRatePercent = 20m, EstimatedShipping = 30m, TransactionCost = 5m,
            VatRatePercent = 10m, VatIncludedInSale = true, FxRateTryPerUnit = 30m,
            FxSnapshotUtc = DateTimeOffset.UtcNow
        });
        Assert.AreEqual(MoneyPriceStatus.BlockedNegativeMargin, loss.Status);
        Assert.IsTrue(loss.NetContribution < 0m);

        var healthy = MoneyPriceCalculator.Calculate(new MoneyPriceInput("SKU-1", "etsy", "shop-a", 220m, 60m, "USD")
        {
            CommissionRatePercent = 20m, EstimatedShipping = 30m, TransactionCost = 5m,
            VatRatePercent = 10m, VatIncludedInSale = true, FxRateTryPerUnit = 30m,
            FxSnapshotUtc = DateTimeOffset.UtcNow
        });
        Assert.AreEqual(MoneyPriceStatus.Ready, healthy.Status);
        Assert.IsTrue(healthy.NetContribution > 0m);
        Assert.AreEqual("etsy/shop-a", healthy.ChannelShop);
    }

    [TestMethod]
    public void MissingExpenseOrFxIsBlockedAndDispatchGateCannotBypassIt()
    {
        var missing = MoneyPriceCalculator.Calculate(new MoneyPriceInput("SKU-2", "ebay", "shop-b", 10m, 100m, "USD")
        { FxSnapshotUtc = DateTimeOffset.UtcNow });
        Assert.AreEqual(MoneyPriceStatus.BlockedMissingInput, missing.Status);
        Assert.ThrowsException<InvalidOperationException>(() => PriceDispatchPreflight.EnsureReady(missing));
    }

    [TestMethod]
    public void StaleFxIsBlockedInsteadOfFallingBackToOneToOne()
    {
        var stale = MoneyPriceCalculator.Calculate(new MoneyPriceInput("SKU-3", "etsy", "shop-c", 100m, 50m, "EUR")
        { CommissionRatePercent = 5m, EstimatedShipping = 10m, TransactionCost = 1m, VatRatePercent = 0m, FxRateTryPerUnit = 35m, FxSnapshotUtc = DateTimeOffset.UtcNow.AddDays(-2), AsOfUtc = DateTimeOffset.UtcNow });
        Assert.AreEqual(MoneyPriceStatus.BlockedStaleFx, stale.Status);
    }
}
