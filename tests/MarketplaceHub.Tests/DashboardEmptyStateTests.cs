using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #811 (DESIGN: Dashboard zero-data onboarding state). An empty board has four very different causes and one
// useless answer ("veri yok"). Each cause gets its own sentence and its own safe next step -- and the step is
// always a screen this build actually has, never an invented endpoint or capability.
[TestClass]
public sealed class DashboardEmptyStateTests
{
    static readonly string[] KnownRoutes = { "dashboard", "connections", "xml", "products", "orders" };
    static bool Known(string route) => KnownRoutes.Contains(route);

    static DashboardEmptyInput Full() => new()
    {
        Connections = 2, Sources = 1, SourcesEverRun = 1, SourcesWithSuccessfulFeed = 1,
        Products = 40, Orders = 3, Filtered = false, VisibleRecords = 2, ScopeLabel = "tüm mağazalar",
    };

    [TestMethod]
    public void EachEmptyReasonGetsItsOwnSentenceAndItsOwnNextStep()
    {
        var noStore = DashboardEmptyState.Evaluate(Full() with { Connections = 0, Sources = 0, SourcesEverRun = 0, SourcesWithSuccessfulFeed = 0, Products = 0, Orders = 0, VisibleRecords = 0 }, Known);
        var noSource = DashboardEmptyState.Evaluate(Full() with { Sources = 0, SourcesEverRun = 0, SourcesWithSuccessfulFeed = 0, Products = 0, Orders = 0 }, Known);
        var neverRun = DashboardEmptyState.Evaluate(Full() with { SourcesEverRun = 0, SourcesWithSuccessfulFeed = 0, Products = 0, Orders = 0 }, Known);
        var filtered = DashboardEmptyState.Evaluate(Full() with { Filtered = true, VisibleRecords = 0, ScopeLabel = "etsy / shop-a" }, Known);

        Assert.AreEqual(DashboardEmptyState.NoStore, noStore.Reason);
        Assert.AreEqual(DashboardEmptyState.NoSource, noSource.Reason);
        Assert.AreEqual(DashboardEmptyState.NeverRun, neverRun.Reason);
        Assert.AreEqual(DashboardEmptyState.FilteredEmpty, filtered.Reason);

        var all = new[] { noStore, noSource, neverRun, filtered };
        Assert.AreEqual(4, all.Select(x => x.Title).Distinct().Count(), "Four causes, four sentences -- not one 'veri yok' four times.");
        foreach (var state in all)
        {
            Assert.IsTrue(state.IsEmpty);
            Assert.IsTrue(state.HasAction, $"{state.Reason} leaves the operator with something safe to do.");
            Assert.AreNotEqual("", state.ActionLabel);
            CollectionAssert.Contains(KnownRoutes, state.Route, $"{state.Reason} points at a screen this build has.");
        }
        StringAssert.Contains(filtered.Detail, "etsy / shop-a", "The filtered case names the scope that is hiding the data.");
    }

    [TestMethod]
    public void ABoardWithDataSaysNothingAtAll()
    {
        var state = DashboardEmptyState.Evaluate(Full(), Known);

        Assert.AreEqual(DashboardEmptyState.None, state.Reason);
        Assert.IsFalse(state.IsEmpty, "An onboarding panel over a working board is noise.");
        Assert.IsFalse(state.HasAction);
    }

    [TestMethod]
    public void ASourceThatRanButNeverBroughtAFeedIsDisconnectedNotUnstarted()
    {
        var state = DashboardEmptyState.Evaluate(Full() with { SourcesEverRun = 1, SourcesWithSuccessfulFeed = 0, Products = 0, Orders = 0 }, Known);

        Assert.AreEqual(DashboardEmptyState.DisconnectedSource, state.Reason, "It has been tried; telling the operator to run it again explains nothing.");
        Assert.AreEqual("xml", state.Route);
        StringAssert.Contains(state.Detail, "bağlan", StringComparison.CurrentCultureIgnoreCase);
        Assert.AreNotEqual(DashboardEmptyState.Evaluate(Full() with { SourcesEverRun = 0, SourcesWithSuccessfulFeed = 0, Products = 0, Orders = 0 }, Known).Title, state.Title);
    }

    [TestMethod]
    public void AnActionThisBuildCannotPerformIsDroppedRatherThanOffered()
    {
        var state = DashboardEmptyState.Evaluate(Full() with { Sources = 0, SourcesEverRun = 0, SourcesWithSuccessfulFeed = 0, Products = 0, Orders = 0 }, route => route == "dashboard");

        Assert.AreEqual(DashboardEmptyState.NoSource, state.Reason, "The diagnosis does not change because the screen is missing.");
        Assert.IsFalse(state.HasAction, "A button that cannot open anything is worse than no button.");
        Assert.AreEqual("", state.ActionLabel);
        Assert.AreNotEqual("", state.Detail, "The operator still gets told what is wrong.");
    }

    [TestMethod]
    public void TheEmptyStateNeverInventsAnEndpointOrACapability()
    {
        var inputs = new[]
        {
            Full() with { Connections = 0, Sources = 0, SourcesEverRun = 0, SourcesWithSuccessfulFeed = 0, Products = 0, Orders = 0, VisibleRecords = 0 },
            Full() with { Sources = 0, SourcesEverRun = 0, SourcesWithSuccessfulFeed = 0, Products = 0, Orders = 0 },
            Full() with { SourcesEverRun = 0, SourcesWithSuccessfulFeed = 0, Products = 0, Orders = 0 },
            Full() with { SourcesEverRun = 1, SourcesWithSuccessfulFeed = 0, Products = 0, Orders = 0 },
            Full() with { Filtered = true, VisibleRecords = 0, ScopeLabel = "etsy / shop-a" },
        };

        foreach (var input in inputs)
        {
            var state = DashboardEmptyState.Evaluate(input, Known);
            var copy = $"{state.Title} {state.Detail} {state.ActionLabel}";
            foreach (var forbidden in new[] { "http", "api.", "/v1", "token", "endpoint" })
                Assert.IsFalse(copy.Contains(forbidden, StringComparison.OrdinalIgnoreCase), $"{state.Reason} copy must not name '{forbidden}': {copy}");
            Assert.IsTrue(!state.HasAction || Known(state.Route), "Every offered action opens a real local screen.");
        }
    }

    [TestMethod]
    public void TheSnapshotTellsARanButDisconnectedFeedApartFromOneNeverStarted()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "empty-state-" + Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = new TrMarketplaceHubDesktop.Catalog.CatalogStore(root);
            new MarketplaceConnectionStore(root).Save("allegro", "shop-a", "Allegro Shop", true);
            catalog.SaveSource(new TrMarketplaceHubDesktop.Catalog.XmlSource { Id = "never", Name = "Never run" });
            var service = new DashboardDataService(root);

            var untouched = service.Load(bypassCache: true);
            Assert.AreEqual(1, untouched.XmlSources);
            Assert.AreEqual(0, untouched.SourcesEverRun);
            Assert.AreEqual(DashboardEmptyState.NeverRun, FromSnapshot(untouched).Reason);

            catalog.SaveSource(new TrMarketplaceHubDesktop.Catalog.XmlSource { Id = "never", Name = "Never run", LastRunUtc = DateTime.UtcNow, LastStatus = "Failed" });
            var tried = service.Load(bypassCache: true);
            Assert.AreEqual(1, tried.SourcesEverRun);
            Assert.AreEqual(0, tried.SourcesWithSuccessfulFeed);
            Assert.AreEqual(DashboardEmptyState.DisconnectedSource, FromSnapshot(tried).Reason, "A feed that was tried and brought nothing is disconnected, not unstarted.");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { System.IO.Directory.Delete(root, true); } catch (System.IO.IOException) { }
        }
    }

    static DashboardEmptyStateView FromSnapshot(DashboardSnapshot snapshot) => DashboardEmptyState.Evaluate(new DashboardEmptyInput
    {
        Connections = snapshot.Connections.Count, Sources = snapshot.XmlSources, SourcesEverRun = snapshot.SourcesEverRun,
        SourcesWithSuccessfulFeed = snapshot.SourcesWithSuccessfulFeed, Products = snapshot.TotalProducts, Orders = snapshot.OpenOrders,
        Filtered = false, VisibleRecords = snapshot.Connections.Count,
    }, Known);

    [TestMethod]
    public void TheLadderDiagnosesTheFirstThingThatIsMissingNotTheLast()
    {
        // Nothing is set up at all: the answer is "connect a store", not "your filter is hiding everything".
        var nothing = DashboardEmptyState.Evaluate(new DashboardEmptyInput { Filtered = true, ScopeLabel = "etsy / shop-a" }, Known);
        Assert.AreEqual(DashboardEmptyState.NoStore, nothing.Reason);

        // A store and a working feed, but the import brought no rows: that is a run problem, not a setup problem.
        var ranEmpty = DashboardEmptyState.Evaluate(Full() with { Products = 0, Orders = 0 }, Known);
        Assert.AreEqual(DashboardEmptyState.NoRecords, ranEmpty.Reason);
        Assert.IsTrue(ranEmpty.IsEmpty);
        CollectionAssert.Contains(KnownRoutes, ranEmpty.Route);
    }
}
