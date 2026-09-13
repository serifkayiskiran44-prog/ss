using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #924 (PRICING: FX staleness policy). The age an observed rate may have is the operator's, per channel/shop, with
// what a staler rate does to a manual preview: block, or warn and compute with it. A missing observation blocks in
// every mode (nothing to fall back to); an automatic live write blocks on a stale rate in every mode. The policy
// persists across a restart, and the real pricing chain and the automation runner honour it.
[TestClass]
public sealed class FxStalenessPolicyTests
{
    static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void TheVerdictFollowsAgeThresholdModeAndAutomationAndThePolicyPersists()
    {
        var block = new FxStalenessPolicy { Channel = "etsy", Shop = "S1" };
        Assert.AreEqual(24, block.StaleAfterHours); Assert.AreEqual(FxStalenessPolicy.Block, block.Mode); Assert.IsTrue(block.IsDefault);

        // Fresh, on the boundary, stale, missing -- under the default (block).
        var fresh = FxStaleness.Evaluate(block, Now.AddHours(-3), Now, automatic: false);
        Assert.AreEqual((FxStalenessVerdict.Fresh, FxStalenessVerdict.Allow, 3.0), (fresh.State, fresh.Decision, fresh.AgeHours)); StringAssert.Contains(fresh.Words, "3 saatlik");
        Assert.AreEqual(FxStalenessVerdict.Fresh, FxStaleness.Evaluate(block, Now.AddHours(-24), Now, false).State, "exactly the threshold is still fresh");
        var stale = FxStaleness.Evaluate(block, Now.AddHours(-30), Now, false);
        Assert.AreEqual((FxStalenessVerdict.Stale, FxStalenessVerdict.BlockDecision), (stale.State, stale.Decision)); Assert.IsTrue(stale.Blocks); StringAssert.Contains(stale.Words, "kur bayat: 30 saatlik"); StringAssert.Contains(stale.Words, "engelliyor");
        var missing = FxStaleness.Evaluate(block, null, Now, false);
        Assert.AreEqual((FxStalenessVerdict.Missing, FxStalenessVerdict.BlockDecision), (missing.State, missing.Decision)); StringAssert.Contains(missing.Words, "kur gözlem tarihi yok");
        Assert.AreEqual(0.0, FxStaleness.Evaluate(block, Now.AddHours(1), Now, false).AgeHours, "an observation ahead of the clock is not negative age");

        // Warn: a manual preview proceeds with the words; an automatic write never does; a missing rate still blocks (no fallback to nothing).
        var warn = new FxStalenessPolicy { Channel = "etsy", Shop = "S1", StaleAfterHours = 12, Mode = FxStalenessPolicy.Warn };
        var warned = FxStaleness.Evaluate(warn, Now.AddHours(-30), Now, automatic: false);
        Assert.AreEqual(FxStalenessVerdict.WarnDecision, warned.Decision); Assert.IsFalse(warned.Blocks); StringAssert.Contains(warned.Words, "uyarıyor"); StringAssert.Contains(warned.Words, "canlı yazım engellenir"); StringAssert.Contains(warned.Words, "eşik 12 saat");
        var auto = FxStaleness.Evaluate(warn, Now.AddHours(-30), Now, automatic: true);
        Assert.AreEqual(FxStalenessVerdict.BlockDecision, auto.Decision); StringAssert.Contains(auto.Words, "otomatik canlı yazım bayat kurla yapılmaz");
        Assert.AreEqual(FxStalenessVerdict.Allow, FxStaleness.Evaluate(warn, Now.AddHours(-3), Now, true).Decision);
        Assert.IsTrue(FxStaleness.Evaluate(warn, null, Now, false).Blocks, "warn allows a stale rate, never a missing one");

        // Validation.
        Assert.ThrowsException<ArgumentException>(() => FxStaleness.Validate(new FxStalenessPolicy { Channel = "etsy", Shop = "S1", StaleAfterHours = 0 }));
        Assert.ThrowsException<ArgumentException>(() => FxStaleness.Validate(new FxStalenessPolicy { Channel = "etsy", Shop = "S1", StaleAfterHours = 169 }));
        Assert.ThrowsException<ArgumentException>(() => FxStaleness.Validate(new FxStalenessPolicy { Channel = "etsy", Shop = "S1", Mode = "maybe" }));
        Assert.ThrowsException<ArgumentException>(() => FxStaleness.Validate(new FxStalenessPolicy { Channel = "etsy", Shop = " " }));

        // The store: the default is named for the shop and never saved; a save is a version; a stale version is refused; it reads back after a restart.
        var root = Path.Combine(Path.GetTempPath(), "fx-policy-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new FxStalenessPolicyStore(root);
            var missingPolicy = store.Get("Etsy", "S1"); Assert.IsTrue(missingPolicy.IsDefault); Assert.AreEqual("etsy", missingPolicy.Channel); Assert.AreEqual("S1", missingPolicy.Shop); Assert.AreEqual(0, store.List().Count);
            var saved = store.Save(new FxStalenessPolicy { Channel = "Etsy", Shop = "S1", StaleAfterHours = 48, Mode = "warn" });
            Assert.AreEqual(1, saved.Version); Assert.AreEqual("WARN", saved.Mode); Assert.AreEqual("etsy", saved.Channel);
            var read = store.Get("etsy", "S1"); Assert.AreEqual(48, read.StaleAfterHours); Assert.AreEqual(FxStalenessPolicy.Warn, read.Mode); Assert.IsFalse(read.IsDefault);
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => store.Save(new FxStalenessPolicy { Channel = "etsy", Shop = "S1", StaleAfterHours = 6, Version = 0 })).Message, "başka işlemde değişti");
            var second = store.Save(new FxStalenessPolicy { Channel = "etsy", Shop = "S1", StaleAfterHours = 6, Mode = FxStalenessPolicy.Block, Version = 1 }); Assert.AreEqual(2, second.Version);
            Assert.AreEqual(1, store.List().Count);
            SqliteConnection.ClearAllPools();
            var reopened = new FxStalenessPolicyStore(root).Get("etsy", "S1"); Assert.AreEqual((6, FxStalenessPolicy.Block, 2), (reopened.StaleAfterHours, reopened.Mode, reopened.Version));
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void ThePricingChainHonoursThePolicyAndNeverLetsAnAutomaticWriteUseAStaleRate()
    {
        var root = Path.Combine(Path.GetTempPath(), "fx-policy-chain-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var catalog = new CatalogStore(root);
            catalog.Import(new XmlSource { Id = "src", Name = "Src" }, new[] { new CatalogProduct { SourceId = "src", Sku = "SKU-1", Name = "Product", Cost = 100m, CostCurrency = "TRY", Stock = 10, Active = true } });
            var id = catalog.Products().Single().Id;
            PricePolicy Usd(int version, DateTimeOffset? observed) => new() { Channel = "local", Shop = "default", Version = version, Formula = "x*3", Currency = "USD", TryPerUnit = 30m, Enabled = true, CommissionPercent = 20m, EstimatedShippingTry = 10m, TransactionCostTry = 0m, VatRatePercent = 0m, FxRateObservedUtc = observed };
            catalog.SavePricePolicy(Usd(0, Now.AddHours(-30)));

            // The default policy (24 hours, block): a 30-hour rate is refused by name, as the gate always did.
            var refused = Assert.ThrowsException<InvalidOperationException>(() => catalog.PreviewPrice("local", "default", id, Now));
            StringAssert.Contains(refused.Message, "BlockedStaleFx"); StringAssert.Contains(refused.Message, "kur bayat: 30 saatlik");

            // A wider threshold makes the same rate fresh; the preview carries no warning.
            var policies = new FxStalenessPolicyStore(root);
            policies.Save(new FxStalenessPolicy { Channel = "local", Shop = "default", StaleAfterHours = 48, Mode = FxStalenessPolicy.Block });
            var fresh = catalog.PreviewPrice("local", "default", id, Now);
            Assert.AreEqual(10m, fresh.Price); Assert.AreEqual("USD", fresh.Currency); Assert.AreEqual("", fresh.FxWarning);

            // Warn under a 12-hour threshold: the manual preview computes and says so; the automatic path is refused -- through the real runner, nothing dispatchable is queued.
            policies.Save(new FxStalenessPolicy { Channel = "local", Shop = "default", StaleAfterHours = 12, Mode = FxStalenessPolicy.Warn, Version = 1 });
            var warned = catalog.PreviewPrice("local", "default", id, Now);
            Assert.AreEqual(10m, warned.Price); StringAssert.Contains(warned.FxWarning, "kur bayat: 30 saatlik"); StringAssert.Contains(warned.FxWarning, "uyarıyor");
            var automatic = Assert.ThrowsException<InvalidOperationException>(() => catalog.PreviewPrice("local", "default", id, Now, automatic: true));
            StringAssert.Contains(automatic.Message, "BlockedStaleFx"); StringAssert.Contains(automatic.Message, "otomatik canlı yazım bayat kurla yapılmaz");
            var automation = new AutomationStore(root); var sync = new SyncStore(root);
            automation.Save(new AutomationJob { Kind = AutomationKind.Price, Enabled = true, NextRunUtc = DateTime.UtcNow.AddMinutes(-1), Channel = "local", Shop = "default" });
            var run = AutomationRunner.RunDue(catalog, automation, sync, automation.List().Single().Id, DateTime.UtcNow);
            StringAssert.Contains(JsonSerializer.Serialize(run), "otomatik", "the run reports the refusal");
            Assert.IsTrue(sync.List().Count > 0 && sync.List().All(j => j.Status == SyncStatus.Failed), "no dispatchable price job was queued on a stale rate");

            // A missing observation blocks in warn mode too: there is no rate to fall back to.
            catalog.SavePricePolicy(Usd(catalog.GetPricePolicy("local", "default")!.Version, null));
            var noRate = Assert.ThrowsException<InvalidOperationException>(() => catalog.PreviewPrice("local", "default", id, Now));
            StringAssert.Contains(noRate.Message, "BlockedMissingInput"); StringAssert.Contains(noRate.Message, "kur gözlem tarihi yok");

            // A TRY rule has no FX leg: no policy, no warning, no block.
            catalog.SavePricePolicy(new PricePolicy { Channel = "local", Shop = "try", Formula = "x*2", Currency = "TRY", TryPerUnit = 1, Enabled = true, CommissionPercent = 20m, EstimatedShippingTry = 10m, TransactionCostTry = 0m, VatRatePercent = 0m });
            Assert.AreEqual("", catalog.PreviewPrice("local", "try", id, Now).FxWarning);

            // Restart: the warn policy still governs the manual preview.
            catalog.SavePricePolicy(Usd(catalog.GetPricePolicy("local", "default")!.Version, Now.AddHours(-30)));
            SqliteConnection.ClearAllPools();
            StringAssert.Contains(new CatalogStore(root).PreviewPrice("local", "default", id, Now).FxWarning, "uyarıyor");
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
