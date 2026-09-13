using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #875 on the real main window: a density saved before the envelope existed is brought forward and applied and is
// re-saved in the current envelope; a corrupt sidebar record and a last route from a future version fall back to
// the defaults without stopping the start-up, each named in the diagnostics without its payload; the migrated
// value is current for the next start (a restart), and a preference written by the window is an envelope.
[TestClass]
public sealed class PreferenceSchemaUiTests
{
    [TestMethod]
    public void OldCorruptAndFutureRecordsOpenTheWindowOnDefaultsAndMigrationsSurviveARestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "preference-schema-window-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            MainWindow window = null; MainWindow second = null;
            try
            {
                Directory.CreateDirectory(root);
                PreferenceSchema.ClearDiagnostics();
                var store = new UiPreferenceStore(root);
                store.Set("density:products", "compact");                              // written before the envelope existed
                store.Set("shell:sidebar", "{\"schema\":1,\"payload\":");               // corrupt
                store.Set("last-route", PreferenceSchema.Wrap(9, "orders"));            // from a future version

                window = new MainWindow(root); window.Show(); Drain(window);
                var tabs = (TabControl)window.FindName("ModuleTabs");
                var routes = (Dictionary<string, TabItem>)typeof(MainWindow).GetField("routes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                Assert.AreSame(routes["dashboard"], tabs.SelectedItem, "a future last route falls back to the default page");
                Assert.IsTrue(((ColumnDefinition)window.FindName("SidebarColumn")).ActualWidth > 100, "a corrupt sidebar record falls back to the expanded default");

                Navigate(window, "products"); Drain(window);
                var density = (ComboBox)typeof(MainWindow).GetField("productDensityBox", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                Assert.AreEqual(ProductListDensity.Label("compact"), density.SelectedItem?.ToString(), "the legacy density is applied");
                Assert.IsTrue(PreferenceSchema.TryUnwrap(store.Get("density:products")!, out var version, out var payload)); Assert.AreEqual(1, version); Assert.AreEqual("compact", payload);
                var diagnostics = PreferenceSchema.Diagnostics;
                Assert.IsTrue(diagnostics.Any(d => d.StartsWith("shell:sidebar:")), string.Join(" | ", diagnostics));
                Assert.IsTrue(diagnostics.Any(d => d.StartsWith("last-route:") && !d.Contains("orders")), "the future route is named without its payload");

                // What the window writes is an envelope of the current version.
                Navigate(window, "orders"); Drain(window);
                Assert.IsTrue(PreferenceSchema.TryUnwrap(store.Get("last-route")!, out var routeVersion, out var route)); Assert.AreEqual(1, routeVersion); Assert.AreEqual("orders", route);

                // A restart: the migrated density is current, the last route comes back.
                window.Close(); Drain(window); window = null;
                second = new MainWindow(root); second.Show(); Drain(second);
                Assert.AreEqual(PreferenceReadOutcome.Current, PreferenceSchema.Inspect(store, "density:products").Outcome);
                var secondTabs = (TabControl)second.FindName("ModuleTabs");
                var secondRoutes = (Dictionary<string, TabItem>)typeof(MainWindow).GetField("routes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(second)!;
                Assert.AreSame(secondRoutes["orders"], secondTabs.SelectedItem, "the last route written in the envelope is read back");
            }
            finally
            {
                try { window?.Close(); if (window is not null) Drain(window); } catch (Exception) { }
                try { second?.Close(); if (second is not null) Drain(second); } catch (Exception) { }
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
                    catch (IOException) { Thread.Sleep(300); }
                    catch (UnauthorizedAccessException) { Thread.Sleep(300); }
                }
            }
        });
    }

    static void Navigate(MainWindow window, string key) => typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { key, true });
    static void Drain(Window window) { window.UpdateLayout(); for (var i = 0; i < 4; i++) window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { })); }

    static void RunSta(Action body)
    {
        Exception failure = null;
        var thread = new Thread(() => { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); try { body(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new AssertFailedException(failure.ToString());
    }
}
