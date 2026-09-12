using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #790 (TEST: Pricing provenance integration suite). Every case goes through the real pricing entry point
// (CatalogStore.PreviewPrice, and AutomationRunner.RunDue where dispatch matters) with a real catalog.db:
// FX conversion, commission, shipping, VAT, rounding and the operator's minimum-margin guard, in the order
// the production chain applies them. Fixture: cost 100 TRY, commission 20% of sale, shipping 10 TRY, so a
// TRY sale S yields net = 0.8*S - 110 (VAT-exclusive) and the zero-contribution point sits at S = 137.50.
[TestClass]
public sealed class PricingProvenanceSuiteTests
{
    static string NewRoot() => Path.Combine(Path.GetTempPath(), "pricing-provenance-" + Guid.NewGuid().ToString("N"));
    static void Cleanup(string root) { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }

    static (CatalogStore Catalog, string ProductId) Seed(string root)
    {
        var catalog = new CatalogStore(root);
        var source = new XmlSource { Id = "src", Name = "Src" };
        catalog.Import(source, new[] { new CatalogProduct { SourceId = source.Id, Sku = "SKU-1", Name = "Product", Cost = 100m, CostCurrency = "TRY", Stock = 10, Active = true } });
        return (catalog, catalog.Products().Single().Id);
    }

    static PricePolicy Policy(string shop, string formula, decimal? commission = 20m, decimal? shipping = 10m, decimal? transaction = 0m, decimal? vat = 0m, bool vatIncluded = false, decimal minimumMargin = 0m, string currency = "TRY", decimal tryPerUnit = 1m, DateTimeOffset? fxObserved = null, int version = 0) => new()
    {
        Channel = "local", Shop = shop, Formula = formula, Currency = currency, TryPerUnit = tryPerUnit, Enabled = true, Version = version,
        CommissionPercent = commission, EstimatedShippingTry = shipping, TransactionCostTry = transaction, VatRatePercent = vat, VatIncludedInSale = vatIncluded,
        MinimumMarginTry = minimumMargin, FxRateObservedUtc = fxObserved,
    };

    static InvalidOperationException Blocked(CatalogStore catalog, string shop, string productId) => Assert.ThrowsException<InvalidOperationException>(() => catalog.PreviewPrice("local", shop, productId));

    [TestMethod]
    public void RoundingBoundaryIsHalfAwayFromZeroOnTheSalePriceAndTheGuardSeesTheRoundedPrice()
    {
        var root = NewRoot();
        try
        {
            var (catalog, id) = Seed(root);
            catalog.SavePricePolicy(Policy("exact", "x*1.375"));      // 137.500 -> net exactly 0.00
            catalog.SavePricePolicy(Policy("down", "x*1.375005"));    // 137.5005 -> 137.50 -> net 0.00
            catalog.SavePricePolicy(Policy("up", "x*1.37505"));       // 137.505 -> 137.51 (half away from zero) -> net +0.008

            StringAssert.Contains(Blocked(catalog, "exact", id).Message, "BlockedNegativeMargin", "A zero contribution is not a profit; the boundary itself is blocked.");
            StringAssert.Contains(Blocked(catalog, "down", id).Message, "BlockedNegativeMargin");
            var preview = catalog.PreviewPrice("local", "up", id);
            Assert.AreEqual(137.51m, preview.Price, "Half-away-from-zero: 137.505 rounds up to 137.51 (banker's rounding would have produced 137.50 and a block).");
            Assert.AreEqual("TRY", preview.Currency);
            Assert.AreEqual(100m, preview.CostTry);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void ATaxChangeOnAVatInclusivePriceFlipsAReadyPriceToBlockedWhileAVatExclusivePriceIsUnaffected()
    {
        var root = NewRoot();
        try
        {
            var (catalog, id) = Seed(root);
            var saved = catalog.SavePricePolicy(Policy("vat", "x*1.6", vat: 0m));                       // 160 -> net 18.00
            Assert.AreEqual(160m, catalog.PreviewPrice("local", "vat", id).Price);

            // The operator records that 20% VAT is included in the 160: the shop keeps 133.33 before fees.
            saved = catalog.SavePricePolicy(Policy("vat", "x*1.6", vat: 20m, vatIncluded: true, version: saved.Version));
            StringAssert.Contains(Blocked(catalog, "vat", id).Message, "BlockedNegativeMargin", "160 incl. 20% VAT nets 133.33 - 32 - 10 - 100 = -8.67 TRY.");

            // The same rate on a VAT-exclusive price changes nothing about the contribution.
            catalog.SavePricePolicy(Policy("vat", "x*1.6", vat: 20m, vatIncluded: false, version: saved.Version));
            Assert.AreEqual(160m, catalog.PreviewPrice("local", "vat", id).Price);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void AnyOneMissingFeeBlocksTheChainInsteadOfBeingTreatedAsZero()
    {
        var root = NewRoot();
        try
        {
            var (catalog, id) = Seed(root);
            catalog.SavePricePolicy(Policy("no-shipping", "x*1.6", shipping: null));
            catalog.SavePricePolicy(Policy("no-commission", "x*1.6", commission: null));
            catalog.SavePricePolicy(Policy("no-vat", "x*1.6", vat: null));
            catalog.SavePricePolicy(Policy("no-transaction", "x*1.6", transaction: null));
            catalog.SavePricePolicy(Policy("complete", "x*1.6"));

            foreach (var shop in new[] { "no-shipping", "no-commission", "no-vat", "no-transaction" })
                StringAssert.Contains(Blocked(catalog, shop, id).Message, "BlockedMissingInput", shop);
            Assert.AreEqual(160m, catalog.PreviewPrice("local", "complete", id).Price, "The positive control with every fee present is Ready.");
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void TheFxLegConvertsRoundsAndCarriesProvenanceAndAStaleOrMissingRateObservationBlocks()
    {
        var root = NewRoot();
        try
        {
            var (catalog, id) = Seed(root);
            var now = DateTimeOffset.UtcNow;
            catalog.SavePricePolicy(Policy("fresh", "x*1.599", currency: "USD", tryPerUnit: 30m, fxObserved: now.AddHours(-1)));   // 159.90 TRY / 30 = 5.33 USD
            catalog.SavePricePolicy(Policy("stale", "x*1.599", currency: "USD", tryPerUnit: 30m, fxObserved: now.AddHours(-25)));
            catalog.SavePricePolicy(Policy("unobserved", "x*1.599", currency: "USD", tryPerUnit: 30m, fxObserved: null));

            var preview = catalog.PreviewPrice("local", "fresh", id);
            Assert.AreEqual(5.33m, preview.Price);
            Assert.AreEqual("USD", preview.Currency);
            Assert.AreEqual(159.9m, preview.FormulaPriceTry, "Provenance: the TRY formula price the USD price was derived from travels with the preview.");
            Assert.AreEqual(100m, preview.CostTry, "Provenance: the cost the guard evaluated against travels with the preview.");
            StringAssert.Contains(Blocked(catalog, "stale", id).Message, "BlockedStaleFx", "A rate observed more than 24h ago is not reused silently.");
            StringAssert.Contains(Blocked(catalog, "unobserved", id).Message, "BlockedMissingInput", "A non-TRY policy without an observation time has no usable rate.");
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void TheOperatorsMinimumMarginIsEnforcedAgainstTheRealNetContributionNotTheNaiveFormulaMinusCost()
    {
        var root = NewRoot();
        try
        {
            var (catalog, id) = Seed(root);
            // 160 TRY: naive formula-minus-cost is +60, the real net contribution is 160 - 32 - 10 - 100 = 18.00.
            catalog.SavePricePolicy(Policy("min-25", "x*1.6", minimumMargin: 25m));
            catalog.SavePricePolicy(Policy("min-18", "x*1.6", minimumMargin: 18m));
            catalog.SavePricePolicy(Policy("min-18.01", "x*1.6", minimumMargin: 18.01m));

            var blocked = Blocked(catalog, "min-25", id);
            StringAssert.Contains(blocked.Message, "asgari kâr", "A 25 TRY minimum must block an 18 TRY contribution even though formula-minus-cost is 60.");
            StringAssert.Contains(blocked.Message, "local/min-25", "The block names the channel/shop it applies to.");
            Assert.AreEqual(160m, catalog.PreviewPrice("local", "min-18", id).Price, "Exactly the minimum is allowed.");
            StringAssert.Contains(Blocked(catalog, "min-18.01", id).Message, "asgari kâr", "One kuruş under the minimum is blocked.");
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void TheMinimumMarginGuardAlsoStopsTheRealAutomationDispatchAndRecordsWhy()
    {
        var root = NewRoot();
        try
        {
            var (catalog, id) = Seed(root);
            catalog.SavePricePolicy(Policy("default", "x*1.6", minimumMargin: 25m));
            var automation = new AutomationStore(root);
            automation.Save(new AutomationJob { Kind = AutomationKind.Price, Enabled = true, NextRunUtc = DateTime.UtcNow.AddMinutes(-1), Channel = "local", Shop = "default" });
            var sync = new SyncStore(root);

            var result = AutomationRunner.RunDue(catalog, automation, sync, automation.List().Single().Id, DateTime.UtcNow);

            Assert.AreEqual(0, result.Queued, "Nothing under the operator's minimum margin may be queued for dispatch.");
            var job = sync.List().Single(x => x.Operation == "price");
            Assert.AreEqual(SyncStatus.Failed, job.Status);
            StringAssert.Contains(job.LastError, "asgari kâr", "The recorded failure explains which guard blocked the price.");
        }
        finally { Cleanup(root); }
    }
}
