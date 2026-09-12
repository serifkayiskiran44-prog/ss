using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #807 (DESIGN: Dashboard KPI freshness indicators). Every card has to say when its number was true, what it
// covers, and whether that is still current -- the failure this prevents is a dashboard that keeps showing
// yesterday's figures after a refresh fails, with nothing to tell the operator.
[TestClass]
public sealed class DashboardKpiFreshnessTests
{
    static readonly DateTime Now = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void FreshStaleAndMissingTimestampsEachReadDifferently()
    {
        var fresh = DashboardKpiFreshness.Describe(Now.AddMinutes(-5), Now, TimeSpan.FromHours(1), "yerel katalog");
        Assert.AreEqual(DashboardKpiFreshness.Fresh, fresh.State);
        Assert.IsFalse(fresh.IsStale);
        StringAssert.Contains(fresh.When, "5 dakika önce");
        Assert.AreEqual("yerel katalog", fresh.Scope);

        var stale = DashboardKpiFreshness.Describe(Now.AddHours(-9), Now, TimeSpan.FromHours(1), "yerel katalog");
        Assert.AreEqual(DashboardKpiFreshness.Stale, stale.State);
        Assert.IsTrue(stale.IsStale);
        StringAssert.Contains(stale.When, "9 saat önce");
        StringAssert.Contains(stale.Label, "eski", "A stale card says so in words, not only by colour.");

        var missing = DashboardKpiFreshness.Describe(null, Now, TimeSpan.FromHours(1), "yerel katalog");
        Assert.AreEqual(DashboardKpiFreshness.NoData, missing.State);
        Assert.IsTrue(missing.IsStale, "An unknown data time is not treated as current.");
        StringAssert.Contains(missing.When, "bilinmiyor");
        Assert.AreNotEqual(stale.Label, missing.Label, "'We know it is old' and 'we do not know' are different answers.");
    }

    [TestMethod]
    public void AFailedRefreshMarksEveryCardRatherThanLeavingYesterdaysNumbersLookingCurrent()
    {
        var afterFailure = DashboardKpiFreshness.AfterRefreshFailure(DashboardKpiFreshness.Describe(Now.AddMinutes(-1), Now, TimeSpan.FromHours(1), "yerel katalog"));

        Assert.IsTrue(afterFailure.IsStale, "The number on screen predates a refresh that did not succeed.");
        Assert.AreEqual(DashboardKpiFreshness.Stale, afterFailure.State);
        Assert.IsTrue(afterFailure.Label.Contains("yenilenemedi", StringComparison.CurrentCultureIgnoreCase), afterFailure.Label);
        Assert.AreEqual("yerel katalog", afterFailure.Scope, "The scope is still true; only the currency of the figure changed.");
    }

    [TestMethod]
    public void TheScopeDescribesCoverageAndCannotCarrySourceDetail()
    {
        var leaky = DashboardKpiFreshness.Describe(Now, Now, TimeSpan.FromHours(1), "https://feed.example/list.xml?key=abc123secret");

        Assert.IsFalse(leaky.Scope.Contains("abc123secret", StringComparison.Ordinal), leaky.Scope);
        Assert.IsFalse(leaky.Scope.Contains("feed.example", StringComparison.Ordinal), "A KPI card describes what it counts, not where the bytes came from: " + leaky.Scope);
        Assert.IsTrue(leaky.Scope.Length > 0);

        var normal = DashboardKpiFreshness.Describe(Now, Now, TimeSpan.FromHours(1), "3 mağaza · yerel kayıtlar");
        Assert.AreEqual("3 mağaza · yerel kayıtlar", normal.Scope, "An ordinary coverage description passes through untouched.");
    }

    [TestMethod]
    public void EachKpiGetsItsOwnDataTimeRatherThanTheSnapshotsRenderTime()
    {
        var snapshot = new DashboardSnapshot(
            TotalProducts: 10, ActiveProducts: 8, OutOfStockProducts: 1, FailedSyncs: 2, PendingSyncs: 0,
            OpenOrders: 3, StockWaitingOrders: 0, XmlSources: 1, LastXmlStatus: "Tamam", LastXmlUtc: Now.AddDays(-4),
            ConnectionIssues: 0, GeneratedUtc: Now, Connections: [], Notifications: [], OrderTrend: [])
        {
            ProductsAtUtc = Now.AddMinutes(-10),
            OrdersAtUtc = Now.AddDays(-3),
            SyncAtUtc = null,
        };

        var cards = DashboardKpiFreshness.ForSnapshot(snapshot, Now).ToDictionary(c => c.Key, c => c.Freshness);

        Assert.AreEqual(DashboardKpiFreshness.Fresh, cards["products"].State);
        Assert.AreEqual(DashboardKpiFreshness.Stale, cards["orders"].State, "Orders last moved three days ago; the render time does not make that figure current.");
        Assert.AreEqual(DashboardKpiFreshness.NoData, cards["sync"].State);
        Assert.AreEqual(DashboardKpiFreshness.Stale, cards["xml"].State);
        Assert.IsTrue(cards.Values.All(f => f.Scope.Length > 0), "Every card says what it covers.");
    }
}
