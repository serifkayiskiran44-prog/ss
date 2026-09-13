using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #846 on the real reports panel and the real shell: every catalog report is a focusable, named, fixed-DIP card;
// a real recorded run and a real saved filter change the card on refresh; without an offered store the
// store-scoped cards disappear and the count says so; search narrows and names a zero result; a card opens its
// owner route; fifty cards with a long name lay out in rows with the trimmed title on the card and the whole
// title in the tooltip. The shell now registers "reports", the parity audit is complete, and Navigate reaches it.
[TestClass]
public sealed class ReportsPanelUiTests
{
    [TestMethod]
    public void CardsReflectRealRunsAndFiltersHideWithoutAStoreSearchAndOpenTheirOwner()
    {
        var root = Path.Combine(Path.GetTempPath(), "reports-panel-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            try
            {
                Directory.CreateDirectory(root);
                var allowed = new List<string> { "etsy|S1", "trendyol|T1" }; var navigated = new List<string>();
                var panel = ReportsPanel.Create(root, navigated.Add, () => allowed);
                var window = new Window { Content = panel, Width = 1200, Height = 800, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
                try
                {
                    window.Show(); Drain(window);
                    var host = Descendants(panel).OfType<WrapPanel>().Single(w => (string?)w.Tag == "report-cards");
                    var count = Descendants(panel).OfType<TextBlock>().Single(t => (string?)t.Tag == "report-count");
                    var empty = Descendants(panel).OfType<StackPanel>().Single(t => (string?)t.Tag == "report-empty");
                    var search = Descendants(panel).OfType<TextBox>().Single(t => (string?)t.Tag == "report-search");
                    var refresh = Descendants(panel).OfType<Button>().Single(b => b.Content as string == "Yenile");
                    List<Button> Cards() => host.Children.OfType<Button>().ToList();
                    Button Card(string key) => Cards().Single(b => ((ReportCard)b.Tag).Key == key);
                    string RunText(Button b) => Descendants(b).OfType<TextBlock>().Single(t => (string?)t.Tag == "report-card-run").Text;

                    Assert.AreEqual(ReportCatalog.Definitions.Count, Cards().Count);
                    Assert.IsTrue(Cards().All(b => b.Focusable && b.IsTabStop && AutomationProperties.GetName(b).Length > 0 && b.Width == ReportsPanel.CardWidth && b.ToolTip is string));
                    Assert.IsTrue(Cards().All(b => RunText(b).Contains("Hiç çalıştırılmadı")), "Nothing has run yet.");
                    Assert.AreEqual(Visibility.Collapsed, empty.Visibility); StringAssert.StartsWith(count.Text, $"{ReportCatalog.Definitions.Count} rapor");
                    Card("products-xlsx").Focus(); Drain(window); Assert.IsTrue(Card("products-xlsx").IsKeyboardFocused, "A card takes keyboard focus.");

                    // A real run and a real saved filter change the cards on refresh.
                    new ReportRunStore(root).Record("products-xlsx", DateTime.UtcNow.AddSeconds(-30), ReportRunState.Succeeded, 42);
                    new UiPreferenceStore(root).SaveView(ReportCatalog.FilterModulePrefix + "orders", "Bugün", "{}");
                    refresh.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(window);
                    StringAssert.Contains(RunText(Card("products-xlsx")), "başarılı · 42 satır"); StringAssert.Contains(AutomationProperties.GetName(Card("products-xlsx")), "42 satır");
                    Assert.IsTrue(Descendants(Card("orders")).OfType<TextBlock>().Any(t => t.Text == "1 kayıtlı filtre"));

                    // No offered store: store-scoped cards go, the count says why.
                    allowed.Clear(); refresh.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(window);
                    Assert.AreEqual(ReportCatalog.Definitions.Count(d => d.Scope == ReportScope.Global), Cards().Count);
                    Assert.IsFalse(Cards().Any(b => ((ReportCard)b.Tag).Key == "listing-matrix")); StringAssert.Contains(count.Text, "mağaza kapsamlı rapor gizli");
                    allowed.Add("etsy|S1");

                    // Search narrows; a zero result is named; clearing restores.
                    search.Text = "destek"; Drain(window);
                    Assert.AreEqual(1, Cards().Count); Assert.AreEqual("support-package", ((ReportCard)Cards()[0].Tag).Key); StringAssert.Contains(count.Text, "aramaya uymadı");
                    search.Text = "böyle-bir-rapor-yok"; Drain(window);
                    Assert.AreEqual(0, Cards().Count); Assert.AreEqual(Visibility.Visible, empty.Visibility); StringAssert.Contains(Descendants(empty).OfType<TextBlock>().Single(t => (string?)t.Tag == EmptyState.TitleTag).Text, "Aramaya uyan rapor yok");
                    search.Text = ""; Drain(window);
                    Assert.AreEqual(ReportCatalog.Definitions.Count, Cards().Count); Assert.AreEqual(Visibility.Collapsed, empty.Visibility);

                    // A card selects its report: the setup opens beside the grid (#847) and "Ekranda aç" opens the owner screen.
                    Card("support-package").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(window);
                    var setupHost = Descendants(panel).OfType<Border>().Single(b => (string?)b.Tag == "report-setup-host");
                    Assert.AreEqual(Visibility.Visible, setupHost.Visibility); Assert.AreEqual(0, navigated.Count, "Selecting a card does not navigate by itself.");
                    Descendants(setupHost).OfType<Button>().Single(b => (string?)b.Tag == "report-param-open").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(window);
                    Assert.AreEqual("diagnostics", navigated.Last());

                    // Fifty cards, one with a long name: rows wrap, DIP width holds, the card shows the trimmed title and the tooltip the whole one.
                    var longTitle = new string('A', 90);
                    var fifty = ReportCatalog.Build(Enumerable.Range(0, 50).Select(i => new ReportDefinition($"r-{i:D2}", i == 0 ? longTitle : $"Rapor {i}", "Amaç", "Kaynak", ReportScope.Global, new[] { "CSV" }, "dashboard")), null, null, null, null, DateTime.UtcNow);
                    ReportsPanel.Render(host, fifty, _ => { }); Drain(window);
                    Assert.AreEqual(50, Cards().Count); Assert.IsTrue(Cards().All(b => Math.Abs(b.ActualWidth - ReportsPanel.CardWidth) < 0.5));
                    var first = Cards()[0]; var last = Cards()[49];
                    Assert.IsTrue(last.TranslatePoint(new Point(0, 0), host).Y > first.TranslatePoint(new Point(0, 0), host).Y, "Fifty cards wrap into rows.");
                    var title = Descendants(first).OfType<TextBlock>().Single(t => (string?)t.Tag == "report-card-title");
                    StringAssert.EndsWith(title.Text, "…"); Assert.AreEqual(ReportCatalog.TitleLimit, title.Text.Length); StringAssert.StartsWith((string)first.ToolTip, longTitle); StringAssert.StartsWith(AutomationProperties.GetName(first), longTitle);
                }
                finally { window.Close(); }
            }
            finally { Cleanup(root); }
        });
    }

    [TestMethod]
    public void TheShellRegistersTheReportsRouteAndTheParityAuditIsComplete()
    {
        var root = Path.Combine(Path.GetTempPath(), "reports-shell-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            MainWindow? window = null;
            try
            {
                var store = new CatalogStore(root);
                var source = new XmlSource { Id = Guid.NewGuid().ToString("N"), Name = "Fixture" };
                store.Import(source, new[] { new CatalogProduct { SourceId = source.Id, Sku = "A", Name = "Product A", Price = 10, Stock = 30 } });
                window = new MainWindow(root); window.Show(); Drain(window);
                var routes = (Dictionary<string, TabItem>)typeof(MainWindow).GetField("routes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                Assert.IsTrue(routes.ContainsKey("reports"), "The shell registers the reports workspace.");
                Assert.IsTrue(ScreenParityAudit.Evaluate(routes.Keys).IsComplete, "No required route is missing any more.");
                typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { "reports", true }); Drain(window);
                Assert.AreEqual("reports", (string?)typeof(MainWindow).GetField("currentRoute", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
                // A TabItem's content is presented by the TabControl, not under the TabItem's own visual tree.
                var host = Descendants((DependencyObject)routes["reports"].Content).OfType<WrapPanel>().Single(w => (string?)w.Tag == "report-cards");
                // The page gets the shell's own store list: with stores offered every report shows and a store-scoped card counts them; with none, only the global ones remain.
                var allowed = (IReadOnlyList<string>)typeof(MainWindow).GetMethod("AllowedStoreKeys", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
                var cards = host.Children.OfType<Button>().Select(b => (ReportCard)b.Tag).ToList();
                Assert.AreEqual(allowed.Count > 0 ? ReportCatalog.Definitions.Count : ReportCatalog.Definitions.Count(d => d.Scope == ReportScope.Global), cards.Count);
                if (allowed.Count > 0) Assert.AreEqual($"Kapsam: {allowed.Count:N0} sunulan mağaza · Siparişler", cards.Single(c => c.Key == "product-orders").ScopeText);
            }
            finally { window?.Close(); Cleanup(root); }
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
