using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #851 on the real dashboard: a product out of stock becomes a persisted "Uyarı · Ürünler" alert in the centre with
// focusable group and action buttons; acknowledging moves it to the acknowledged section and a refresh keeps it
// there while counting the repeat; restocking resolves it into the resolved tail; running out again reopens it,
// unacknowledged and marked; an empty centre says so. A thousand alerts render capped per group in reasonable time.
[TestClass]
public sealed class NotificationCenterUiTests
{
    [TestMethod]
    public void TheCentreGroupsAcknowledgesResolvesAndReopensOnTheRealDashboard()
    {
        var root = Path.Combine(Path.GetTempPath(), "alert-centre-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            try
            {
                Directory.CreateDirectory(root);
                var catalog = new CatalogStore(root);
                var source = new XmlSource { Id = Guid.NewGuid().ToString("N"), Name = "seed", Location = "https://seed.example.com/f.xml", ItemPath = "/p", PriceMode = "Simple", ExchangeRate = 1, AutoFx = false, Currency = "TRY", CostCurrency = "TRY", Fields = new Dictionary<string, string> { ["Sku"] = "s", ["Name"] = "n" } };
                catalog.SaveSource(source);
                catalog.Import(source, new List<CatalogProduct> { new() { Sku = "A", Name = "Kupa", Price = 5, Currency = "TRY", Stock = 0, Active = true, SourceId = source.Id, SourceKind = "xml", Description = "Uzun bir açıklama metni.", ImageUrls = "https://cdn.example.com/a.jpg" } });
                SqliteConnection.ClearAllPools();
                var navigated = new List<string>();
                var panel = DashboardPanel.Create(root, navigated.Add);
                var window = new Window { Content = panel, Width = 1300, Height = 950, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
                try
                {
                    window.Show(); Drain(window);
                    StackPanel Centre() => Descendants(panel).OfType<StackPanel>().Single(s => s.Children.OfType<TextBlock>().Any(t => (string?)t.Tag == "alerts-headline"));
                    string Headline() => Centre().Children.OfType<TextBlock>().Single(t => (string?)t.Tag == "alerts-headline").Text;
                    List<Expander> Groups(string section) => ((StackPanel)Centre().Children.OfType<StackPanel>().Single(s => (string?)s.Tag == section)).Children.OfType<Expander>().ToList();
                    string GroupHeader(Expander e) => ((TextBlock)e.Header).Text;
                    List<Button> Buttons(Expander e, string tag) => Descendants(e).OfType<Button>().Where(b => (string?)b.Tag == tag).ToList();
                    void Click(ButtonBase b) { b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(window); }
                    var refresh = Descendants(panel).OfType<Button>().Single(b => b.Content as string == "Durumu yenile");
                    void Refresh() { var before = Headline(); Click(refresh); WaitUntil(window, () => Descendants(panel).OfType<TextBlock>().Any(t => (string?)t.Tag == "alerts-headline") && refresh.IsEnabled, "the dashboard refresh"); }

                    // The board also lists every registry channel as an unconfigured connection warning; the product alert is the one this test follows.
                    Expander? ProductsGroup(string section) => Centre().Children.OfType<StackPanel>().Any(s => (string?)s.Tag == section) ? Groups(section).SingleOrDefault(e => GroupHeader(e).Contains("Uyarı · Ürünler")) : null;
                    LocalNotification Stock() => new NotificationStore(root).List().Single(a => a.Title == "Kritik stok");

                    WaitUntil(window, () => Descendants(panel).OfType<TextBlock>().Any(t => (string?)t.Tag == "alerts-headline"), "the first dashboard load");
                    StringAssert.Contains(Headline(), "açık uyarı:"); StringAssert.Contains(Headline(), "uyarı");
                    var initialOpen = Groups("alerts-unresolved").Count; Assert.IsTrue(initialOpen >= 1);
                    var products = ProductsGroup("alerts-unresolved")!; Assert.IsNotNull(products); StringAssert.Contains(GroupHeader(products), "Uyarı · Ürünler (1)"); Assert.IsTrue(products.IsExpanded && products.Focusable);
                    StringAssert.Contains(AutomationProperties.GetName(products), "Uyarı · Ürünler, 1 uyarı");
                    int RankOf(Expander e) => NotificationCenter.Rank(GroupHeader(e).Split(' ', 3)[1]); // "✖ Hata · …" -> the word after the glyph
                    Assert.IsTrue(Groups("alerts-unresolved").Zip(Groups("alerts-unresolved").Skip(1)).All(p => RankOf(p.First) <= RankOf(p.Second)), "Groups never go back up in severity.");
                    var row = Descendants(products).OfType<DockPanel>().Single(d => (string?)d.Tag == "alert-row"); Assert.IsTrue(Descendants(row).OfType<TextBlock>().Any(t => t.Text.Contains("Kritik stok") && t.Text.Contains("×1")), "The row carries the title and its sighting count.");
                    Assert.IsTrue(Buttons(products, "alert-open").Single().IsTabStop && Buttons(products, "alert-ack").Single().IsTabStop);
                    Click(Buttons(products, "alert-open").Single()); Assert.AreEqual("products", navigated.Last());
                    Assert.AreEqual("products", Stock().Source); Assert.IsTrue(Stock().IsOpen);

                    // Acknowledge: the alert moves to its own section; a refresh keeps it there and counts the repeat.
                    Click(Buttons(products, "alert-ack").Single());
                    Assert.AreEqual(initialOpen - 1, Groups("alerts-unresolved").Count); Assert.IsNull(ProductsGroup("alerts-unresolved")); StringAssert.Contains(Headline(), "1 onaylandı");
                    var acknowledged = ProductsGroup("alerts-acknowledged")!; Assert.IsNotNull(acknowledged); Assert.IsFalse(acknowledged.IsExpanded, "Acknowledged groups start collapsed.");
                    acknowledged.IsExpanded = true; Drain(window); Assert.AreEqual(1, Buttons(acknowledged, "alert-unack").Count);
                    Refresh();
                    acknowledged = ProductsGroup("alerts-acknowledged")!; Assert.IsNotNull(acknowledged); StringAssert.Contains(GroupHeader(acknowledged), "×2"); Assert.IsNull(ProductsGroup("alerts-unresolved"));
                    Assert.AreEqual(2, Stock().Occurrences); Assert.IsTrue(Stock().Acknowledged);

                    // Restock: the finding is no longer live, so it resolves into the tail.
                    var product = catalog.Products().Single(); product.Stock = 12; catalog.SaveProduct(product); SqliteConnection.ClearAllPools();
                    Refresh();
                    Assert.IsNull(ProductsGroup("alerts-unresolved")); Assert.IsNull(ProductsGroup("alerts-acknowledged"));
                    var resolved = Centre().Children.OfType<Expander>().Single(e => (string?)e.Tag == "alerts-resolved"); StringAssert.Contains((string)resolved.Header, "Çözülenler (1)"); StringAssert.Contains(Headline(), "1 çözüldü");
                    Assert.IsFalse(Stock().IsOpen);

                    // Out of stock again: reopened, unacknowledged, marked.
                    product.Stock = 0; catalog.SaveProduct(product); SqliteConnection.ClearAllPools();
                    Refresh();
                    products = ProductsGroup("alerts-unresolved")!; Assert.IsNotNull(products); StringAssert.Contains(GroupHeader(products), "×3");
                    Assert.IsTrue(Descendants(products).OfType<TextBlock>().Any(t => t.Text.Contains("yeniden açıldı ×1")), "The reopened row says so.");
                    Assert.IsTrue(Stock().IsOpen && !Stock().Acknowledged); Assert.AreEqual(1, Stock().Reopened);
                    Assert.AreEqual(initialOpen, Groups("alerts-unresolved").Count);
                }
                finally { window.Close(); }
            }
            finally { Cleanup(root); }
        });
    }

    [TestMethod]
    public void AThousandAlertsRenderCappedPerGroup()
    {
        RunSta(() =>
        {
            var t0 = DateTime.UtcNow;
            var alerts = Enumerable.Range(0, 1000).Select(i => new LocalNotification(Guid.NewGuid().ToString("N"), $"fp{i}", i % 3 == 0 ? "ERROR" : i % 3 == 1 ? "WARNING" : "INFO", i % 2 == 0 ? "etsy" : "", i % 2 == 0 ? $"S{i % 4}" : "", $"Uyarı {i:D4}", "token=abc detail", i % 10 == 0, t0.AddMinutes(-i), i % 5 == 0 ? "sync" : "products", i % 25 == 0 ? "Resolved" : "Open", 1 + i % 3, i % 25 == 0 ? t0.AddMinutes(-i) : null, 0)).ToList();
            var host = new StackPanel(); var window = new Window { Content = new ScrollViewer { Content = host }, Width = 1000, Height = 800, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
            try
            {
                window.Show();
                var clock = Stopwatch.StartNew();
                NotificationCenterPanel.Render(host, NotificationCenter.Build(alerts, t0), _ => { }, _ => { }, _ => { }, t0); Drain(window);
                clock.Stop();
                var groups = Descendants(host).OfType<Expander>().Where(e => (string?)e.Tag == "alert-group").ToList();
                Assert.IsTrue(groups.Count > 0);
                Assert.IsTrue(groups.All(g => Descendants(g).OfType<DockPanel>().Count(d => (string?)d.Tag == "alert-row") <= NotificationCenter.MaxPerGroup), "Rows are capped per group.");
                Assert.IsTrue(Descendants(host).OfType<TextBlock>().Any(t => (string?)t.Tag == "alert-more"), "The cap is named.");
                Assert.IsTrue(Descendants(host).OfType<TextBlock>().All(t => !t.Text.Contains("token=abc")), "The screen redacts once more, whatever source a row came from.");
                Assert.IsTrue(clock.ElapsedMilliseconds < 8000, $"{clock.ElapsedMilliseconds} ms");
                NotificationCenterPanel.Render(host, NotificationCenter.Build(Array.Empty<LocalNotification>(), t0), _ => { }, _ => { }, _ => { }, t0); Drain(window);
                Assert.IsTrue(Descendants(host).OfType<TextBlock>().Any(t => (string?)t.Tag == "alerts-empty"), "An empty centre says so."); Assert.AreEqual("Açık uyarı yok", Descendants(host).OfType<TextBlock>().Single(t => (string?)t.Tag == "alerts-headline").Text);
            }
            finally { window.Close(); }
        });
    }

    static void Cleanup(string root)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
            catch (IOException) { Thread.Sleep(300); }
            catch (UnauthorizedAccessException) { Thread.Sleep(300); }
        }
    }

    static void WaitUntil(Window window, Func<bool> condition, string what)
    {
        for (var i = 0; i < 400; i++) { Drain(window); if (condition()) return; Thread.Sleep(25); }
        Assert.Fail($"Timed out waiting for {what}.");
    }

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
