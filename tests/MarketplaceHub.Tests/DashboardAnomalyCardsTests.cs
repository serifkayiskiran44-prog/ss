using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #808 (DESIGN: Dashboard anomaly cards). The board listed anomalies as undifferentiated notification lines.
// A card has to answer three questions the line did not: what it costs, how long it has been true, and what to
// do next. Only states the app actually tracks are projected -- no invented anomaly types.
[TestClass]
public sealed class DashboardAnomalyCardsTests
{
    static readonly DateTime Now = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);

    static DashboardAnomalyInput Input(Action<DashboardAnomalyInput>? tweak = null)
    {
        var input = new DashboardAnomalyInput { Scope = "tüm mağazalar" };
        tweak?.Invoke(input); return input;
    }

    [TestMethod]
    public void ACleanBoardShowsNoCardsAndSaysWhyRatherThanLookingBroken()
    {
        var view = DashboardAnomalies.Project(Input(), Now);

        Assert.AreEqual(0, view.Cards.Count);
        Assert.IsFalse(view.HasAnomalies);
        StringAssert.Contains(view.Headline, "anomali yok");
    }

    [TestMethod]
    public void EachTrackedStateBecomesItsOwnCardWithImpactAgeAndNextAction()
    {
        var view = DashboardAnomalies.Project(Input(i =>
        {
            i.OversellRiskProducts = 4; i.OversellOldestUtc = Now.AddDays(-2);
            i.StaleSources = 1; i.StaleSourceOldestUtc = Now.AddDays(-9);
            i.FailedSyncJobs = 7; i.FailedSyncOldestUtc = Now.AddHours(-3);
            i.UnmappedOrders = 2; i.UnmappedOrderOldestUtc = Now.AddHours(-30);
        }), Now);

        CollectionAssert.AreEquivalent(new[] { "oversell", "stale-source", "failed-sync", "unmapped-order" }, view.Cards.Select(c => c.Key).ToArray());
        foreach (var card in view.Cards)
        {
            Assert.IsTrue(card.Impact.Length > 0, card.Key + " has no stated impact");
            Assert.IsTrue(card.NextAction.Length > 0, card.Key + " has no next action");
            Assert.IsTrue(card.Route.Length > 0, card.Key + " has nowhere to go");
            Assert.IsTrue(card.Age.Length > 0, card.Key + " has no age");
        }
        var oversell = view.Cards.Single(c => c.Key == "oversell");
        Assert.AreEqual(4, oversell.Count);
        StringAssert.Contains(oversell.Age, "2 gün");
        StringAssert.Contains(oversell.Impact, "satış");
        StringAssert.Contains(view.Headline, "4 anomali türü");
    }

    [TestMethod]
    public void CardsAreOrderedBySeverityThenByHowLongTheyHaveBeenTrue()
    {
        var view = DashboardAnomalies.Project(Input(i =>
        {
            i.UnmappedOrders = 1; i.UnmappedOrderOldestUtc = Now.AddMinutes(-5);
            i.OversellRiskProducts = 1; i.OversellOldestUtc = Now.AddMinutes(-5);
            i.StaleSources = 1; i.StaleSourceOldestUtc = Now.AddDays(-30);
            i.FailedSyncJobs = 1; i.FailedSyncOldestUtc = Now.AddMinutes(-1);
        }), Now);

        Assert.AreEqual("oversell", view.Cards[0].Key, "Overselling is the one that costs money; it leads regardless of age.");
        Assert.AreEqual(DashboardAnomalies.Critical, view.Cards[0].Severity);
        var warnings = view.Cards.Skip(1).ToList();
        Assert.IsTrue(warnings.All(c => c.Severity != DashboardAnomalies.Critical));
        Assert.AreEqual("stale-source", warnings[0].Key, "Among equals, the oldest problem comes first -- a month-old stale feed outranks a one-minute-old failure.");
    }

    [TestMethod]
    public void AnAnomalyThatHasBeenResolvedDisappearsInsteadOfLingeringAtZero()
    {
        var resolved = DashboardAnomalies.Project(Input(i => { i.FailedSyncJobs = 0; i.FailedSyncOldestUtc = Now.AddDays(-1); i.OversellRiskProducts = 2; i.OversellOldestUtc = Now; }), Now);

        Assert.AreEqual(1, resolved.Cards.Count, "A count of zero is not an anomaly, whatever timestamp is still lying around.");
        Assert.AreEqual("oversell", resolved.Cards.Single().Key);
    }

    [TestMethod]
    public void TheScopeIsStatedOnEveryCardAndCarriesNoIdentifiersOrSecrets()
    {
        var view = DashboardAnomalies.Project(Input(i =>
        {
            i.Scope = "https://feed.example/list.xml?key=abc123secret";
            i.FailedSyncJobs = 1; i.FailedSyncOldestUtc = Now;
        }), Now);

        var card = view.Cards.Single();
        Assert.IsFalse(card.Scope.Contains("abc123secret", StringComparison.Ordinal), card.Scope);
        Assert.IsFalse(card.Scope.Contains("feed.example", StringComparison.Ordinal), card.Scope);
        Assert.IsTrue(card.Scope.Length > 0);

        var scoped = DashboardAnomalies.Project(Input(i => { i.Scope = "shop-a"; i.FailedSyncJobs = 1; i.FailedSyncOldestUtc = Now; }), Now);
        Assert.AreEqual("shop-a", scoped.Cards.Single().Scope, "A shop filter's own label passes through, so a filtered board says what it is filtered to.");
    }

    [TestMethod]
    public void AnAnomalyWithNoKnownStartDoesNotClaimAnAge()
    {
        var view = DashboardAnomalies.Project(Input(i => { i.UnmappedOrders = 3; i.UnmappedOrderOldestUtc = null; }), Now);

        var card = view.Cards.Single();
        Assert.AreEqual(3, card.Count);
        StringAssert.Contains(card.Age, "bilinmiyor");
    }
}
