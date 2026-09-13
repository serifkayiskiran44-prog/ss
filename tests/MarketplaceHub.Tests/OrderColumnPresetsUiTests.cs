using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #834 on the real OrdersPanel: the "Kolonlar" selector applies a preset's visibility and order without touching
// widths, a column the user hides flips the grid to Özel and becomes the custom layout, a rebuilt panel (restart)
// remembers the mode, a preset chosen afterwards leaves the custom layout untouched, and Özel restores it.
[TestClass]
public sealed class OrderColumnPresetsUiTests
{
    [TestMethod]
    public void PresetsApplyRestartRestoresAndTheCustomLayoutIsNeverOverwritten()
    {
        Run(root =>
        {
            var (panel, grid, combo) = Build(root);
            Assert.AreEqual("Özel", combo.SelectedItem, "Nothing remembered: the user's own layout.");
            Assert.IsTrue(grid.Columns.All(c => c.Visibility == Visibility.Visible)); Assert.IsTrue(combo.Focusable);
            var orderIdWidth = grid.Columns.Single(c => Key(c) == "OrderId").Width.Value;

            combo.SelectedItem = "Kargo"; Drain();
            CollectionAssert.AreEqual(new[] { "OrderId", "ShopId", "DeliveryLabel", "SlaLabel", "Carriers", "TrackingNumbers", "SyncLabel" }, Visible(grid), "The shipping preset: its columns, in its order.");
            Assert.AreEqual(orderIdWidth, grid.Columns.Single(c => Key(c) == "OrderId").Width.Value, "A preset never resizes.");
            var prefs = new UiPreferenceStore(root);
            Assert.AreEqual("shipping", PreferenceSchema.Read(prefs, OrderColumnPresets.PresetPreferenceKey)); Assert.IsNull(PreferenceSchema.Read(prefs, OrderColumnPresets.CustomLayoutPreferenceKey), "No custom layout was written by choosing a preset.");

            // The user hides a column: that is theirs -- the grid is Özel now and the layout is the custom one.
            grid.Columns.Single(c => Key(c) == "SlaLabel").Visibility = Visibility.Collapsed; Drain();
            Assert.AreEqual("Özel", combo.SelectedItem);
            var custom = PreferenceSchema.Read(prefs, OrderColumnPresets.CustomLayoutPreferenceKey);
            Assert.IsNotNull(custom); Assert.IsFalse(DataGridLayoutCodec.Deserialize(custom)!.Columns.Single(c => c.Key == "SlaLabel").Visible);

            // Restart: the mode and the custom layout come back.
            var (panel2, grid2, combo2) = Build(root);
            Assert.AreEqual("Özel", combo2.SelectedItem);
            CollectionAssert.AreEqual(new[] { "OrderId", "ShopId", "DeliveryLabel", "Carriers", "TrackingNumbers", "SyncLabel" }, Visible(grid2), "The custom layout: the shipping columns minus the one the user hid.");

            combo2.SelectedItem = "Operasyon"; Drain();
            CollectionAssert.AreEqual(new[] { "Marketplace", "ShopId", "OrderId", "RawStatus", "PaymentStatus", "StockDecisionLabel", "DeliveryLabel", "SlaLabel", "UrgencyLabel" }, Visible(grid2));
            Assert.AreEqual(custom, PreferenceSchema.Read(prefs, OrderColumnPresets.CustomLayoutPreferenceKey), "Choosing a preset leaves the custom layout byte-for-byte as it was.");

            var (panel3, grid3, combo3) = Build(root);
            Assert.AreEqual("Operasyon", combo3.SelectedItem, "The preset survives a restart.");
            combo3.SelectedItem = "Özel"; Drain();
            CollectionAssert.AreEqual(new[] { "OrderId", "ShopId", "DeliveryLabel", "Carriers", "TrackingNumbers", "SyncLabel" }, Visible(grid3), "Özel restores the user's own layout.");
            Assert.IsFalse(grid3.Columns.Any(c => OrderColumnPresets.IsPii(Key(c))), "The order grid carries no PII column today; the preset rule would collapse one if it did.");
            GC.KeepAlive(panel); GC.KeepAlive(panel2); GC.KeepAlive(panel3);
        });
    }

    static (FrameworkElement Panel, DataGrid Grid, ComboBox Combo) Build(string root)
    {
        var panel = OrdersPanel.Create(root);
        var grid = Descendants(panel).OfType<DataGrid>().First();
        var combo = Descendants(panel).OfType<ComboBox>().Single(c => System.Windows.Automation.AutomationProperties.GetName(c) == "Kolon ön ayarı");
        Drain();
        return (panel, grid, combo);
    }

    static string Key(DataGridColumn c) => c is DataGridBoundColumn { Binding: System.Windows.Data.Binding b } ? b.Path.Path : c.Header?.ToString() ?? "";
    static string[] Visible(DataGrid grid) => grid.Columns.Where(c => c.Visibility == Visibility.Visible).OrderBy(c => c.DisplayIndex).Select(Key).ToArray();

    static void Drain() { for (var i = 0; i < 4; i++) Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { })); }

    static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is not DependencyObject d) continue;
            yield return d;
            foreach (var g in Descendants(d)) yield return g;
        }
    }

    static void Run(Action<string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "order-presets-ui-" + Guid.NewGuid().ToString("N"));
        Exception failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { test(root); }
            catch (Exception ex) { failure = ex; }
            finally
            {
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    try { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
                    catch (IOException) { Thread.Sleep(300); }
                    catch (UnauthorizedAccessException) { Thread.Sleep(300); }
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new AssertFailedException(failure.ToString());
    }
}
