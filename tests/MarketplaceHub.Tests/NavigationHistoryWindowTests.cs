using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #889 in the real window: Back leaves a forward branch the "İleri ›" button and Alt+→ walk, the trail survives a
// restart with its record selected again, a record deleted in between degrades to its screen on the way back in,
// and a saved trail that names a store this session does not offer is left behind.
[TestClass]
public sealed class NavigationHistoryWindowTests
{
    [TestMethod]
    public void ForwardWalksTheBranchAndTheTrailSurvivesARestartWithinItsScope()
    {
        var root = Path.Combine(Path.GetTempPath(), "history-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            MainWindow window = null;
            try
            {
                var store = new CatalogStore(root);
                var source = new XmlSource { Id = "fixture", Name = "Fixture" };
                store.Import(source, new[] { new CatalogProduct { SourceId = source.Id, Sku = "A", Name = "Product A", Price = 10, Stock = 10 } });
                var product = store.Products("").Single();
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

                window = new MainWindow(root); window.Show(); Drain(window);
                Navigate(window, "dashboard"); Drain(window);
                var forwardButton = (Button)window.FindName("ForwardButton"); var backButton = (Button)window.FindName("BackButton");
                var breadcrumb = (TextBlock)window.FindName("BreadcrumbText");
                var grid = (DataGrid)typeof(MainWindow).GetField("products", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                Assert.IsFalse(forwardButton.IsEnabled); StringAssert.Contains(forwardButton.ToolTip?.ToString() ?? "", "Alt+→", "the forward button names its gesture");

                // Drill to the product: the crumb names it and the grid selects it.
                Drill(window, new DrillRequest(new DrillTarget("products", "Ürün yönetimi", DashboardStoreFilter.AllStoresKey, "product", product.Id, "SKU-A"), Array.Empty<string>()));
                Assert.AreEqual("products", CurrentRoute(window)); StringAssert.Contains(breadcrumb.Text, "SKU-A");
                Assert.IsFalse(forwardButton.IsEnabled, "nothing in front of the newest step");

                // Back leaves the branch; Alt+→ walks it and the product is selected again.
                backButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Drain(window);
                Assert.AreEqual("dashboard", CurrentRoute(window)); Assert.IsTrue(forwardButton.IsEnabled); Assert.IsFalse(backButton.IsEnabled);
                Assert.IsTrue(HandleShortcut(window, Key.Right, ModifierKeys.Alt), "Alt+→ is the forward gesture"); Drain(window);
                Assert.AreEqual("products", CurrentRoute(window)); StringAssert.Contains(breadcrumb.Text, "SKU-A");
                Assert.IsTrue(grid.SelectedItem is CatalogProduct selected && selected.Id == product.Id, "forward restores the selection the crumb names");
                Assert.IsFalse(forwardButton.IsEnabled); Assert.IsTrue(backButton.IsEnabled);
                Assert.IsFalse(HandleShortcut(window, Key.Right, ModifierKeys.Alt), "with nothing in front the gesture is not consumed");

                // A store switch drops the branch too.
                backButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Drain(window);
                Assert.IsTrue(forwardButton.IsEnabled);
                typeof(MainWindow).GetMethod("SwitchDashboardStore", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { "ozon|shop-c" }); Drain(window);
                Assert.IsFalse(forwardButton.IsEnabled, "the branch belonged to the store that was switched away from");
                typeof(MainWindow).GetMethod("SwitchDashboardStore", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { DashboardStoreFilter.AllStoresKey }); Drain(window);

                // Restart: the trail, with its record, comes back and opens where it was.
                Drill(window, new DrillRequest(new DrillTarget("products", "Ürün yönetimi", DashboardStoreFilter.AllStoresKey, "product", product.Id, "SKU-A"), Array.Empty<string>()));
                window.Close(); Drain(window); window = null; Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                window = new MainWindow(root); window.Show(); Drain(window);
                backButton = (Button)window.FindName("BackButton"); breadcrumb = (TextBlock)window.FindName("BreadcrumbText");
                grid = (DataGrid)typeof(MainWindow).GetField("products", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                Assert.AreEqual("products", CurrentRoute(window), "the restored trail decides the opening screen");
                Assert.IsTrue(backButton.IsEnabled); StringAssert.Contains(breadcrumb.Text, "SKU-A");
                WaitUntil(window, () => grid.SelectedItem is CatalogProduct s && s.Id == product.Id, "the restored selection");

                // Restart after the record was deleted: the screen stays, the record does not.
                window.Close(); Drain(window); window = null; Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                new CatalogStore(root).DeleteProduct(product); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                window = new MainWindow(root); window.Show(); Drain(window);
                backButton = (Button)window.FindName("BackButton"); breadcrumb = (TextBlock)window.FindName("BreadcrumbText");
                Assert.AreEqual("products", CurrentRoute(window)); Assert.IsTrue(backButton.IsEnabled);
                Assert.IsFalse(breadcrumb.Text.Contains("SKU-A"), "the crumb stops claiming the deleted record: " + breadcrumb.Text); StringAssert.Contains(breadcrumb.Text, "Ürün yönetimi");

                // Restart with a saved trail that names a store this session does not offer: the trail is left behind.
                window.Close(); Drain(window); window = null; Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                var preferences = PreferenceSchema.OpenStore(root);
                PreferenceSchema.Write(preferences, DrillHistoryCodec.PreferenceKey, DrillHistoryCodec.Serialize(new DrillHistoryState(new[] { new DrillTarget("dashboard", "Genel bakış", "etsy|shop-zzz"), new DrillTarget("products", "Ürün yönetimi", "etsy|shop-zzz") }, Array.Empty<DrillTarget>())));
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                window = new MainWindow(root); window.Show(); Drain(window);
                backButton = (Button)window.FindName("BackButton"); breadcrumb = (TextBlock)window.FindName("BreadcrumbText");
                Assert.IsFalse(backButton.IsEnabled, "a trail from a store this session cannot use never comes back");
                Assert.AreEqual("Genel bakış", breadcrumb.Text);
            }
            finally
            {
                try { window?.Close(); if (window is not null) Drain(window); } catch (Exception) { }
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
                    catch (IOException) { Thread.Sleep(300); }
                    catch (UnauthorizedAccessException) { Thread.Sleep(300); }
                }
            }
        });
    }

    static string CurrentRoute(MainWindow window) => (string)typeof(MainWindow).GetField("currentRoute", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    static void Navigate(MainWindow window, string key) => typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { key, true });
    static void Drill(MainWindow window, DrillRequest request) { typeof(MainWindow).GetMethod("DrillThrough", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { request }); Drain(window); }
    static bool HandleShortcut(MainWindow window, Key key, ModifierKeys modifiers) => (bool)typeof(MainWindow).GetMethod("HandleShortcut", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { key, modifiers })!;
    static void Drain(System.Windows.Window window) { window.UpdateLayout(); for (var i = 0; i < 4; i++) window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { })); }

    static void WaitUntil(System.Windows.Window window, Func<bool> condition, string what)
    {
        for (var i = 0; i < 400; i++) { Drain(window); if (condition()) return; Thread.Sleep(25); }
        Assert.Fail($"Timed out waiting for {what}.");
    }

    static void RunSta(Action body)
    {
        Exception failure = null;
        var thread = new Thread(() => { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); try { body(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new AssertFailedException(failure.ToString());
    }
}
