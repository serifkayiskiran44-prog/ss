using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #870 on the real main window: the product grid, the orders grid and the source list open a row-action menu from
// the keyboard at the focused row; the product menu carries the real commands (inspect with its gesture, activate,
// deactivate with the count, delete marked as confirmed and refused for a multi-selection, copy the SKU); the order
// menu opens the detail and copies numbers, with a reason when there is no tracking number; the source list's
// actions say to pick a source first when none is picked. Nothing in a menu bypasses a confirmation.
[TestClass]
public sealed class RowActionsUiTests
{
    [TestMethod]
    public void TheProductOrderAndSourceMenusOpenFromTheKeyboardWithTheirRealActionsAndReasons()
    {
        var root = Path.Combine(Path.GetTempPath(), "row-actions-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            MainWindow window = null;
            try
            {
                Directory.CreateDirectory(root);
                var store = new CatalogStore(root); var source = new XmlSource { Id = "feed-1", Name = "Fixture feed" }; store.SaveSource(source);
                store.Import(source, new[]
                {
                    new CatalogProduct { SourceId = source.Id, Sku = "SKU-1", Name = "Bir", Price = 10, Stock = 30, Currency = "TRY" },
                    new CatalogProduct { SourceId = source.Id, Sku = "SKU-2", Name = "İki", Price = 10, Stock = 30, Currency = "TRY" },
                });
                new OrdersStore(root).SaveManual(new OrderSnapshot { Marketplace = "etsy", ShopId = "shop-1", OrderId = "1001", Items = { new() { Title = "Bir", Sku = "SKU-1", Quantity = 1 } } });
                window = new MainWindow(root); window.Show(); Drain(window);
                var tabs = (TabControl)window.FindName("ModuleTabs");

                // Products: one selected — every action enabled, inspect carries its gesture, delete says a confirmation follows; inspect opens the drawer.
                Navigate(window, "products"); Drain(window);
                var products = (DataGrid)Field(window, "products"); products.SelectedIndex = 0; Drain(window);
                var row = (DataGridRow)products.ItemContainerGenerator.ContainerFromIndex(0); var cell = Descendants(row).OfType<DataGridCell>().First(); cell.Focus(); Drain(window);
                PressMenuKey(window); Drain(window);
                var menu = products.ContextMenu!; Assert.IsTrue(menu.IsOpen, "the menu key opens the product menu"); Assert.AreSame(row, menu.PlacementTarget);
                var items = menu.Items.OfType<MenuItem>().ToDictionary(i => (string)i.Tag, i => i);
                CollectionAssert.IsSubsetOf(new[] { "product-inspect", "product-activate", "product-deactivate", "product-delete", "product-copy-sku" }, items.Keys.ToList());
                Assert.AreEqual(KeyboardShortcuts.Find("product-inspect")!.Gesture, items["product-inspect"].InputGestureText, "the gesture comes from the catalogue");
                StringAssert.EndsWith((string)items["product-delete"].Header, "…", "delete says a confirmation follows"); Assert.IsTrue(items["product-delete"].IsEnabled); Assert.IsTrue(items["product-deactivate"].IsEnabled);
                var drawer = (FrameworkElement)Field(window, "productInspectDrawer");
                items["product-inspect"].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Drain(window);
                Assert.AreEqual(Visibility.Visible, drawer.Visibility, "inspect from the menu opens the drawer");
                menu.IsOpen = false; Drain(window); window.HandleShortcut(Key.Escape, ModifierKeys.None); Drain(window);
                // Two selected: deactivate carries the count, delete is refused with a reason.
                products.SelectedItems.Add(products.Items[1]); Drain(window); cell.Focus(); Drain(window);
                PressMenuKey(window); Drain(window);
                menu = products.ContextMenu!; Assert.IsTrue(menu.IsOpen); items = menu.Items.OfType<MenuItem>().ToDictionary(i => (string)i.Tag, i => i);
                StringAssert.Contains((string)items["product-deactivate"].Header, "(2)"); Assert.IsFalse(items["product-delete"].IsEnabled); StringAssert.Contains((string)items["product-delete"].ToolTip, "tek ürün");
                menu.IsOpen = false; Drain(window);

                // Orders: the detail and the numbers; no tracking number is a reason, not a silent no-op.
                Navigate(window, "orders"); Drain(window);
                var orders = Descendants(tabs).OfType<DataGrid>().First(g => g.Columns.Any(c => c is DataGridBoundColumn { Binding: System.Windows.Data.Binding b } && b.Path.Path == "OrderId"));
                orders.SelectedIndex = 0; Drain(window);
                var orderRow = (DataGridRow)orders.ItemContainerGenerator.ContainerFromIndex(0); Descendants(orderRow).OfType<DataGridCell>().First().Focus(); Drain(window);
                PressMenuKey(window); Drain(window);
                menu = orders.ContextMenu!; Assert.IsTrue(menu.IsOpen, "the order menu opens"); items = menu.Items.OfType<MenuItem>().ToDictionary(i => (string)i.Tag, i => i);
                Assert.IsTrue(items["order-open"].IsEnabled); Assert.IsTrue(items["order-copy-id"].IsEnabled);
                Assert.IsFalse(items["order-copy-tracking"].IsEnabled); StringAssert.Contains((string)items["order-copy-tracking"].ToolTip, "Takip numarası yok");
                Assert.IsFalse(items.Values.Any(i => ((string)i.Header).EndsWith("…")), "no order action is destructive");
                menu.IsOpen = false; Drain(window);

                // Sources: with a source picked the actions are live; with none picked they say to pick one first.
                Navigate(window, "xml"); Drain(window);
                var sources = (ListBox)Field(window, "sources"); sources.SelectedItem = sources.Items.OfType<XmlSource>().First(); Drain(window); sources.Focus(); Drain(window);
                PressMenuKey(window); Drain(window);
                menu = sources.ContextMenu!; Assert.IsTrue(menu.IsOpen, "the source menu opens"); items = menu.Items.OfType<MenuItem>().ToDictionary(i => (string)i.Tag, i => i);
                Assert.IsTrue(items["source-inspect"].IsEnabled); Assert.IsTrue(items["source-health"].IsEnabled);
                menu.IsOpen = false; Drain(window);
                sources.SelectedItem = null; Drain(window); sources.Focus(); Drain(window);
                PressMenuKey(window); Drain(window);
                menu = sources.ContextMenu!; Assert.IsTrue(menu.IsOpen); items = menu.Items.OfType<MenuItem>().ToDictionary(i => (string)i.Tag, i => i);
                Assert.IsTrue(items.Values.All(i => !i.IsEnabled && ((string)i.ToolTip).Contains("kaynak seçin")), "every source action says to pick a source first");
                menu.IsOpen = false; Drain(window);
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

    static object Field(MainWindow window, string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    static void PressMenuKey(Window window)
    {
        // A physical key reaches an element as a tunnelling preview key-down on the focused element (a key pushed through the input manager is only seen by its post-process handlers).
        var target = Keyboard.FocusedElement as UIElement ?? window;
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, Key.Apps) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    }
    static void Navigate(MainWindow window, string key) => typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { key, true });
    static void Drain(Window window) { window.UpdateLayout(); for (var i = 0; i < 4; i++) window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { })); }

    static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        var count = node is Visual ? VisualTreeHelper.GetChildrenCount(node) : 0;
        for (var i = 0; i < count; i++) { var child = VisualTreeHelper.GetChild(node, i); yield return child; foreach (var d in Descendants(child)) yield return d; }
    }

    static void RunSta(Action body)
    {
        Exception failure = null;
        var thread = new Thread(() => { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); try { body(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new AssertFailedException(failure.ToString());
    }
}
