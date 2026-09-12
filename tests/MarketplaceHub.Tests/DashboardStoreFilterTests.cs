using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #809 (DESIGN: Dashboard store filter persistence). The filter has to survive a restart, fall back visibly
// when the store it named is gone or switched off, and -- the security line -- never resolve to a store the
// operator cannot currently use, however the saved value got there.
[TestClass]
public sealed class DashboardStoreFilterTests
{
    static DashboardStoreOption[] Options(params (string Channel, string Shop, bool Enabled)[] rows) =>
        DashboardStoreFilter.Options(rows.Select(r => new DashboardStoreCandidate(r.Channel, r.Shop, r.Channel + " / " + r.Shop, r.Enabled)).ToArray()).ToArray();

    [TestMethod]
    public void TheListAlwaysOffersAllStoresFirstAndOnlyUsableStoresAfterIt()
    {
        var options = Options(("etsy", "shop-a", true), ("etsy", "shop-b", false), ("ozon", "shop-c", true));

        Assert.AreEqual(DashboardStoreFilter.AllStoresKey, options[0].Key);
        StringAssert.Contains(options[0].Label, "Tüm mağazalar");
        CollectionAssert.AreEqual(new[] { DashboardStoreFilter.AllStoresKey, "etsy|shop-a", "ozon|shop-c" }, options.Select(o => o.Key).ToArray(),
            "A disabled connection is not something the operator can filter to, so it is not offered.");
    }

    [TestMethod]
    public void ASavedStoreIsRestoredAfterARestart()
    {
        var options = Options(("etsy", "shop-a", true), ("ozon", "shop-c", true));

        var restored = DashboardStoreFilter.Resolve("ozon|shop-c", options);

        Assert.AreEqual("ozon|shop-c", restored.Selected.Key);
        Assert.IsFalse(restored.FellBack);
        Assert.AreEqual("", restored.Notice);
        Assert.AreEqual("ozon / shop-c", restored.Selected.Label);
    }

    [TestMethod]
    public void ADeletedOrDisabledStoreFallsBackToAllStoresAndSaysWhy()
    {
        var options = Options(("etsy", "shop-a", true));

        var deleted = DashboardStoreFilter.Resolve("ozon|shop-gone", options);
        Assert.AreEqual(DashboardStoreFilter.AllStoresKey, deleted.Selected.Key, "A board must not sit on a filter that matches nothing.");
        Assert.IsTrue(deleted.FellBack);
        StringAssert.Contains(deleted.Notice, "tüm mağazalar");

        var disabled = DashboardStoreFilter.Resolve("etsy|shop-b", Options(("etsy", "shop-a", true), ("etsy", "shop-b", false)));
        Assert.AreEqual(DashboardStoreFilter.AllStoresKey, disabled.Selected.Key, "A switched-off connection is as unusable as a deleted one.");
        Assert.IsTrue(disabled.FellBack);
    }

    [TestMethod]
    public void NothingSavedOrAnEmptyInstallationResolvesToAllStoresWithoutComplaining()
    {
        var options = Options(("etsy", "shop-a", true));

        foreach (var saved in new[] { null, "", "   " })
        {
            var resolved = DashboardStoreFilter.Resolve(saved, options);
            Assert.AreEqual(DashboardStoreFilter.AllStoresKey, resolved.Selected.Key);
            Assert.IsFalse(resolved.FellBack, "Never having chosen a store is not a fallback.");
        }

        var empty = DashboardStoreFilter.Resolve("etsy|shop-a", Options());
        Assert.AreEqual(DashboardStoreFilter.AllStoresKey, empty.Selected.Key);
        Assert.AreEqual(1, Options().Length, "With no connections at all the list is still offerable.");
    }

    [TestMethod]
    public void AKeyThatWasNeverOfferedCannotBeSelectedHoweverItArrives()
    {
        var options = Options(("etsy", "shop-a", true));

        foreach (var forged in new[] { "etsy|shop-admin", "*", "all|*", "etsy|shop-a extra", "ETSY|SHOP-A" })
        {
            var resolved = DashboardStoreFilter.Resolve(forged, options);
            Assert.IsTrue(resolved.Selected.Key == DashboardStoreFilter.AllStoresKey || options.Any(o => o.Key == resolved.Selected.Key),
                $"'{forged}' resolved to a store that was never on the list: {resolved.Selected.Key}");
        }
        Assert.AreEqual("etsy|shop-a", DashboardStoreFilter.Resolve("etsy|shop-a", options).Selected.Key, "The exact offered key still works.");
    }

    [TestMethod]
    public void TheScopeLabelDescribesTheFilterForTheCardsThatQuoteIt()
    {
        var options = Options(("etsy", "shop-a", true));

        Assert.AreEqual("tüm mağazalar", DashboardStoreFilter.Resolve(null, options).Selected.Scope);
        Assert.AreEqual("etsy / shop-a", DashboardStoreFilter.Resolve("etsy|shop-a", options).Selected.Scope);
        Assert.IsFalse(DashboardStoreFilter.Resolve("etsy|shop-a", options).Selected.Scope.Contains('|'), "The scope is for reading, not the storage key.");
    }
}
