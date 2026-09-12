using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #285 (MARKETPLACE_ZIP_AUDIT_2026_09_12): AutomationRunner.RunClaimed -> catalog.PreviewPrice was computing
// the price-dispatch gate as (formula - product.Cost) < MinimumMarginTry, with no commission/shipping/tax
// awareness. This exercises the REAL automation path (not the standalone MoneyPriceCalculator, which was
// already tested but never wired in) against the exact synthetic fixture from the audit: cost=100 TRY,
// sale=130 TRY, commission=20% of sale, shipping=10 TRY -> real contribution 130-100-26-10=-6 TRY (a loss
// the naive check would have missed, since the naive diff is +30 TRY). Positive control: sale=160 TRY ->
// contribution 160-100-32-10=18 TRY, must be allowed.
[TestClass]
public sealed class MoneyAwarePriceDispatchTests
{
    static (CatalogStore Catalog, AutomationStore Automation, SyncStore Sync, string Root) Setup(decimal salePrice, out string productId)
    {
        var root = Path.Combine(Path.GetTempPath(), "money-price-dispatch-" + Guid.NewGuid().ToString("N"));
        var catalog = new CatalogStore(root);
        var source = new XmlSource { Id = "src", Name = "Src" };
        catalog.Import(source, new[] { new CatalogProduct { SourceId = source.Id, Sku = "SKU-1", Name = "Product", Cost = 100m, CostCurrency = "TRY", Stock = 10, Active = true } });
        productId = catalog.Products().Single().Id;
        var markup = salePrice / 100m; // Formula "x*<markup>" turns cost=100 into the fixture's sale price.
        catalog.SavePricePolicy(new PricePolicy
        {
            Channel = "local", Shop = "default", Formula = $"x*{markup.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            Currency = "TRY", TryPerUnit = 1, MinimumPrice = 0, MinimumMarginTry = 0, Enabled = true,
            CommissionPercent = 20m, EstimatedShippingTry = 10m, TransactionCostTry = 0m, VatRatePercent = 0m, VatIncludedInSale = false
        });
        var automation = new AutomationStore(root);
        automation.Save(new AutomationJob { Kind = AutomationKind.Price, Enabled = true, NextRunUtc = DateTime.UtcNow.AddMinutes(-1), Channel = "local", Shop = "default" });
        var sync = new SyncStore(root);
        return (catalog, automation, sync, root);
    }

    static void Cleanup(string root) { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }

    [TestMethod]
    public void RealAutomationPathBlocksALossMakingPriceEvenThoughTheNaiveDiffLooksProfitable()
    {
        var (catalog, automation, sync, root) = Setup(130m, out var productId);
        try
        {
            var job = automation.List().Single();
            var result = AutomationRunner.RunDue(catalog, automation, sync, job.Id, DateTime.UtcNow);

            Assert.AreEqual(0, result.Queued, "A loss-making price (real contribution -6 TRY) must not be queued as a dispatchable price job.");
            Assert.IsTrue(result.Errors.Count > 0, "The blocked price must surface as an error, not silently succeed.");
            var priceJobs = sync.List().Where(x => x.Operation == "price").ToList();
            Assert.IsTrue(priceJobs.All(x => x.Status == SyncStatus.Failed), "No price job for the loss-making product may reach Pending/Running (which is what a live dispatcher would pick up).");
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void RealAutomationPathAllowsAHealthyMarginPrice()
    {
        var (catalog, automation, sync, root) = Setup(160m, out var productId);
        try
        {
            var job = automation.List().Single();
            var result = AutomationRunner.RunDue(catalog, automation, sync, job.Id, DateTime.UtcNow);

            Assert.AreEqual(1, result.Queued, "A healthy-margin price (real contribution +18 TRY) must be queued. Errors: " + string.Join(" | ", result.Errors));
            Assert.AreEqual(0, result.Errors.Count);
            var priceJob = sync.List().Single(x => x.Operation == "price");
            Assert.AreEqual(SyncStatus.Pending, priceJob.Status);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void DisabledPolicyBlocksDispatchInsteadOfSilentlyPricing()
    {
        var (catalog, automation, sync, root) = Setup(160m, out var productId);
        try
        {
            var policy = catalog.GetPricePolicy("local", "default")!;
            catalog.SavePricePolicy(new PricePolicy { Channel = policy.Channel, Shop = policy.Shop, Version = policy.Version, Formula = policy.Formula, Currency = policy.Currency, TryPerUnit = policy.TryPerUnit, MinimumPrice = policy.MinimumPrice, MinimumMarginTry = policy.MinimumMarginTry, Enabled = false, CommissionPercent = policy.CommissionPercent, EstimatedShippingTry = policy.EstimatedShippingTry, TransactionCostTry = policy.TransactionCostTry, VatRatePercent = policy.VatRatePercent });

            var job = automation.List().Single();
            var result = AutomationRunner.RunDue(catalog, automation, sync, job.Id, DateTime.UtcNow);

            Assert.AreEqual(0, result.Queued, "A disabled price policy must never dispatch a price.");
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void MissingCommissionOrShippingDataBlocksInsteadOfAssumingZeroCost()
    {
        var root = Path.Combine(Path.GetTempPath(), "money-price-dispatch-" + Guid.NewGuid().ToString("N"));
        var catalog = new CatalogStore(root);
        try
        {
            var source = new XmlSource { Id = "src", Name = "Src" };
            catalog.Import(source, new[] { new CatalogProduct { SourceId = source.Id, Sku = "SKU-2", Name = "Product", Cost = 100m, CostCurrency = "TRY", Stock = 10, Active = true } });
            var productId = catalog.Products().Single().Id;
            // No CommissionPercent/EstimatedShippingTry/VatRatePercent set (all null) -- must block, not treat as 0.
            catalog.SavePricePolicy(new PricePolicy { Channel = "local", Shop = "default", Formula = "x*1.6", Currency = "TRY", TryPerUnit = 1, Enabled = true });
            var automation = new AutomationStore(root);
            automation.Save(new AutomationJob { Kind = AutomationKind.Price, Enabled = true, NextRunUtc = DateTime.UtcNow.AddMinutes(-1), Channel = "local", Shop = "default" });
            var sync = new SyncStore(root);

            var job = automation.List().Single();
            var result = AutomationRunner.RunDue(catalog, automation, sync, job.Id, DateTime.UtcNow);

            Assert.AreEqual(0, result.Queued, "Missing required expense inputs must block dispatch, never be treated as zero cost.");
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void StaleFxRateOnANonTryPolicyBlocksTheRealPreviewInsteadOfReusingAnOldRate()
    {
        var root = Path.Combine(Path.GetTempPath(), "money-price-dispatch-" + Guid.NewGuid().ToString("N"));
        var catalog = new CatalogStore(root);
        try
        {
            var source = new XmlSource { Id = "src", Name = "Src" };
            catalog.Import(source, new[] { new CatalogProduct { SourceId = source.Id, Sku = "SKU-4", Name = "Product", Cost = 100m, CostCurrency = "TRY", Stock = 10, Active = true } });
            var productId = catalog.Products().Single().Id;
            catalog.SavePricePolicy(new PricePolicy
            {
                Channel = "local", Shop = "default", Formula = "x*0.1", Currency = "USD", TryPerUnit = 30m, Enabled = true,
                CommissionPercent = 20m, EstimatedShippingTry = 10m, TransactionCostTry = 0m, VatRatePercent = 0m,
                FxRateObservedUtc = DateTimeOffset.UtcNow.AddDays(-2) // older than the calculator's 24h staleness window.
            });

            var ex = Assert.ThrowsException<InvalidOperationException>(() => catalog.PreviewPrice("local", "default", productId));
            StringAssert.Contains(ex.Message, "BlockedStaleFx");
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void ForeignCostCurrencyBlocksInsteadOfSilentlyTreatingItAsTry()
    {
        var root = Path.Combine(Path.GetTempPath(), "money-price-dispatch-" + Guid.NewGuid().ToString("N"));
        var catalog = new CatalogStore(root);
        try
        {
            var source = new XmlSource { Id = "src", Name = "Src" };
            catalog.Import(source, new[] { new CatalogProduct { SourceId = source.Id, Sku = "SKU-3", Name = "Product", Cost = 100m, CostCurrency = "USD", Stock = 10, Active = true } });
            var productId = catalog.Products().Single().Id;
            catalog.SavePricePolicy(new PricePolicy { Channel = "local", Shop = "default", Formula = "x*1.6", Currency = "TRY", TryPerUnit = 1, Enabled = true, CommissionPercent = 20m, EstimatedShippingTry = 10m, TransactionCostTry = 0m, VatRatePercent = 0m });
            var ex = Assert.ThrowsException<InvalidOperationException>(() => catalog.PreviewPrice("local", "default", productId));
            StringAssert.Contains(ex.Message, "TRY");
        }
        finally { Cleanup(root); }
    }
}
