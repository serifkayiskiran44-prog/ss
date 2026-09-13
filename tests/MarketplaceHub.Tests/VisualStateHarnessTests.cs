using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #887 (TEST: WPF visual-state regression harness). Deterministic synthetic fixtures drive the dashboard, the
// product list, the import page, the order list, the channel matrix and settings into their empty, populated,
// loading and error states; each state is read as a semantic snapshot — empty states, error banners, busy, grid
// rows, actions, cut elements — at the default and the minimum width and across a DPI matrix, never as pixels:
// the snapshot must be the same at every scale, nothing may be cut, and nothing on screen may look like
// personal data or a credential.
[TestClass]
public sealed class VisualStateHarnessTests
{
    static readonly string[] Workspaces = { "dashboard", "products", "xml", "orders", "listing-matrix", "settings" };
    const double BaseWidth = 1440, BaseHeight = 900;
    const string LongName = "Şüpheli işlemlerin çözümlenmesi için özel üretim, çok uzun adlı, ölçülü ürün — sonbahar koleksiyonu, ğüşıöç";

    [TestMethod]
    public void SnapshotReadsStateNotPixelsAndAScaleKeepsTheDipRoom()
    {
        RunSta(() =>
        {
            var grid = new DataGrid { ItemsSource = new[] { new { A = 1 }, new { A = 2 } }, AutoGenerateColumns = true };
            var empty = new StackPanel(); EmptyStatePanel.Render(empty, EmptyState.Sources(), null, _ => { });
            var busy = new Grid { Tag = BusyOverlay.Tag, Visibility = Visibility.Collapsed };
            var texts = new StackPanel();
            texts.Children.Add(new TextBlock { Text = "Sipariş 1001 · 42,00 USD" });
            texts.Children.Add(new TextBlock { Text = "Müşteri: ayse.yilmaz@example.com" });
            var root = new StackPanel(); root.Children.Add(grid); root.Children.Add(empty); root.Children.Add(busy); root.Children.Add(texts); root.Children.Add(new Button { Content = "Kaydet" }); root.Children.Add(new Button { Content = "Sil", IsEnabled = false });
            var window = new Window { Content = root, Width = 600, Height = 400, Left = -4000, Top = -4000, ShowActivated = false };
            try
            {
                window.Show(); Drain(window);
                var s = VisualStateAudit.Snapshot(root, "fixture", 600, 1.0);
                CollectionAssert.AreEqual(new[] { "Henüz XML kaynağı yok" }, s.EmptyStates.ToList());
                CollectionAssert.AreEqual(new[] { 2 }, s.GridRows.ToList());
                Assert.IsFalse(s.Busy); Assert.AreEqual(2, s.Actions, "the empty state's own action and the enabled button; the disabled one is not an action");
                busy.Visibility = Visibility.Visible; Drain(window);
                Assert.IsTrue(VisualStateAudit.Snapshot(root, "fixture", 600, 1.0).Busy);
                StringAssert.Contains(s.Semantic, "rows:2"); Assert.IsFalse(s.StateOnly.Contains("overflow"), "the room-free part carries no cut count");

                // Personal data is found by the same redaction the audit trail uses; ordinary figures pass.
                var personal = VisualStateAudit.PersonalDataOnScreen(root);
                Assert.AreEqual(1, personal.Count, string.Join(" / ", personal)); StringAssert.Contains(personal[0], "Müşteri");

                // A scale grows the window's client area in step: the content keeps its DIP room, so nothing about the state changes.
                var before = VisualStateAudit.Snapshot(root, "fixture", 600, 1.0).Semantic;
                var roomBefore = root.ActualWidth;
                VisualStateAudit.ApplyDpiScale(window, 1.5, 600, 400); Drain(window);
                Assert.IsTrue(VisualStateAudit.HasRoom(window, 1.5, 600, 400), $"{window.ActualWidth}×{window.ActualHeight}");
                Assert.AreEqual(roomBefore, root.ActualWidth, 1, "the content's DIP room is unchanged under the scale");
                Assert.IsTrue(window.ActualWidth > 850, "the window itself grew: " + window.ActualWidth);
                Assert.AreEqual(before, VisualStateAudit.Snapshot(root, "fixture", 600, 1.5).Semantic);
                VisualStateAudit.ApplyDpiScale(window, 1.0, 600, 400); Drain(window);
                Assert.IsNull(root.LayoutTransform is ScaleTransform ? root.LayoutTransform : null);
                Assert.ThrowsException<ArgumentOutOfRangeException>(() => VisualStateAudit.ApplyDpiScale(window, 0, 600, 400));
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void EveryWorkspaceStateIsTheSameAcrossWidthsAndDpiScalesAndShowsNoPersonalData()
    {
        var emptyRoot = Path.Combine(Path.GetTempPath(), "visual-empty-" + Guid.NewGuid().ToString("N"));
        var fullRoot = Path.Combine(Path.GetTempPath(), "visual-full-" + Guid.NewGuid().ToString("N"));
        var ledger = Path.Combine(fullRoot, "notifications.db");
        RunSta(() =>
        {
            MainWindow window = null;
            var report = new StringBuilder();
            try
            {
                Directory.CreateDirectory(emptyRoot); Directory.CreateDirectory(fullRoot);
                SeedPopulated(fullRoot);

                // Empty: every workspace over an empty directory shows its true-empty state and nothing else.
                window = new MainWindow(emptyRoot); window.Show(); Drain(window);
                var emptySnapshots = Walk(window, "empty", report);
                // Every other registered screen, once at the default width: nothing on it may look like personal data either.
                var emptyTabs = (TabControl)window.FindName("ModuleTabs");
                var emptyRoutes = (Dictionary<string, TabItem>)typeof(MainWindow).GetField("routes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                foreach (var route in emptyRoutes.Keys.Where(k => !Workspaces.Contains(k)).ToList())
                {
                    Navigate(window, route); Settle(window, emptyTabs, route);
                    foreach (var text in VisualStateAudit.PersonalDataOnScreen(emptyTabs)) report.AppendLine($"empty {route}: personal data on screen: {text[..Math.Min(40, text.Length)]}");
                }
                Assert.IsTrue(emptySnapshots["products"].EmptyStates.Any(t => t.Contains("Henüz ürün yok")), "products empty: " + emptySnapshots["products"]);
                Assert.IsTrue(emptySnapshots["orders"].EmptyStates.Any(t => t.Contains("Henüz sipariş yok")), "orders empty: " + emptySnapshots["orders"]);
                Assert.IsTrue(emptySnapshots["xml"].EmptyStates.Any(t => t.Contains("XML kaynağı yok")), "xml empty: " + emptySnapshots["xml"]);
                Assert.AreEqual(1, emptySnapshots["dashboard"].EmptyStates.Count, "the dashboard ladder: " + emptySnapshots["dashboard"]);
                Assert.IsFalse(emptySnapshots.Values.Any(s => s.Errors.Count > 0 || s.Busy), "no error, nothing busy: " + string.Join("; ", emptySnapshots.Values));
                window.Close(); Drain(window); window = null;

                // Populated: the same walk over the fixture — rows in the grids, the true-empty states gone, the long Turkish names on screen uncut.
                window = new MainWindow(fullRoot); window.Show(); Drain(window);
                var tabs = (TabControl)window.FindName("ModuleTabs");
                var routes = (Dictionary<string, TabItem>)typeof(MainWindow).GetField("routes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var fullSnapshots = Walk(window, "populated", report);
                Assert.AreEqual(0, fullSnapshots["products"].EmptyStates.Count, "products populated: " + fullSnapshots["products"]);
                Assert.IsTrue(fullSnapshots["products"].GridRows.Contains(3), "three products: " + fullSnapshots["products"]);
                Assert.AreEqual(0, fullSnapshots["orders"].EmptyStates.Count, "orders populated: " + fullSnapshots["orders"]);
                Assert.IsTrue(fullSnapshots["orders"].GridRows.Contains(1), "one order: " + fullSnapshots["orders"]);
                Assert.IsFalse(fullSnapshots["xml"].EmptyStates.Any(t => t.Contains("XML kaynağı yok")), "two sources: " + fullSnapshots["xml"]);
                Assert.IsFalse(fullSnapshots.Values.Any(s => s.Errors.Count > 0 || s.Busy), "no error, nothing busy: " + string.Join("; ", fullSnapshots.Values));

                // Loading: a guarded shell operation in flight is the busy overlay — the same at every scale — and gone when it ends.
                Navigate(window, "products"); Settle(window, tabs, "products");
                var run = typeof(MainWindow).GetMethod("RunAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var gate = new TaskCompletionSource<bool>();
                var running = (Task)run.Invoke(window, new object[] { new Func<Task>(() => gate.Task) })!; Drain(window);
                var loading = Matrix(window, tabs, "products", "loading", report);
                Assert.IsTrue(loading.All(s => s.Busy), "loading is busy at every scale: " + string.Join("; ", loading));
                gate.SetResult(true); WaitUntil(window, () => running.IsCompleted, "the operation");
                Assert.IsFalse(VisualStateAudit.Snapshot(tabs, "products", BaseWidth, 1).Busy, "the overlay is gone");

                // Error, import: reading a source whose file is gone fails the download stage; the page stays whole and the same across scales.
                Navigate(window, "xml"); Settle(window, tabs, "xml");
                var sources = (ListBox)typeof(MainWindow).GetField("sources", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                sources.SelectedItem = sources.Items.OfType<XmlSource>().Single(s => s.Id == "feed-missing"); Drain(window);
                var inspect = (Task)typeof(MainWindow).GetMethod("InspectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
                WaitUntil(window, () => inspect.IsCompleted, "the failing read");
                Assert.IsTrue(inspect.IsFaulted, "a missing file fails the read");
                var progress = (ImportProgressState)typeof(MainWindow).GetField("importProgress", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                Assert.AreEqual(ImportProgressStage.Download, progress.Failed, "the progress panel shows the failed stage");
                var importError = Matrix(window, tabs, "xml", "error", report);
                Assert.IsFalse(importError.Any(s => s.Busy), "nothing stays busy after a failed read");

                // Error, dashboard: the notification ledger cannot be written, so a refresh fails into the shared banner at every scale, and the next refresh clears it.
                Navigate(window, "dashboard"); Settle(window, tabs, "dashboard");
                var refresh = Descendants((DependencyObject)routes["dashboard"].Content).OfType<Button>().Single(b => b.Content as string == "Durumu yenile");
                SqliteConnection.ClearAllPools(); File.SetAttributes(ledger, File.GetAttributes(ledger) | FileAttributes.ReadOnly);
                try
                {
                    refresh.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    WaitUntil(window, () => VisualStateAudit.Snapshot(tabs, "dashboard", BaseWidth, 1).Errors.Count == 1 && !VisualStateAudit.IsSettling(tabs), "the dashboard error banner");
                    var dashboardError = Matrix(window, tabs, "dashboard", "error", report);
                    Assert.IsTrue(dashboardError.All(s => s.Errors.Count == 1), "one banner at every scale: " + string.Join("; ", dashboardError));
                    Assert.IsFalse(VisualStateAudit.VisibleTexts(tabs).Any(t => t.Contains(fullRoot, StringComparison.OrdinalIgnoreCase)), "the banner never quotes the data directory");
                }
                finally { SqliteConnection.ClearAllPools(); File.SetAttributes(ledger, File.GetAttributes(ledger) & ~FileAttributes.ReadOnly); }
                refresh.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                WaitUntil(window, () => VisualStateAudit.Snapshot(tabs, "dashboard", BaseWidth, 1).Errors.Count == 0 && !VisualStateAudit.IsSettling(tabs), "the recovery");

                Assert.AreEqual("", report.ToString(), "Visual-state findings:\n" + report);
            }
            finally
            {
                try { window?.Close(); if (window is not null) Drain(window); } catch (Exception) { }
                try { if (File.Exists(ledger)) File.SetAttributes(ledger, File.GetAttributes(ledger) & ~FileAttributes.ReadOnly); } catch (Exception) { }
                foreach (var root in new[] { emptyRoot, fullRoot })
                    for (var attempt = 0; attempt < 30; attempt++)
                    {
                        try { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
                        catch (IOException) { Thread.Sleep(300); }
                        catch (UnauthorizedAccessException) { Thread.Sleep(300); }
                    }
            }
        });
    }

    /// <summary>The synthetic fixture: long Turkish names, a store connection, a feed on disk and one whose file is gone, an order, a failed run, a failed audit row — no real person, no credential.</summary>
    static void SeedPopulated(string root)
    {
        var catalog = new CatalogStore(root);
        var feed = Path.Combine(root, "feed.xml");
        File.WriteAllText(feed, "<Products><Product><Code>S1</Code><Title>" + LongName + "</Title><Cost>4</Cost><Stock>30</Stock></Product></Products>");
        var source = new XmlSource { Id = "feed-1", Name = "Tedarikçi ana kataloğu — sonbahar 2026 (haftalık tam okuma)", Location = feed, ItemPath = "/Products/Product", Currency = "TRY", Fields = new Dictionary<string, string> { ["Sku"] = "Code", ["Name"] = "Title", ["Cost"] = "Cost", ["Stock"] = "Stock" } };
        catalog.SaveSource(source);
        catalog.SaveSource(new XmlSource { Id = "feed-missing", Name = "Kaybolmuş besleme", Location = Path.Combine(root, "gone.xml"), ItemPath = "/Products/Product", Currency = "TRY", Fields = new Dictionary<string, string> { ["Sku"] = "Code", ["Name"] = "Title" } });
        catalog.Import(source, new[]
        {
            new CatalogProduct { SourceId = source.Id, Sku = "SKU-ÇĞİÖŞÜ-0001", Name = LongName, Price = 1234.5m, Stock = 12, Currency = "TRY" },
            new CatalogProduct { SourceId = source.Id, Sku = "SKU-0002", Name = "Kısa ürün", Price = 10, Stock = 5, Currency = "EUR" },
            new CatalogProduct { SourceId = source.Id, Sku = "SKU-0003", Name = "İğne oyası şal", Price = 99, Stock = 0, Currency = "TRY" },
        });
        new MarketplaceConnectionStore(root).Save("etsy", "S1", "Etsy S1", enabled: true);
        new OrdersStore(root).SaveManual(new OrderSnapshot { Marketplace = "etsy", ShopId = "S1", OrderId = "1001", UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1), Total = 42, Currency = "USD", Items = [new() { Sku = "SKU-0002", Title = LongName, Quantity = 1 }] });
        new SyncStore(root).Enqueue(new SyncRequest("etsy", "stock", "SKU-0002", "v1", "S1"));
        var runs = new XmlRunStore(root); runs.Fail(runs.Start(source.Id), "Kaynak okunamadı: bağlantı zaman aşımına uğradı (30 sn) — " + new string('y', 120));
        new AuditStore(root).Append(new AuditEvent { Module = "import", Action = "run", Outcome = "Failed", Detail = "Sentetik hata kaydı; gerçek kişi veya gizli değer yok." });
        SqliteConnection.ClearAllPools();
    }

    /// <summary>Every workspace at the default and the minimum width and across the DPI matrix: nothing cut, nothing personal, the state the same at every scale.</summary>
    static Dictionary<string, VisualStateSnapshot> Walk(MainWindow window, string state, StringBuilder report)
    {
        var result = new Dictionary<string, VisualStateSnapshot>(StringComparer.Ordinal);
        var tabs = (TabControl)window.FindName("ModuleTabs");
        foreach (var route in Workspaces)
        {
            Navigate(window, route); Settle(window, tabs, route);
            foreach (var width in new[] { BaseWidth, window.MinWidth })
            {
                VisualStateAudit.ApplyDpiScale(window, 1.0, width, BaseHeight); Drain(window);
                var snapshot = VisualStateAudit.Snapshot(tabs, route, width, 1.0);
                if (snapshot.Overflow > 0) report.AppendLine($"{state} {route} @ {width:F0}: {snapshot.Overflow} cut: {string.Join("; ", OverflowAudit.Audit(tabs).Take(5))}");
                foreach (var text in VisualStateAudit.PersonalDataOnScreen(tabs)) report.AppendLine($"{state} {route} @ {width:F0}: personal data on screen: {text[..Math.Min(40, text.Length)]}");
                if (width == BaseWidth) result[route] = snapshot;
            }
            Matrix(window, tabs, route, state, report);
        }
        return result;
    }

    /// <summary>The DPI matrix at the default width: the state must not change with the scale; the cut count is compared only where the desktop gave the window its full size.</summary>
    static List<VisualStateSnapshot> Matrix(MainWindow window, TabControl tabs, string route, string state, StringBuilder report)
    {
        var snapshots = new List<VisualStateSnapshot>(); var roomy = new List<VisualStateSnapshot>();
        foreach (var scale in VisualStateAudit.DpiScales)
        {
            VisualStateAudit.ApplyDpiScale(window, scale, BaseWidth, BaseHeight); Drain(window);
            var snapshot = VisualStateAudit.Snapshot(tabs, route, BaseWidth, scale);
            snapshots.Add(snapshot);
            if (VisualStateAudit.HasRoom(window, scale, BaseWidth, BaseHeight)) roomy.Add(snapshot); else Console.WriteLine($"{state} {route} ×{scale}: the desktop refused {BaseWidth * scale:F0}×{BaseHeight * scale:F0} (got {window.ActualWidth:F0}×{window.ActualHeight:F0}); cut count not compared at this scale");
        }
        VisualStateAudit.ApplyDpiScale(window, 1.0, BaseWidth, BaseHeight); Drain(window);
        if (snapshots.Select(s => s.StateOnly).Distinct().Count() > 1) report.AppendLine($"{state} {route}: the state changes with the DPI scale: {string.Join(" || ", snapshots)}");
        if (roomy.Select(s => s.Semantic).Distinct().Count() > 1) report.AppendLine($"{state} {route}: the cut count changes with the DPI scale: {string.Join(" || ", roomy)}");
        return snapshots;
    }

    /// <summary>A screen has settled when no command is disabled for a running operation and three consecutive snapshots, spaced apart, agree.</summary>
    static void Settle(MainWindow window, TabControl tabs, string route)
    {
        string last = null; var agreed = 0;
        for (var i = 0; i < 400; i++)
        {
            Drain(window);
            var now = VisualStateAudit.Snapshot(tabs, route, window.Width, 1.0).Semantic;
            agreed = now == last ? agreed + 1 : 0;
            if (!VisualStateAudit.IsSettling(tabs) && agreed >= 2) return;
            last = now; Thread.Sleep(80);
        }
        Assert.Fail($"{route} never settled.");
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
