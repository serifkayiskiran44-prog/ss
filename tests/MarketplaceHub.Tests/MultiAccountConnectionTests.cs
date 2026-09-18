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
    public void ConnectionSettingsExposeAccountTabsActiveStateAndDisabledCapabilityReasons()
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
                    var blocked = store.Save("amazon", "shop-a", "Amazon A", false);
                    var panel = MarketplaceConnectionsPanel.Create(root);
                    var controls = Walk(panel).ToList();
                    var tabs = controls.OfType<TabControl>().Single(x => x.Name == "MarketplaceAccountTabs");

                    Assert.IsTrue(tabs.Items.Cast<TabItem>().Any(x => Equals(x.Tag, trendyol.Id)));
                    Assert.IsTrue(tabs.Items.Cast<TabItem>().Any(x => Equals(x.Tag, blocked.Id)));
                    var activeStates = controls.OfType<CheckBox>().Where(x => x.Name.StartsWith("MarketplaceAccountActive", StringComparison.Ordinal)).ToList();
                    Assert.IsTrue(activeStates.Any(x => x.IsChecked == true));
                    Assert.IsTrue(activeStates.Any(x => x.IsChecked == false));
                    var unsupported = controls.OfType<CheckBox>().Where(x => x.Name.StartsWith("MarketplaceCapability_", StringComparison.Ordinal) && !x.IsEnabled).ToList();
                    Assert.IsTrue(unsupported.Count > 0);
                    Assert.IsTrue(unsupported.All(x => !string.IsNullOrWhiteSpace(x.ToolTip?.ToString())));
                    Assert.IsTrue(controls.OfType<GroupBox>().Any(x => Equals(x.Header, "Bağlantı")));
                    Assert.IsTrue(controls.OfType<GroupBox>().Any(x => Equals(x.Header, "Ürün kuralları")));
                    Assert.IsTrue(controls.OfType<GroupBox>().Any(x => Equals(x.Header, "Sipariş kuralları")));
                    Assert.IsTrue(controls.OfType<GroupBox>().Any(x => Equals(x.Header, "Senkronizasyon")));
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
