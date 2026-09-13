using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #886 on the real main window: the product list, the order list, the XML source list and the report catalogue show
// the shared empty state — a true empty with a next step that opens a screen this build has, a filtered empty with
// the workspace's own clear action — and the dashboard's ladder renders through the same shape; no image anywhere.
[TestClass]
public sealed class EmptyStateUiTests
{
    [TestMethod]
    public void EveryWorkspaceShowsTheSharedEmptyStateAndTellsFilteredFromTrueEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), "empty-state-window-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            MainWindow window = null;
            try
            {
                Directory.CreateDirectory(root);
                window = new MainWindow(root); window.Show(); Drain(window);
                var routes = (Dictionary<string, TabItem>)typeof(MainWindow).GetField("routes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var tabs = (TabControl)window.FindName("ModuleTabs");
                Border State(string route) => Descendants((DependencyObject)routes[route].Content).OfType<Border>().First(b => b.Tag as string == EmptyState.Tag && b.IsVisible);
                string Title(Border state) => Descendants(state).OfType<TextBlock>().Single(t => t.Tag as string == EmptyState.TitleTag).Text;
                Button Action(Border state) => Descendants(state).OfType<Button>().Single(b => b.Tag as string == EmptyState.ActionTag);

                // The dashboard: the ladder's first missing thing, in the shared shape.
                Navigate(window, "dashboard"); Drain(window);
                WaitUntil(window, () => Descendants((DependencyObject)routes["dashboard"].Content).OfType<Border>().Any(b => b.Tag as string == EmptyState.Tag && b.IsVisible), "the dashboard empty state");
                StringAssert.StartsWith(Title(State("dashboard")), "Henüz", "the ladder's first missing thing, in the shared shape"); Assert.IsFalse(Descendants(State("dashboard")).OfType<Image>().Any());

                // Products: an empty pool is a true empty whose action opens the XML screen.
                Navigate(window, "products"); Drain(window);
                var products = State("products");
                StringAssert.Contains(Title(products), "Henüz ürün yok"); Assert.IsFalse(Descendants(products).OfType<Image>().Any());
                Action(products).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(window);
                Assert.AreSame(routes["xml"], tabs.SelectedItem, "the action opens the XML screen");

                // XML sources: none yet — the true empty offers the new-source action.
                var sources = State("xml"); StringAssert.Contains(Title(sources), "XML kaynağı yok"); Assert.AreEqual("Yeni kaynak", Action(sources).Content as string);

                // Orders: none yet — the true empty points at the connections screen.
                Navigate(window, "orders"); Drain(window);
                var orders = State("orders"); StringAssert.Contains(Title(orders), "Henüz sipariş yok"); Assert.AreEqual("Mağaza bağlantılarını aç", Action(orders).Content as string);

                // Reports: a search that matches nothing is a filtered empty whose action clears the search.
                Navigate(window, "reports"); Drain(window);
                var reportSearch = Descendants((DependencyObject)routes["reports"].Content).OfType<TextBox>().Single(t => t.Tag as string == "report-search");
                reportSearch.Text = "zzzz-yok"; Drain(window);
                var reports = State("reports"); StringAssert.Contains(Title(reports), "Aramaya uyan rapor yok");
                Action(reports).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(window);
                Assert.AreEqual("", reportSearch.Text); Assert.IsFalse(Descendants((DependencyObject)routes["reports"].Content).OfType<Border>().Any(b => b.Tag as string == EmptyState.Tag && b.IsVisible), "the cards are back");

                // Products with rows and a search that matches nothing: a filtered empty whose action clears the search and the filters; the rows come back.
                var catalog = new CatalogStore(root); var source = new XmlSource { Id = "feed-1", Name = "Fixture feed" }; catalog.SaveSource(source);
                catalog.Import(source, Enumerable.Range(1, 2).Select(i => new CatalogProduct { SourceId = source.Id, Sku = $"SKU-{i}", Name = $"Ürün {i}", Price = 10, Stock = 30, Currency = "TRY" }).ToList());
                Navigate(window, "products"); Drain(window);
                typeof(MainWindow).GetMethod("RefreshProducts", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null); Drain(window);
                Assert.IsFalse(Descendants((DependencyObject)routes["products"].Content).OfType<Border>().Any(b => b.Tag as string == EmptyState.Tag && b.IsVisible), "two products, no empty state");
                var search = (TextBox)typeof(MainWindow).GetField("search", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                search.Text = "zzzz-yok"; Drain(window);
                WaitUntil(window, () => Descendants((DependencyObject)routes["products"].Content).OfType<Border>().Any(b => b.Tag as string == EmptyState.Tag && b.IsVisible), "the filtered empty state");
                var filtered = State("products"); StringAssert.Contains(Title(filtered), "uyan ürün yok");
                Action(filtered).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(window);
                Assert.AreEqual("", search.Text);
                var grid = (DataGrid)typeof(MainWindow).GetField("products", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                WaitUntil(window, () => grid.Items.Count == 2, "the rows back");
                Assert.IsFalse(Descendants((DependencyObject)routes["products"].Content).OfType<Border>().Any(b => b.Tag as string == EmptyState.Tag && b.IsVisible));
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
