using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class MarketplaceAccountNavigationTests
{
    [TestMethod]
    public void EnabledAccountsAreSeparateCardsWithAccountScopedCounts() => InSta(root =>
    {
        var connections = new MarketplaceConnectionStore(root);
        var first = connections.Save("trendyol", "101", "Trendyol A", true);
        var second = connections.Save("trendyol", "202", "Trendyol B", true);
        connections.Save("etsy", "303", "Disabled Etsy", false);
        var catalog = new CatalogStore(root);
        var pending = catalog.CreateManual(new() { Sku = "PENDING", Name = "Pending", Currency = "TRY" });
        var failed = catalog.CreateManual(new() { Sku = "FAILED", Name = "Failed", Currency = "TRY" });
        var bindings = new ProductChannelBindingStore(root);
        bindings.Save(Binding(pending.Id, first.Id, "pending-remote", "Pending"), 0);
        bindings.Save(Binding(failed.Id, first.Id, "failed-remote", "Error"), 0);

        var opened = new List<string>();
        var panel = new MarketplaceAccountHomePanel(root, opened.Add);
        var cards = Walk(panel).OfType<Button>().Where(x => x.Name.StartsWith("MarketplaceAccountCard_", StringComparison.Ordinal)).ToArray();

        CollectionAssert.AreEquivalent(new[] { first.Id, second.Id }, cards.Select(x => (string)x.Tag).ToArray());
        Assert.IsFalse(cards.Any(x => Equals(x.Tag, "303")), "Disabled accounts must not be operational cards.");
        var firstCard = cards.Single(x => Equals(x.Tag, first.Id));
        var model = (MarketplaceAccountCard)firstCard.DataContext;
        Assert.AreEqual(2, model.LinkedProductCount);
        Assert.AreEqual(1, model.PendingCount);
        Assert.AreEqual(1, model.ErrorCount);
        firstCard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        CollectionAssert.AreEqual(new[] { first.Id }, opened);
    });

    [TestMethod]
    public void RefreshAddsNewlySavedConnectionsWithoutRestart() => InSta(root =>
    {
        var store = new MarketplaceConnectionStore(root);
        var first = store.Save("etsy", "303", "Etsy A", true);
        var panel = new MarketplaceAccountHomePanel(root, _ => { });
        Assert.AreEqual(1, AccountCards(panel).Count);

        var second = store.Save("trendyol", "101", "Trendyol A", true);
        panel.Refresh();

        CollectionAssert.AreEquivalent(new[] { first.Id, second.Id }, AccountCards(panel).Select(x => (string)x.Tag).ToArray());
    });

    [TestMethod]
    public void WorkspaceHostRejectsMissingDisabledAndCorruptConnections() => InSta(root =>
    {
        var store = new MarketplaceConnectionStore(root);
        var disabled = store.Save("trendyol", "101", "Disabled", false);
        var corrupt = store.Save("etsy", "303", "Corrupt", true);
        using (var connection = new SqliteConnection("Data Source=" + Path.Combine(root, "catalog.db")))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE MarketplaceConnections SET LastTestUtc='not-a-date' WHERE Id=$id";
            command.Parameters.AddWithValue("$id", corrupt.Id);
            command.ExecuteNonQuery();
        }

        Assert.ThrowsException<InvalidOperationException>(() => MarketplaceWorkspaceHost.Create("missing", root));
        Assert.ThrowsException<InvalidOperationException>(() => MarketplaceWorkspaceHost.Create(disabled.Id, root));
        Assert.ThrowsException<MarketplaceConnectionCorruptException>(() => MarketplaceWorkspaceHost.Create(corrupt.Id, root));
    });

    [TestMethod]
    public void TwoTrendyolWorkspacesCarryExactConnectionIdentity() => InSta(root =>
    {
        var store = new MarketplaceConnectionStore(root);
        var first = store.Save("trendyol", "101", "Trendyol A", true);
        var second = store.Save("trendyol", "202", "Trendyol B", true);

        using var firstHost = MarketplaceWorkspaceHost.Create(first.Id, root);
        using var secondHost = MarketplaceWorkspaceHost.Create(second.Id, root);
        var firstWorkspace = (TrendyolWorkspacePanel)firstHost.Workspace;
        var secondWorkspace = (TrendyolWorkspacePanel)secondHost.Workspace;

        Assert.AreEqual(first.Id, firstHost.ConnectionId);
        Assert.AreEqual(second.Id, secondHost.ConnectionId);
        Assert.AreEqual(first.Id, firstWorkspace.ConnectionId);
        Assert.AreEqual(second.Id, secondWorkspace.ConnectionId);
        Assert.AreEqual("101", firstWorkspace.AccountShopId);
        Assert.AreEqual("202", secondWorkspace.AccountShopId);
    });

    [TestMethod]
    public void AccountSwitcherRebindsExactAccountAndDiscardsPriorWorkspace() => InSta(root =>
    {
        var store = new MarketplaceConnectionStore(root);
        var first = store.Save("trendyol", "101", "Trendyol A", true);
        var second = store.Save("trendyol", "202", "Trendyol B", true);
        using var host = MarketplaceWorkspaceHost.Create(first.Id, root);
        var prior = host.Workspace;
        var switcher = Walk(host).OfType<ComboBox>().Single(x => x.Name == "MarketplaceAccountSwitcher");

        switcher.SelectedValue = second.Id;

        Assert.AreEqual(second.Id, host.ConnectionId);
        Assert.AreNotSame(prior, host.Workspace);
        Assert.AreEqual(second.Id, ((TrendyolWorkspacePanel)host.Workspace).ConnectionId);
    });

    [TestMethod]
    public void RegistryCapabilitiesHideUnsupportedOrdersCommandForTrendyol() => InSta(root =>
    {
        var store = new MarketplaceConnectionStore(root);
        var connection = store.Save("trendyol", "101", "Trendyol A", true);
        var adapter = MarketplaceAdapterRegistry.Default.Get("trendyol");
        using var host = MarketplaceWorkspaceHost.Create(connection.Id, root);
        var commands = Walk(host).OfType<Button>().Where(x => x.Name.StartsWith("MarketplaceOperation_", StringComparison.Ordinal)).ToArray();

        Assert.AreEqual("trendyol", adapter.Channel);
        Assert.IsTrue(adapter.Capabilities.Supports(MarketplaceOperation.ProductsRead));
        Assert.IsFalse(adapter.Capabilities.Supports(MarketplaceOperation.OrdersRead));
        Assert.IsTrue(commands.Any(x => x.Name == "MarketplaceOperation_ProductsRead"));
        Assert.IsFalse(commands.Any(x => x.Name == "MarketplaceOperation_OrdersRead"));
    });

    [TestMethod]
    public void MainWindowStartsWithAccountHomeAndNoGlobalChannelWorkspace() => InSta(root =>
    {
        new MarketplaceConnectionStore(root).Save("etsy", "303", "Etsy A", true);
        var window = new MainWindow(root);
        try
        {
            var navigation = Walk(window).OfType<ListBox>().Single(x => x.Name == "NavigationList");
            Assert.IsTrue(navigation.Items.OfType<ListBoxItem>().Any(x => Equals(x.Tag, "marketplaces")));
            Assert.AreEqual(0, Walk(window).OfType<TrendyolWorkspacePanel>().Count());
            Assert.AreEqual(0, Walk(window).OfType<EtsyWorkspacePanel>().Count());
        }
        finally { window.Close(); }
    });

    [TestMethod]
    public void ChannelCompatibilityShortcutWaitsForExactCardSelectionWhenTwoAccountsShareAChannel() => InSta(root =>
    {
        var store = new MarketplaceConnectionStore(root);
        var first = store.Save("trendyol", "101", "Trendyol A", true);
        var second = store.Save("trendyol", "202", "Trendyol B", true);
        var window = new MainWindow(root);
        try
        {
            var navigation = Walk(window).OfType<ListBox>().Single(x => x.Name == "NavigationList");
            navigation.SelectedItem = navigation.Items.OfType<ListBoxItem>().Single(x => Equals(x.Tag, "trendyol"));

            Assert.AreEqual(0, Walk(window).OfType<TrendyolWorkspacePanel>().Count(), "A channel shortcut must not choose one of two accounts.");
            var cards = AccountCards(window);
            CollectionAssert.AreEquivalent(new[] { first.Id, second.Id }, cards.Select(x => (string)x.Tag).ToArray());

            cards.Single(x => Equals(x.Tag, second.Id)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            var workspace = Walk(window).OfType<TrendyolWorkspacePanel>().Single();
            Assert.AreEqual(second.Id, workspace.ConnectionId);
        }
        finally { window.Close(); }
    });

    [TestMethod]
    public void ConnectionPanelChannelLinkWaitsForExactCardSelectionWhenTwoAccountsShareAChannel() => InSta(root =>
    {
        var store = new MarketplaceConnectionStore(root);
        var first = store.Save("trendyol", "101", "Trendyol A", true);
        var second = store.Save("trendyol", "202", "Trendyol B", true);
        var window = new MainWindow(root);
        try
        {
            var navigation = Walk(window).OfType<ListBox>().Single(x => x.Name == "NavigationList");
            navigation.SelectedItem = navigation.Items.OfType<ListBoxItem>().Single(x => Equals(x.Tag, "connections"));
            Walk(window).OfType<Button>().Single(x => Equals(x.Content, "Trendyol")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.AreEqual(0, Walk(window).OfType<TrendyolWorkspacePanel>().Count(), "A channel link must open account selection, not the first account.");
            var cards = AccountCards(window);
            CollectionAssert.AreEquivalent(new[] { first.Id, second.Id }, cards.Select(x => (string)x.Tag).ToArray());

            cards.Single(x => Equals(x.Tag, first.Id)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.AreEqual(first.Id, Walk(window).OfType<TrendyolWorkspacePanel>().Single().ConnectionId);
        }
        finally { window.Close(); }
    });

    [TestMethod]
    public void SeededNotConfiguredDefaultsStayOutOfHomeSwitcherAndHost() => InSta(root =>
    {
        var store = new MarketplaceConnectionStore(root);
        var seeded = store.List().Where(x => x.Id == x.Channel + ":default" && x.Status == "NOT_CONFIGURED").ToArray();
        Assert.IsTrue(seeded.Length > 0);

        var home = new MarketplaceAccountHomePanel(root, _ => { });

        Assert.AreEqual(0, AccountCards(home).Count);
        foreach (var placeholder in seeded)
            Assert.ThrowsException<InvalidOperationException>(() => MarketplaceWorkspaceHost.Create(placeholder.Id, root));

        var operational = store.Save("trendyol", "101", "Trendyol A", true);
        using var host = MarketplaceWorkspaceHost.Create(operational.Id, root);
        var switcher = Walk(host).OfType<ComboBox>().Single(x => x.Name == "MarketplaceAccountSwitcher");
        CollectionAssert.AreEqual(new[] { operational.Id }, switcher.Items.Cast<MarketplaceConnection>().Select(x => x.Id).ToArray());
    });

    [TestMethod]
    public void MainWindowSeedingAfterHomeConstructionCannotPromoteDefaultPlaceholder() => InSta(root =>
    {
        var window = new MainWindow(root);
        try
        {
            var navigation = Walk(window).OfType<ListBox>().Single(x => x.Name == "NavigationList");
            navigation.SelectedItem = navigation.Items.OfType<ListBoxItem>().Single(x => Equals(x.Tag, "trendyol"));

            Assert.AreEqual(0, AccountCards(window).Count);
            Assert.AreEqual(0, Walk(window).OfType<TrendyolWorkspacePanel>().Count());
        }
        finally { window.Close(); }
    });

    static ProductChannelBinding Binding(string productId, string connectionId, string remoteId, string state) =>
        new(productId, connectionId, remoteId, remoteId + "-sku", remoteId + "-barcode", true, true, true, "", "", state, 0, default);

    static IReadOnlyList<Button> AccountCards(DependencyObject root) => Walk(root).OfType<Button>()
        .Where(x => x.Name.StartsWith("MarketplaceAccountCard_", StringComparison.Ordinal)).ToArray();

    static void InSta(Action<string> action)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "marketplace-navigation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try { action(root); }
            catch (Exception error) { failure = error; }
            finally
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
                for (var attempt = 0; attempt < 20; attempt++)
                {
                    SqliteConnection.ClearAllPools();
                    try { if (Directory.Exists(root)) Directory.Delete(root, true); break; }
                    catch (IOException) when (attempt < 19) { Thread.Sleep(50); }
                    catch (Exception error) { failure ??= error; break; }
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var item in Walk(child)) yield return item;
    }
}
