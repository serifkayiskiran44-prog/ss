using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #878 on the real main window: the product list's first load is a cold "products" load and a search is a warm
// refresh; the orders page records its load at construction; the dashboard records its first load and the
// "Durumu yenile" click as a refresh; reading an XML source records an "import" load scoped by the source id; every
// row carries durations, counts and ids only — never a product name or a feed title.
[TestClass]
public sealed class UiLatencyUiTests
{
    [TestMethod]
    public void TheFourScreensRecordTheirLoadsAndRefreshesWithoutEntityContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "ui-latency-window-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            MainWindow window = null;
            try
            {
                Directory.CreateDirectory(root);
                var feed = Path.Combine(root, "feed.xml");
                File.WriteAllText(feed, "<Products>" + string.Concat(Enumerable.Range(1, 3).Select(i => $"<Product><Code>S{i}</Code><Title>Kırmızı Elbise {i}</Title><Cost>4</Cost><Stock>30</Stock><Desc>Uzun bir açıklama metni.</Desc><Img>https://cdn.example.com/{i}.jpg</Img></Product>")) + "</Products>");
                var catalog = new CatalogStore(root);
                var source = new XmlSource { Id = "feed-1", Name = "Fixture feed LEAKMARKER", Location = feed, ItemPath = "/Products/Product", PriceMode = "Simple", ExchangeRate = 1, AutoFx = false, Currency = "TRY", CostCurrency = "TRY", Fields = new Dictionary<string, string> { ["Sku"] = "Code", ["Name"] = "Title", ["Cost"] = "Cost", ["Stock"] = "Stock", ["Description"] = "Desc", ["ImageUrls"] = "Img" } };
                catalog.SaveSource(source);
                catalog.Import(source, Enumerable.Range(1, 2).Select(i => new CatalogProduct { SourceId = source.Id, Sku = $"SKU-{i}", Name = $"Kırmızı Elbise {i}", Price = 10, Stock = 30, Currency = "TRY" }).ToList());
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

                window = new MainWindow(root); window.Show(); Drain(window);
                var metrics = new LatencyStore(root);

                // The product list loaded at construction: a cold load of "products" with the page's count.
                WaitUntil(window, () => metrics.List(view: UiLatency.ProductsView).Any(m => m.Phase == LatencyPhase.Load && m.Outcome == LatencyOutcome.Completed), "the products load metric");
                var productsLoad = metrics.List(view: UiLatency.ProductsView).First(m => m.Phase == LatencyPhase.Load);
                Assert.IsFalse(productsLoad.Warm); Assert.AreEqual(2, productsLoad.Count);

                // A search is a warm refresh of the same view.
                Navigate(window, "products"); Drain(window);
                var search = (TextBox)typeof(MainWindow).GetField("search", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                search.Text = "SKU-1"; Drain(window);
                WaitUntil(window, () => metrics.List(view: UiLatency.ProductsView).Any(m => m.Phase == LatencyPhase.Refresh && m.Outcome == LatencyOutcome.Completed), "the products refresh metric");
                var productsRefresh = metrics.List(view: UiLatency.ProductsView).First(m => m.Phase == LatencyPhase.Refresh);
                Assert.IsTrue(productsRefresh.Warm); Assert.AreEqual(1, productsRefresh.Count);

                // The orders page loaded at construction.
                Assert.IsTrue(metrics.List(view: UiLatency.OrdersView).Any(m => m.Phase == LatencyPhase.Load && m.Outcome == LatencyOutcome.Completed), "the orders load metric");

                // The dashboard: its first load, then the refresh button.
                Navigate(window, "dashboard"); Drain(window);
                WaitUntil(window, () => metrics.List(view: UiLatency.DashboardView).Any(m => m.Phase == LatencyPhase.Load && m.Outcome == LatencyOutcome.Completed), "the dashboard load metric");
                var routes = (Dictionary<string, TabItem>)typeof(MainWindow).GetField("routes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var refresh = Descendants((DependencyObject)routes["dashboard"].Content).OfType<Button>().First(b => b.Content as string == "Durumu yenile");
                WaitUntil(window, () => refresh.IsEnabled, "the refresh button after the first load");
                refresh.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(window);
                WaitUntil(window, () => metrics.List(view: UiLatency.DashboardView).Any(m => m.Phase == LatencyPhase.Refresh && m.Outcome == LatencyOutcome.Completed), "the dashboard refresh metric");
                Assert.IsTrue(metrics.List(view: UiLatency.DashboardView).First(m => m.Phase == LatencyPhase.Refresh).Warm);

                // Reading an XML source: an "import" load scoped by the source id.
                Navigate(window, "xml"); Drain(window);
                var sources = (ListBox)typeof(MainWindow).GetField("sources", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                sources.SelectedItem = sources.Items.OfType<XmlSource>().Single(); Drain(window);
                var inspect = (Task)typeof(MainWindow).GetMethod("InspectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
                WaitUntil(window, () => inspect.IsCompleted, "the XML read");
                if (inspect.IsFaulted) throw inspect.Exception!.GetBaseException();
                var import = metrics.List(view: UiLatency.ImportView).Single(m => m.Phase == LatencyPhase.Load);
                Assert.AreEqual(LatencyOutcome.Completed, import.Outcome); Assert.AreEqual("feed-1", import.Scope); Assert.IsTrue(import.Count > 0, "the count is the number of fields found");

                // No row carries entity content: only ids, durations and counts.
                foreach (var row in metrics.List(500))
                {
                    Assert.IsTrue(row.DurationMs >= 0);
                    foreach (var text in new[] { row.View, row.Scope, row.Correlation })
                    {
                        Assert.IsFalse(text.Contains("Elbise", StringComparison.OrdinalIgnoreCase), text);
                        Assert.IsFalse(text.Contains("LEAKMARKER", StringComparison.Ordinal), text);
                        Assert.IsFalse(text.Contains(' '), text);
                    }
                }
                var check = new DiagnosticsService(root).Build().Checks.Single(c => c.Name == UiLatency.DiagnosticName);
                StringAssert.Contains(check.Detail, UiLatency.ImportView); Assert.IsFalse(check.Detail.Contains("Elbise", StringComparison.OrdinalIgnoreCase));
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
