using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #868 on the real main window: the product page search, the orders search and the sync centre's query find
// Turkish text whatever the case of the i (dotted, dotless, capital or ASCII), an empty query shows everything,
// and none of them writes the query into the audit trail.
[TestClass]
public sealed class UiSearchUiTests
{
    [TestMethod]
    public void TheProductOrderAndSyncSearchesFindTurkishTextWhateverTheCaseOfTheI()
    {
        var root = Path.Combine(Path.GetTempPath(), "ui-search-window-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            MainWindow window = null;
            try
            {
                Directory.CreateDirectory(root);
                var store = new CatalogStore(root); var source = new XmlSource { Id = "feed-1", Name = "Fixture feed" };
                store.Import(source, new[]
                {
                    new CatalogProduct { SourceId = source.Id, Sku = "SKU-1", Name = "İstanbul Çorap", Price = 10, Stock = 30, Currency = "TRY" },
                    new CatalogProduct { SourceId = source.Id, Sku = "SKU-2", Name = "ISTANBUL Terlik", Price = 10, Stock = 30, Currency = "TRY" },
                    new CatalogProduct { SourceId = source.Id, Sku = "SKU-3", Name = "Ankara Simit", Price = 10, Stock = 30, Currency = "TRY" },
                });
                var orders = new OrdersStore(root);
                orders.SaveManual(new OrderSnapshot { Marketplace = "etsy", ShopId = "shop-1", OrderId = "1001", Items = { new() { Title = "İstanbul Çorap", Sku = "SKU-1", Quantity = 1 } } });
                orders.SaveManual(new OrderSnapshot { Marketplace = "etsy", ShopId = "shop-1", OrderId = "1002", Items = { new() { Title = "Ankara Simit", Sku = "SKU-3", Quantity = 1 } } });
                var sync = new SyncStore(root); sync.Enqueue(new SyncRequest("etsy", "stock", "İSTANBUL-1", "v1")); sync.Enqueue(new SyncRequest("etsy", "stock", "ANKARA-1", "v1"));
                window = new MainWindow(root); window.Show(); Drain(window);
                var tabs = (TabControl)window.FindName("ModuleTabs");

                // Products: the search box drives the SQL search after its debounce.
                Navigate(window, "products"); Drain(window);
                var grid = (DataGrid)typeof(MainWindow).GetField("products", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var search = (TextBox)typeof(MainWindow).GetField("search", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                foreach (var (query, expected) in new[] { ("istanbul", 2), ("ISTANBUL", 2), ("ıstanbul", 2), ("İSTANBUL", 2), ("ankara", 1), ("", 3) })
                {
                    search.Text = query; WaitUntil(window, () => grid.Items.Count == expected, $"{expected} product(s) for '{query}'");
                }

                // Orders: the search box filters the loaded orders at once.
                Navigate(window, "orders"); Drain(window);
                var orderSearch = Descendants(tabs).OfType<TextBox>().First(t => t.ToolTip?.ToString()?.StartsWith("Sipariş, mağaza") == true);
                var orderGrid = Descendants(tabs).OfType<DataGrid>().First(g => g.Columns.Any(c => c is DataGridBoundColumn { Binding: System.Windows.Data.Binding b } && b.Path.Path == "OrderId"));
                foreach (var (query, expected) in new[] { ("istanbul", 1), ("ISTANBUL", 1), ("ıstanbul", 1), ("İstanbul", 1), ("ankara", 1), ("", 2) })
                {
                    orderSearch.Text = query; WaitUntil(window, () => orderGrid.Items.Count == expected, $"{expected} order(s) for '{query}'");
                }

                // Sync: the query box filters the jobs at once.
                Navigate(window, "sync"); Drain(window);
                var syncQuery = Descendants(tabs).OfType<TextBox>().First(t => t.ToolTip?.ToString()?.StartsWith("Kanal, işlem") == true);
                var jobs = Descendants(tabs).OfType<DataGrid>().First(g => g.Columns.Any(c => c is DataGridBoundColumn { Binding: System.Windows.Data.Binding b } && b.Path.Path == "EntityId"));
                foreach (var (query, expected) in new[] { ("istanbul", 1), ("ISTANBUL", 1), ("ıstanbul", 1), ("", 2) })
                {
                    syncQuery.Text = query; WaitUntil(window, () => jobs.Items.Count == expected, $"{expected} job(s) for '{query}'");
                }

                // No search wrote its query into the audit trail.
                search.Text = "ali@example.com"; orderSearch.Text = "ali@example.com"; syncQuery.Text = "ali@example.com"; Drain(window); Thread.Sleep(400); Drain(window);
                Assert.IsFalse(new AuditStore(root).List(500, null).Any(e => $"{e.Action} {e.Detail} {e.Outcome}".Contains("ali@example.com")), "a query is never logged");
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

    static void Navigate(MainWindow window, string key) => typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { key, true });
    static void Drain(Window window) { window.UpdateLayout(); for (var i = 0; i < 4; i++) window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { })); }

    static void WaitUntil(Window window, Func<bool> condition, string what)
    {
        for (var i = 0; i < 400; i++) { Drain(window); if (condition()) return; Thread.Sleep(25); }
        Assert.Fail($"Timed out waiting for {what}.");
    }

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
