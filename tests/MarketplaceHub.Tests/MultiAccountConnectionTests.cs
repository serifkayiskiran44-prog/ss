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

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class MultiAccountConnectionTests
{
    static void WithRoot(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "multi-account-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void LegacyCredentialsMigrateByVerifiedIdentityAndLegacyFilesRemain() => WithRoot(root =>
    {
        CredentialStore.Save(new EtsyCredentials("etsy-key", "etsy-secret", "etsy-token", "303"), root);
        var trendyolPath = Path.Combine(root, "trendyol.bin");
        new TrendyolSettingsStore(trendyolPath).Save(new TrendyolSettings("101", "trendyol-key", "trendyol-secret", "101 - Test"));

        var migrated = new MarketplaceConnectionMigration(root).ImportLegacy();
        var connections = new MarketplaceConnectionStore(root).List(false);
        var etsy = connections.Single(x => x.Channel == "etsy" && x.ShopId == "303");
        var trendyol = connections.Single(x => x.Channel == "trendyol" && x.ShopId == "101");
        var vault = new MarketplaceCredentialVault(root);

        Assert.AreEqual(2, migrated.Count(x => x.State == MarketplaceConnectionMigrationState.Imported));
        Assert.AreEqual("303", vault.Load<EtsyCredentials>(etsy.Id, etsy.Channel, etsy.ShopId)!.ShopId);
        Assert.AreEqual("101", vault.Load<TrendyolSettings>(trendyol.Id, trendyol.Channel, trendyol.ShopId)!.SupplierId);
        Assert.IsTrue(File.Exists(Path.Combine(root, "credentials.bin")), "Legacy Etsy credentials must never be auto-deleted.");
        Assert.IsTrue(File.Exists(trendyolPath), "Legacy Trendyol credentials must never be auto-deleted.");
    });

    [TestMethod]
    public void LegacyMigrationIsRestartSafeAndIdempotent() => WithRoot(root =>
    {
        CredentialStore.Save(new EtsyCredentials("etsy-key", "etsy-secret", "etsy-token", "303"), root);
        var first = new MarketplaceConnectionMigration(root).ImportLegacy();
        var connection = new MarketplaceConnectionStore(root).List(false).Single();

        var second = new MarketplaceConnectionMigration(root).ImportLegacy();
        var reopened = new MarketplaceConnectionStore(root).List(false);

        Assert.AreEqual(1, first.Count(x => x.State == MarketplaceConnectionMigrationState.Imported));
        Assert.AreEqual(1, second.Count(x => x.State == MarketplaceConnectionMigrationState.AlreadyImported));
        Assert.AreEqual(connection.Id, reopened.Single().Id);
        Assert.AreEqual(connection.Revision, reopened.Single().Revision, "A retry must not rewrite connection metadata.");
    });

    [TestMethod]
    public void CorruptEtsyDoesNotBlockValidTrendyolMigrationOrWriteEtsyMarker() => WithRoot(root =>
    {
        File.WriteAllBytes(Path.Combine(root, "credentials.bin"), new byte[] { 1, 2, 3, 4 });
        var trendyolPath = Path.Combine(root, "trendyol.bin");
        new TrendyolSettingsStore(trendyolPath).Save(new TrendyolSettings("101", "trendyol-key", "trendyol-secret", "101 - Test"));

        var results = new MarketplaceConnectionMigration(root).ImportLegacy();
        var etsy = results.Single(x => x.Channel == "etsy");
        var trendyol = results.Single(x => x.Channel == "trendyol");
        var store = new MarketplaceConnectionStore(root);

        Assert.AreEqual(MarketplaceConnectionMigrationState.Failed, etsy.State);
        Assert.IsTrue(etsy.Detail.Length is > 0 and <= 200);
        Assert.AreEqual(MarketplaceConnectionMigrationState.Imported, trendyol.State);
        Assert.IsNull(store.CredentialMigration("etsy"));
        Assert.IsNotNull(store.CredentialMigration("trendyol"));
        Assert.IsTrue(File.Exists(Path.Combine(root, "credentials.bin")));
        Assert.IsTrue(File.Exists(trendyolPath));
    });

    [TestMethod]
    public void ConnectionSettingsCannotBypassTheStartupMigrationGate()
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                WithRoot(root =>
                {
                    File.WriteAllBytes(Path.Combine(root, "credentials.bin"), new byte[] { 1, 2, 3, 4 });
                    new TrendyolSettingsStore(Path.Combine(root, "trendyol.bin")).Save(new TrendyolSettings("101", "trendyol-key", "trendyol-secret", "101 - Test"));

                    _ = MarketplaceConnectionsPanel.Create(root);
                    var store = new MarketplaceConnectionStore(root);

                    Assert.IsNull(store.CredentialMigration("etsy"));
                    Assert.IsNull(store.CredentialMigration("trendyol"), "The settings panel must never import credentials outside the approved startup migration.");
                    Assert.IsTrue(File.Exists(Path.Combine(root, "credentials.bin")));
                    Assert.IsTrue(File.Exists(Path.Combine(root, "trendyol.bin")));
                });
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    [TestMethod]
    public void VaultResolutionUsesConnectionIdForAccountsWithTheSameChannel() => WithRoot(root =>
    {
        var store = new MarketplaceConnectionStore(root);
        var first = store.Save("etsy", "303", "Etsy A", true);
        var second = store.Save("etsy", "404", "Etsy B", true);
        var vault = new MarketplaceCredentialVault(root);
        vault.Save(first.Id, first.Channel, first.ShopId, new EtsyCredentials("key-a", "secret-a", "token-a", "303"));
        vault.Save(second.Id, second.Channel, second.ShopId, new EtsyCredentials("key-b", "secret-b", "token-b", "404"));

        Assert.AreEqual("key-a", MarketplaceConnectionsPanel.LoadCredentialsForProbe<EtsyCredentials>(root, first)!.Key);
        Assert.AreEqual("key-b", MarketplaceConnectionsPanel.LoadCredentialsForProbe<EtsyCredentials>(root, second)!.Key);
    });

    [TestMethod]
    public void ConnectionSettingsUseASimpleTurkishStoreFlowAndHideSeededPlaceholders()
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                WithRoot(root =>
                {
                    var store = new MarketplaceConnectionStore(root);
                    var trendyol = store.Save("trendyol", "101", "Trendyol A", true);
                    var panel = MarketplaceConnectionsPanel.Create(root);
                    var controls = Walk(panel).ToList();
                    var grid = controls.OfType<DataGrid>().Single(x => x.Name == "MarketplaceConnectionList");
                    var rows = grid.ItemsSource!.Cast<object>().ToArray();
                    var add = controls.OfType<Button>().Single(x => x.Name == "MarketplaceAddStore");
                    var editor = controls.OfType<FrameworkElement>().Single(x => x.Name == "MarketplaceStoreEditor");

                    Assert.AreEqual(1, rows.Length, "Unused seeded default channels must not crowd the user's store list.");
                    Assert.AreEqual(Visibility.Collapsed, editor.Visibility);
                    Assert.IsFalse(controls.OfType<TabControl>().Any(x => x.Name == "MarketplaceAccountTabs"));
                    Assert.IsFalse(controls.OfType<CheckBox>().Any(x => x.Content?.ToString()?.Contains("Active", StringComparison.Ordinal) == true));
                    add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.AreEqual(Visibility.Visible, editor.Visibility);
                    Assert.IsNotNull(controls.OfType<ComboBox>().SingleOrDefault(x => x.Name == "MarketplaceNewChannel"));
                    Assert.IsNotNull(controls.OfType<Button>().SingleOrDefault(x => x.Name == "MarketplaceSaveStore"));
                });
            }
            catch (Exception error) { failure = error; }
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
