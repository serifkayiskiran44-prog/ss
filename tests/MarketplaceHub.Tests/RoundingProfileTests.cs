using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #925 (PRICING: channel rounding profiles). A shop's sale price keeps the decimals its profile says, never more
// than its currency carries, settled the way the profile says; a profile is a revision resolved at a date, so a
// historical calculation rounds as its day did and the preview names the revision; without a profile the currency's
// precision with half away from zero stands as before.
[TestClass]
public sealed class RoundingProfileTests
{
    static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void MidpointsFollowTheModeDecimalsNeverExceedTheCurrencyAndRevisionsResolveByDate()
    {
        // Midpoint cases, two decimals and none.
        Assert.AreEqual(137.51m, RoundingProfile.Round(137.505m, 2, RoundingProfile.AwayFromZero));
        Assert.AreEqual(137.50m, RoundingProfile.Round(137.505m, 2, RoundingProfile.ToEven));
        Assert.AreEqual(137.50m, RoundingProfile.Round(137.505m, 2, RoundingProfile.ToZero));
        Assert.AreEqual(137.51m, RoundingProfile.Round(137.501m, 2, RoundingProfile.Up), "up settles any remainder upward");
        Assert.AreEqual(137.50m, RoundingProfile.Round(137.509m, 2, RoundingProfile.Down), "down settles any remainder downward");
        Assert.AreEqual(3m, RoundingProfile.Round(2.5m, 0, RoundingProfile.AwayFromZero)); Assert.AreEqual(2m, RoundingProfile.Round(2.5m, 0, RoundingProfile.ToEven)); Assert.AreEqual(4m, RoundingProfile.Round(3.5m, 0, RoundingProfile.ToEven));
        Assert.AreEqual(2m, RoundingProfile.Round(2.5m, 0, RoundingProfile.ToZero)); Assert.AreEqual(3m, RoundingProfile.Round(2.1m, 0, RoundingProfile.Up)); Assert.AreEqual(2m, RoundingProfile.Round(2.9m, 0, RoundingProfile.Down));
        Assert.ThrowsException<ArgumentException>(() => RoundingProfile.Round(1m, 2, "Nearest"));

        // Currency precision: the yen carries none, the dinar three, the rest two.
        Assert.AreEqual(0, CurrencyPrecision.Of("JPY")); Assert.AreEqual(3, CurrencyPrecision.Of("kwd")); Assert.AreEqual(2, CurrencyPrecision.Of("TRY")); Assert.AreEqual(2, CurrencyPrecision.Of(""));

        var root = Path.Combine(Path.GetTempPath(), "rounding-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new RoundingProfileStore(root);
            Assert.ThrowsException<ArgumentException>(() => store.Save("etsy", "S1", 3, RoundingProfile.ToEven, "TRY", "", Now), "three decimals on a two-decimal currency");
            Assert.ThrowsException<ArgumentException>(() => store.Save("etsy", "S1", 1, RoundingProfile.ToEven, "JPY", "", Now), "one decimal on the yen");
            Assert.ThrowsException<ArgumentException>(() => store.Save("etsy", "S1", 5, RoundingProfile.ToEven, null, "", Now));
            Assert.ThrowsException<ArgumentException>(() => store.Save("etsy", "S1", 2, "Nearest", "TRY", "", Now));
            Assert.ThrowsException<ArgumentException>(() => store.Save("etsy", " ", 2, RoundingProfile.ToEven, "TRY", "", Now));

            // No profile: the currency's default, said as such.
            var none = store.Resolve("etsy", "S1", Now, "TRY"); Assert.IsTrue(none.IsDefault); Assert.AreEqual((2, RoundingProfile.AwayFromZero), (none.Decimals, none.Mode)); StringAssert.Contains(none.Words, "profil yok"); Assert.AreEqual(137.51m, none.Apply(137.505m));
            Assert.AreEqual(0, store.Resolve("etsy", "S1", Now, "JPY").Decimals);

            // Revisions: each save is the next; a date resolves the revision in force then; the words carry the revision.
            var first = store.Save("Etsy", "S1", 2, RoundingProfile.ToEven, "TRY", "bankacı", Now.AddDays(-10));
            var second = store.Save("etsy", "S1", 0, RoundingProfile.Down, "TRY", "tam lira", Now.AddDays(-1));
            Assert.AreEqual((1, "etsy"), (first.Revision, first.Channel)); Assert.AreEqual(2, second.Revision);
            CollectionAssert.AreEqual(new[] { 2, 1 }, store.List("etsy", "S1").Select(x => x.Revision).ToArray(), "newest first");
            var today = store.Resolve("etsy", "S1", Now, "TRY"); Assert.AreEqual(2, today.Profile!.Revision); Assert.AreEqual(137m, today.Apply(137.505m)); StringAssert.Contains(today.Words, "0 hane, Down · profil sürüm 2");
            var historical = store.Resolve("etsy", "S1", Now.AddDays(-5), "TRY"); Assert.AreEqual(1, historical.Profile!.Revision); Assert.AreEqual(137.50m, historical.Apply(137.505m)); StringAssert.Contains(historical.Words, "profil sürüm 1");
            Assert.IsTrue(store.Resolve("etsy", "S1", Now.AddDays(-20), "TRY").IsDefault, "before the first revision the default stood");
            Assert.IsTrue(store.Resolve("etsy", "S2", Now, "TRY").IsDefault, "another shop has its own profiles");

            // Restart.
            SqliteConnection.ClearAllPools();
            var reopened = new RoundingProfileStore(root); Assert.AreEqual(2, reopened.List("etsy", "S1").Count); Assert.AreEqual("tam lira", reopened.Resolve("etsy", "S1", Now, "TRY").Profile!.Note);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void ThePricingChainRoundsBytheProfileInForceAtTheDateAndNamesTheRevision()
    {
        var root = Path.Combine(Path.GetTempPath(), "rounding-chain-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var catalog = new CatalogStore(root);
            catalog.Import(new XmlSource { Id = "src", Name = "Src" }, new[] { new CatalogProduct { SourceId = "src", Sku = "SKU-1", Name = "Product", Cost = 100m, CostCurrency = "TRY", Stock = 10, Active = true } });
            var id = catalog.Products().Single().Id;
            // x*1.37505 = 137.505 TRY: the midpoint the #790 boundary test pins; no fees, so the gate is not what decides here.
            catalog.SavePricePolicy(new PricePolicy { Channel = "local", Shop = "s1", Formula = "x*1.37505", Currency = "TRY", TryPerUnit = 1, Enabled = true, CommissionPercent = 0, EstimatedShippingTry = 0, TransactionCostTry = 0, VatRatePercent = 0 });

            // Without a profile: two decimals, half away from zero -- as before -- and the preview says so.
            var before = catalog.PreviewPrice("local", "s1", id, new DateTimeOffset(Now));
            Assert.AreEqual(137.51m, before.Price); StringAssert.Contains(before.RoundingOrigin, "profil yok");

            // A profile change: half to even from a day ago, whole lira downward from now; the as-of date picks the revision and the words name it.
            var profiles = new RoundingProfileStore(root);
            profiles.Save("local", "s1", 2, RoundingProfile.ToEven, "TRY", "", Now.AddDays(-1));
            var even = catalog.PreviewPrice("local", "s1", id, new DateTimeOffset(Now.AddHours(-1)));
            Assert.AreEqual(137.50m, even.Price); StringAssert.Contains(even.RoundingOrigin, "2 hane, ToEven · profil sürüm 1");
            profiles.Save("local", "s1", 0, RoundingProfile.Down, "TRY", "tam lira", Now);
            var whole = catalog.PreviewPrice("local", "s1", id, new DateTimeOffset(Now));
            Assert.AreEqual(137m, whole.Price); StringAssert.Contains(whole.RoundingOrigin, "0 hane, Down · profil sürüm 2");

            // Historical calculation: a date before the first revision rounds as that day did.
            var historical = catalog.PreviewPrice("local", "s1", id, new DateTimeOffset(Now.AddDays(-3)));
            Assert.AreEqual(137.51m, historical.Price); StringAssert.Contains(historical.RoundingOrigin, "profil yok");

            // Currency precision holds in the chain: a USD rule with a two-decimal default and a whole-unit profile.
            catalog.SavePricePolicy(new PricePolicy { Channel = "local", Shop = "usd", Formula = "x*3.3335", Currency = "USD", TryPerUnit = 10m, Enabled = true, CommissionPercent = 0, EstimatedShippingTry = 0, TransactionCostTry = 0, VatRatePercent = 0, FxRateObservedUtc = new DateTimeOffset(Now) });
            Assert.AreEqual(33.34m, catalog.PreviewPrice("local", "usd", id, new DateTimeOffset(Now)).Price, "333.35 TRY / 10 = 33.335 -> 33.34 half away from zero");
            profiles.Save("local", "usd", 0, RoundingProfile.Up, "USD", "", Now);
            Assert.AreEqual(34m, catalog.PreviewPrice("local", "usd", id, new DateTimeOffset(Now)).Price);
            Assert.ThrowsException<ArgumentException>(() => profiles.Save("local", "usd", 3, RoundingProfile.Up, "USD", "", Now), "a profile never asks for more decimals than the currency carries");
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
