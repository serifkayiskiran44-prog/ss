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

// #872 on the real main window: after the product list refreshes, the keyboard is back on the same product's cell;
// after that product is deleted and the list refreshes, on the row that took its place; while the search box has
// the keyboard, a refresh leaves it there; the orders grid keeps the keyboard on the same order across a filter
// change; the source list keeps it on the same source across a refresh. Everything is driven by the keyboard.
[TestClass]
public sealed class FocusRestoreUiTests
{
    [TestMethod]
    public void RefreshesPutTheKeyboardBackOnTheSameEntityOrItsNeighbourAndLeaveTheSearchBoxAlone()
    {
        var root = Path.Combine(Path.GetTempPath(), "focus-restore-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            MainWindow window = null;
            try
            {
                Directory.CreateDirectory(root);
                var store = new CatalogStore(root); var source = new XmlSource { Id = "feed-1", Name = "Fixture feed" }; var second = new XmlSource { Id = "feed-2", Name = "Second feed" }; store.SaveSource(source); store.SaveSource(second);
                store.Import(source, new[]
                {
                    new CatalogProduct { SourceId = source.Id, Sku = "SKU-1", Name = "Bir", Price = 10, Stock = 30, Currency = "TRY" },
                    new CatalogProduct { SourceId = source.Id, Sku = "SKU-2", Name = "İki", Price = 10, Stock = 30, Currency = "TRY" },
                    new CatalogProduct { SourceId = source.Id, Sku = "SKU-3", Name = "Üç", Price = 10, Stock = 30, Currency = "TRY" },
                });
                var orders = new OrdersStore(root);
                orders.SaveManual(new OrderSnapshot { Marketplace = "etsy", ShopId = "shop-1", OrderId = "1001", Items = { new() { Title = "Bir", Sku = "SKU-1", Quantity = 1 } } });
                orders.SaveManual(new OrderSnapshot { Marketplace = "etsy", ShopId = "shop-1", OrderId = "1002", Items = { new() { Title = "İki", Sku = "SKU-2", Quantity = 1 } } });
                window = new MainWindow(root); window.Show(); Drain(window);
                var tabs = (TabControl)window.FindName("ModuleTabs");

                // Products: the keyboard on the second product's second cell survives a refresh.
                Navigate(window, "products"); Drain(window);
                var products = (DataGrid)Field(window, "products");
                DataGridCell Cell(DataGrid grid, int row, int column) => (DataGridCell)grid.Columns[column].GetCellContent((DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(row))!.Parent;
                var target = Cell(products, 1, 1); target.Focus(); Drain(window); Assert.IsTrue(target.IsKeyboardFocused);
                var sku = ((CatalogProduct)target.DataContext).Sku;
                Invoke(window, "RefreshProducts"); Drain(window);
                var focused = Keyboard.FocusedElement as DataGridCell; Assert.IsNotNull(focused, "the keyboard is on a cell after the refresh");
                Assert.AreEqual(sku, ((CatalogProduct)focused!.DataContext).Sku, "the same product"); Assert.AreEqual(1, focused.Column.DisplayIndex, "the same column");
                // The product is deleted: the row that took its place.
                store.DeleteProduct((CatalogProduct)focused.DataContext); Invoke(window, "RefreshProducts"); Drain(window);
                focused = Keyboard.FocusedElement as DataGridCell; Assert.IsNotNull(focused, "the keyboard is still on a cell");
                Assert.AreNotEqual(sku, ((CatalogProduct)focused!.DataContext).Sku); Assert.AreEqual(((CatalogProduct)products.Items[Math.Min(1, products.Items.Count - 1)]).Sku, ((CatalogProduct)focused.DataContext).Sku, "the row at the same index");
                // The search box has the keyboard: a refresh leaves it there.
                var search = (TextBox)Field(window, "search"); search.Focus(); Drain(window); Assert.IsTrue(search.IsKeyboardFocused);
                Invoke(window, "RefreshProducts"); Drain(window);
                Assert.IsTrue(search.IsKeyboardFocused, "typing continues after a refresh");

                // Orders: a filter change rebinds the grid; the keyboard stays on the same order.
                Navigate(window, "orders"); Drain(window);
                var grid = Descendants(tabs).OfType<DataGrid>().First(g => g.Columns.Any(c => c is DataGridBoundColumn { Binding: System.Windows.Data.Binding b } && b.Path.Path == "OrderId"));
                var orderCell = Cell(grid, 1, 0); orderCell.Focus(); Drain(window); var orderId = ((OrderSnapshot)orderCell.DataContext).OrderId;
                var urgency = Descendants(tabs).OfType<CheckBox>().FirstOrDefault(c => c.Content?.ToString()?.Contains("Aciliyet") == true) ?? Descendants(tabs).OfType<CheckBox>().First();
                urgency.IsChecked = !(urgency.IsChecked ?? false); Drain(window);
                var orderFocused = Keyboard.FocusedElement as DataGridCell; Assert.IsNotNull(orderFocused, "the keyboard is on an order cell after the rebind");
                Assert.AreEqual(orderId, ((OrderSnapshot)orderFocused!.DataContext).OrderId, "the same order");

                // Sources: the keyboard on a source survives the list's refresh.
                Navigate(window, "xml"); Drain(window);
                var sources = (ListBox)Field(window, "sources"); var item = Descendants(sources).OfType<ListBoxItem>().First(i => i.DataContext is XmlSource x && x.Id == "feed-2");
                item.Focus(); Drain(window); Assert.IsTrue(item.IsKeyboardFocused);
                typeof(MainWindow).GetMethod("RefreshSources", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { false }); Drain(window);
                var sourceFocused = Keyboard.FocusedElement as ListBoxItem; Assert.IsNotNull(sourceFocused, "the keyboard is on a source after the refresh");
                Assert.AreEqual("feed-2", ((XmlSource)sourceFocused!.DataContext).Id, "the same source");
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
    static void Invoke(MainWindow window, string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
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
