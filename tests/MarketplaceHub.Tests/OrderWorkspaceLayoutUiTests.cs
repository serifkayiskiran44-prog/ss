using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #836 on the real OrdersPanel hosted in a window: side by side at 1400 DIP with a focusable splitter and the
// detail column at the remembered width, stacked below 900 DIP and back, a drag persists the width and a rebuilt
// panel restores it, the selection survives a list rebind (search) and a reload (reveal), and a shop filter on
// another store closes the detail rather than keeping a wrong-store order open.
[TestClass]
public sealed class OrderWorkspaceLayoutUiTests
{
    [TestMethod]
    public void TheSplitResizesStacksWhenNarrowRemembersItsWidthAndTheSelectionSurvivesReloadsButNotAWrongStore()
    {
        Run(root =>
        {
            var store = new OrdersStore(root);
            store.SaveManual(Order("o-1", "S1")); store.SaveManual(Order("o-2", "S1")); store.SaveManual(Order("o-3", "S2"));
            Func<string, string, string, bool> reveal = null;
            var panel = OrdersPanel.Create(root, exposeReveal: r => reveal = r);
            var window = new Window { Content = panel, Width = 1400, Height = 800, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
            try
            {
                window.Show(); Drain(window);
                var layout = Descendants(panel).OfType<Grid>().Single(g => (string)g.Tag == "order-split");
                var splitter = Descendants(panel).OfType<GridSplitter>().Single();
                var grid = Descendants(panel).OfType<DataGrid>().First();
                // #837 docked a header above the body: the grid cell holds the detail host, the scroll sits inside it.
                var host = Descendants(panel).OfType<DockPanel>().Single(d => (string)d.Tag == "order-detail-host");
                var scroll = host.Children.OfType<ScrollViewer>().Single();
                Assert.AreEqual(3, layout.ColumnDefinitions.Count); Assert.AreEqual(0, layout.RowDefinitions.Count);
                Assert.AreEqual(OrderWorkspaceLayout.DefaultDetailWidth, layout.ColumnDefinitions[2].Width.Value, "Nothing remembered: the default detail width.");
                Assert.IsTrue(splitter.Focusable && splitter.KeyboardIncrement > 0, "The splitter is keyboard-operable.");
                Assert.IsTrue(layout.ColumnDefinitions[0].ActualWidth >= OrderWorkspaceLayout.MinListWidth, "The list keeps its minimum at 1400.");

                window.Width = 800; Drain(window);
                Assert.AreEqual(3, layout.RowDefinitions.Count); Assert.AreEqual(0, layout.ColumnDefinitions.Count, "Below 900 DIP the detail stacks under the list.");
                Assert.AreEqual(2, Grid.GetRow(host)); Assert.AreEqual(1, Grid.GetRow(splitter)); Assert.AreEqual(GridResizeDirection.Rows, splitter.ResizeDirection);
                window.Width = 1400; Drain(window);
                Assert.AreEqual(3, layout.ColumnDefinitions.Count); Assert.AreEqual(2, Grid.GetColumn(host)); Assert.AreEqual(GridResizeDirection.Columns, splitter.ResizeDirection);

                // A drag ends: the detail column's width is remembered in DIP, clamped.
                layout.ColumnDefinitions[2].Width = new GridLength(450); Drain(window);
                splitter.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent }); Drain(window);
                Assert.AreEqual("450", PreferenceSchema.Read(new UiPreferenceStore(root), OrderWorkspaceLayout.SplitPreferenceKey));

                // Selection persistence: a search rebinds the list; the same order stays selected and its detail stays open.
                var orders = ((IEnumerable<OrderSnapshot>)grid.ItemsSource).ToList();
                grid.SelectedItem = orders.Single(o => o.OrderId == "o-2"); Drain(window);
                string DetailText() => string.Join(" | ", Descendants(scroll).Select(d => d switch { TextBlock t => t.Text, TextBox b => b.Text, _ => "" }).Where(t => t.Length > 0));
                StringAssert.Contains(DetailText(), "o-2");
                var search = Descendants(panel).OfType<TextBox>().First(t => t.Width == 220);
                search.Text = "o-"; Drain(window);
                Assert.AreEqual("o-2", ((OrderSnapshot)grid.SelectedItem).OrderId, "A rebind keeps the selection by key."); StringAssert.Contains(DetailText(), "o-2");
                search.Text = ""; Drain(window);
                Assert.AreEqual("o-2", ((OrderSnapshot)grid.SelectedItem).OrderId);

                // A reload through the reveal path hands out fresh instances: the revealed order is selected and shown.
                Assert.IsTrue(reveal("etsy", "S1", "o-1")); Drain(window);
                Assert.AreEqual("o-1", ((OrderSnapshot)grid.SelectedItem).OrderId); StringAssert.Contains(DetailText(), "o-1");
                Assert.IsFalse(reveal("etsy", "S9", "o-1"), "An order in a store the list does not have is not revealed.");

                // Wrong store: filtering to S2 lists other orders; the S1 detail must not stay open.
                var shop = Descendants(panel).OfType<ComboBox>().Single(c => c.Width == 140);
                shop.SelectedItem = "S2"; Drain(window);
                Assert.IsNull(grid.SelectedItem, "The S1 order is not in the S2 list."); Assert.IsFalse(DetailText().Contains("o-1"), DetailText());
                StringAssert.Contains(DetailText(), "ayrıntı kapatıldı");
                grid.SelectedItem = ((IEnumerable<OrderSnapshot>)grid.ItemsSource).Single(); Drain(window);
                StringAssert.Contains(DetailText(), "o-3", "An order of the filtered store opens.");
            }
            finally { window.Close(); }

            // Restart: the remembered width comes back, clamped to the new workspace.
            var again = OrdersPanel.Create(root);
            var window2 = new Window { Content = again, Width = 1400, Height = 800, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
            try
            {
                window2.Show(); Drain(window2);
                var layout2 = Descendants(again).OfType<Grid>().Single(g => (string)g.Tag == "order-split");
                Assert.AreEqual(450, layout2.ColumnDefinitions[2].Width.Value, "The dragged width survived the restart.");
            }
            finally { window2.Close(); }
        });
    }

    static OrderSnapshot Order(string id, string shop) => new()
    {
        Marketplace = "etsy", ShopId = shop, OrderId = id, RawStatus = "paid", Items = new List<OrderItem> { new() { Title = "Kupa", Sku = "K1", Quantity = 1 } },
        Shipments = new List<OrderShipment> { new() { Id = "p1", Carrier = "test", State = "InTransit", Events = new List<OrderTrackingEvent> { new(DateTimeOffset.UtcNow.AddDays(-1), "Shipped", "test") } } },
    };

    static void Drain(Window window) { window.UpdateLayout(); for (var i = 0; i < 4; i++) window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { })); }

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
        var root = Path.Combine(Path.GetTempPath(), "order-split-ui-" + Guid.NewGuid().ToString("N"));
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
