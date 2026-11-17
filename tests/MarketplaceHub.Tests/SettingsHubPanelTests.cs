using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Data.Sqlite;
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
public sealed class SettingsHubPanelTests
{
    [TestMethod]
    public void HubExposesNamedSectionsWithoutCredentialInputs() => InSta(root =>
    {
        var routes = new List<string>();
        var controller = new BackgroundAppController(root, Array.Empty<IBackgroundJob>());
        var panel = new SettingsHubPanel(root, controller, routes.Add);

        var sections = Walk(panel).OfType<ListBox>().Single(x => x.Name == "SettingsSections");
        CollectionAssert.AreEqual(
            new[] { "Genel", "Mağaza bağlantıları", "Hesap kuralları", "Zamanlama", "Yerel ayarlar", "Veri ve yedek", "Sağlık ve tanılama" },
            sections.Items.OfType<ListBoxItem>().Select(x => x.Content!.ToString()).ToArray());
        foreach (var name in new[] { "SettingsGeneral", "SettingsConnections", "SettingsAccountRules", "SettingsScheduling", "SettingsLocale", "SettingsData", "SettingsHealth" })
            Assert.AreEqual(1, Walk(panel).OfType<FrameworkElement>().Count(x => x.Name == name), $"Missing settings section {name}.");

        var text = string.Join(" ", Walk(panel).Select(x => x switch
        {
            TextBlock block => block.Text,
            Button button => button.Content?.ToString() ?? "",
            _ => ""
        }));
        Assert.IsFalse(text.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(text.Contains("token", StringComparison.OrdinalIgnoreCase));

        controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
    });

    [TestMethod]
    public void TrayPreferenceAndRouteActionsUseTheExistingStoresAndRoutes() => InSta(root =>
    {
        var routes = new List<string>();
        var controller = new BackgroundAppController(root, Array.Empty<IBackgroundJob>());
        var panel = new SettingsHubPanel(root, controller, routes.Add);
        var tray = Walk(panel).OfType<CheckBox>().Single(x => x.Name == "SettingsCloseToTray");

        Assert.IsTrue(tray.IsChecked == true);
        tray.IsChecked = false;
        Assert.IsFalse(controller.CloseToTray);
        tray.IsChecked = true;
        Assert.IsTrue(controller.CloseToTray);

        foreach (var pair in new[]
        {
            ("SettingsOpenConnections", "connections"), ("SettingsOpenMarketplaces", "marketplaces"),
            ("SettingsOpenPolicies", "policy-center"), ("SettingsOpenAutomation", "automation"),
            ("SettingsOpenSync", "sync"), ("SettingsOpenApiHealth", "api-health"),
            ("SettingsOpenReports", "reports")
        })
        {
            Walk(panel).OfType<Button>().Single(x => x.Name == pair.Item1).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreEqual(pair.Item2, routes.Last());
        }

        controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
    });

    static void InSta(Action<string> action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "settings-hub-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try { action(root); }
            catch (Exception error) { failure = error; }
            finally
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
                SqliteConnection.ClearAllPools();
                try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                catch (Exception error) { failure ??= error; }
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
