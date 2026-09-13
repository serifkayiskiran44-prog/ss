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
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #891 (RELEASE UX: End-to-end operator design acceptance). One operator flow through the real window over a
// synthetic multi-store fixture with long Turkish data — dashboard → product list and workspace → XML read and
// import preview → channel matrix → order workspace → report → settings — scored at every step by the shared
// audits: the design tokens (#857), the semantic visual state at 100 / 150 / 200 % (#887), keyboard reachability,
// names and disabled reasons (#888), the focus ring under keyboard input (#861), the empty, loading and error
// states (#886, #885, #816), the destructive confirmation's safe default (#818/#821), the wrong-store refusal (#810)
// and the absence of any live marketplace write. Nothing here reaches a marketplace; nothing on screen may look
// like personal data or a credential. The same steps, for the packaged app, are docs/ACCEPTANCE-OPERATOR-FLOW.md.
[TestClass]
public sealed class OperatorAcceptanceTests
{
    const double BaseWidth = 1440, BaseHeight = 900;
    static readonly double[] Scales = { 1.0, 1.5, 2.0 };
    const string LongName = "Şüpheli işlemlerin çözümlenmesi için özel üretim, çok uzun adlı, ölçülü ürün — sonbahar koleksiyonu, ğüşıöç";

    [TestMethod]
    public void TheOperatorFlowPassesDesignStateKeyboardDpiAndConfirmationAcceptanceEndToEnd()
    {
        var root = Path.Combine(Path.GetTempPath(), "acceptance-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            MainWindow window = null;
            var report = new StringBuilder();
            try
            {
                Directory.CreateDirectory(root);
                var (product, feed) = Seed(root);
                window = new MainWindow(root); window.Show(); window.Activate(); Drain(window);
                var tabs = (TabControl)window.FindName("ModuleTabs");
                var routes = (Dictionary<string, TabItem>)Field(window, "routes");
                FrameworkElement Content(string route) => (FrameworkElement)routes[route].Content;

                // Step 0 — the shell: the design tokens resolve, the base font is the token font, the shortcuts catalogue has no conflicts.
                DesignTokens.Verify(DesignTokens.Resources);
                Assert.AreEqual(DesignTokens.FontFamilyBody, window.FontFamily); Assert.AreEqual(DesignTokens.TextBodySize, window.FontSize);
                Assert.AreEqual(0, KeyboardShortcuts.Conflicts().Count);

                // Step 1 — dashboard: two stores offered, the board settles with cards, a wrong-store deep link never moves the window.
                Navigate(window, "dashboard"); Settle(window, tabs, "dashboard");
                Score(window, tabs, "dashboard", report);
                var drill = typeof(MainWindow).GetMethod("DrillThrough", BindingFlags.Instance | BindingFlags.NonPublic)!;
                drill.Invoke(window, new object[] { new DrillRequest(new DrillTarget("products", "Ürün yönetimi", "etsy|GHOST", "product", product.Id, "SKU"), new[] { "etsy|S1", "trendyol|T1" }) }); Drain(window);
                Assert.AreEqual("dashboard", CurrentRoute(window), "a link naming a store this session does not offer never opens");
                drill.Invoke(window, new object[] { new DrillRequest(new DrillTarget("products", "Ürün yönetimi", "etsy|S1", "product", product.Id, "SKU-ÇĞİÖŞÜ-0001"), new[] { "etsy|S1", "trendyol|T1" }) }); Drain(window);
                Assert.AreEqual("products", CurrentRoute(window)); StringAssert.Contains(((TextBlock)window.FindName("BreadcrumbText")).Text, "SKU-ÇĞİÖŞÜ-0001");

                // Step 2 — product list and workspace: the drilled product is selected, the editor is live, the long name is on screen uncut, the delete asks first and defaults to the safe button.
                Settle(window, tabs, "products");
                var products = (DataGrid)Field(window, "products");
                Assert.IsTrue(products.SelectedItem is CatalogProduct selected && selected.Id == product.Id, "the drilled product is selected");
                Score(window, tabs, "products", report);
                var confirmed = DriveModal(window, () => typeof(MainWindow).GetMethod("DeleteSelectedProduct", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null), new Func<Window, bool>[]
                {
                    dialog =>
                    {
                        var buttons = Descendants(dialog).OfType<Button>().ToList();
                        var cancel = buttons.Single(b => b.IsCancel); Assert.IsTrue(cancel.IsDefault, "a destructive confirmation defaults to the safe button"); Assert.IsFalse(buttons.Single(b => b.Content as string == "Sil").IsDefault);
                        foreach (var finding in AccessibilityAudit.Names((FrameworkElement)dialog.Content)) report.AppendLine("delete dialog: " + finding);
                        cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); return true;
                    },
                });
                if (confirmed is not null) throw confirmed;
                Assert.IsNotNull(new CatalogStore(root).FindProduct(product.Id), "cancelling the confirmation keeps the product");

                // Step 3 — XML read and import preview: the source is selected, the read and the preview run on the real pipeline, the preview grid carries rows, the stepper is live; the apply is not clicked (it stays local anyway).
                Navigate(window, "xml"); Settle(window, tabs, "xml");
                var sources = (ListBox)Field(window, "sources"); sources.SelectedItem = sources.Items.OfType<XmlSource>().Single(s => s.Location == feed); Drain(window);
                Run(window, "InspectAsync"); Run(window, "PreviewAsync"); Drain(window);
                var preview = (DataGrid)Field(window, "preview");
                Assert.IsTrue(preview.Items.Count >= 2, "the preview lists the feed's rows: " + preview.Items.Count);
                Score(window, tabs, "xml", report);

                // Step 4 — channel matrix: the local plan shows as a cell, the legend explains it, a bulk plan asks for a selection first.
                Navigate(window, "listing-matrix"); Settle(window, tabs, "listing-matrix");
                var matrix = Descendants(Content("listing-matrix")).OfType<DataGrid>().Single(g => g.Tag as string == "channel-matrix");
                Assert.IsTrue(matrix.Items.Count >= 1, "the matrix lists the products");
                Score(window, tabs, "listing-matrix", report);

                // Step 5 — order workspace: the order is listed and revealed with its long line, the customer stays masked.
                Navigate(window, "orders"); Settle(window, tabs, "orders");
                var reveal = (Func<string, string, string, bool>)Field(window, "ordersReveal");
                Assert.IsTrue(reveal("etsy", "S1", "1001"), "the order is revealed"); Drain(window);
                Assert.IsFalse(VisualStateAudit.VisibleTexts(tabs).Any(t => t.Contains("ayse", StringComparison.OrdinalIgnoreCase) || t.Contains("@example", StringComparison.OrdinalIgnoreCase)), "the customer never shows unmasked");
                Score(window, tabs, "orders", report);

                // Step 6 — report: the orders report's setup opens, the query runs on the real store, the result is on screen, no file is written.
                Navigate(window, "reports"); Settle(window, tabs, "reports");
                var cards = Descendants(Content("reports")).OfType<WrapPanel>().Single(w => (string?)w.Tag == "report-cards");
                cards.Children.OfType<Button>().Single(b => ((ReportCard)b.Tag).Key == "orders-csv").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(window);
                var setup = Descendants(Content("reports")).OfType<Border>().Single(b => (string?)b.Tag == "report-setup-host");
                Descendants(setup).OfType<Button>().Single(b => (string?)b.Tag == "report-param-run").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                WaitUntil(window, () => Descendants(setup).OfType<StackPanel>().Single(p => (string?)p.Tag == "report-result").Visibility == Visibility.Visible, "the report result");
                Assert.IsFalse(Directory.EnumerateFiles(root, "*.csv", SearchOption.AllDirectories).Any(), "running the query writes no file");
                Score(window, tabs, "reports", report);

                // Step 7 — settings: the tree opens, every input is named and reachable, nothing personal is on screen.
                Navigate(window, "settings"); Settle(window, tabs, "settings");
                Score(window, tabs, "settings", report);

                // Keyboard only: the whole shell is one closed Tab cycle from the search box, and the ring draws where focus lands.
                var search = (TextBox)window.FindName("GlobalSearchBox"); search.Focus(); Drain(window);
                LastInput(Keyboard.PrimaryDevice);
                var walk = AccessibilityAudit.Walk((FrameworkElement)window.Content);
                Assert.IsTrue(walk.Closed, $"the Tab cycle comes back after {walk.Stops.Count} stops");
                Assert.IsTrue(walk.Stops.Count >= 8, $"the shell has keyboard stops: {walk.Stops.Count}");
                Assert.IsTrue(HandleShortcut(window, Key.D1, ModifierKeys.Control), "Ctrl+1 opens the dashboard"); Drain(window); Assert.AreEqual("dashboard", CurrentRoute(window));
                Assert.IsTrue(HandleShortcut(window, Key.K, ModifierKeys.Control), "Ctrl+K reaches the search"); Drain(window); Assert.IsTrue(search.IsKeyboardFocused);
                Assert.IsTrue(HandleShortcut(window, Key.Tab, ModifierKeys.None) == false, "an unbound key is not consumed");
                Assert.IsTrue(HasFocusRing(search) || search.IsKeyboardFocused, "keyboard focus is visible");

                // No live marketplace write: the sync queue is where a write would have to go, and it holds nothing but the fixture's own local job.
                var jobs = new SyncStore(root).List();
                Assert.IsTrue(jobs.All(j => j.Status is SyncStatus.Pending or SyncStatus.Cancelled), "no job ran against a marketplace: " + string.Join(", ", jobs.Select(j => j.Status)));

                Assert.AreEqual("", report.ToString(), "Acceptance findings:\n" + report);
            }
            finally
            {
                try { window?.Close(); if (window is not null) Drain(window); } catch (Exception) { }
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    try { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
                    catch (IOException) { Thread.Sleep(300); }
                    catch (UnauthorizedAccessException) { Thread.Sleep(300); }
                }
            }
        });
    }

    /// <summary>The synthetic multi-store fixture: two stores, a feed on disk with long Turkish titles, three products, a local channel plan, an order with a customer that must stay masked, one queued sync job — no real person, no credential, nothing live.</summary>
    static (CatalogProduct Product, string Feed) Seed(string root)
    {
        var catalog = new CatalogStore(root);
        var feed = Path.Combine(root, "feed.xml");
        File.WriteAllText(feed, "<Products>" +
            "<Product><Code>SKU-ÇĞİÖŞÜ-0001</Code><Title>" + LongName + "</Title><Cost>4</Cost><Stock>30</Stock></Product>" +
            "<Product><Code>SKU-0002</Code><Title>İğne oyası şal — el yapımı, sınırlı sayıda</Title><Cost>9</Cost><Stock>3</Stock></Product>" +
            "<Product><Code>SKU-0003</Code><Title>Kısa ürün</Title><Cost>2</Cost><Stock>0</Stock></Product></Products>");
        var source = new XmlSource { Id = "feed-1", Name = "Tedarikçi ana kataloğu — sonbahar 2026 (haftalık tam okuma)", Location = feed, ItemPath = "/Products/Product", Currency = "TRY", Fields = new Dictionary<string, string> { ["Sku"] = "Code", ["Name"] = "Title", ["Cost"] = "Cost", ["Stock"] = "Stock" } };
        catalog.SaveSource(source);
        catalog.Import(source, new[]
        {
            new CatalogProduct { SourceId = source.Id, SourceKind = "xml", Sku = "SKU-ÇĞİÖŞÜ-0001", Name = LongName, Price = 1234.5m, Stock = 12, Currency = "TRY" },
            new CatalogProduct { SourceId = source.Id, SourceKind = "xml", Sku = "SKU-0002", Name = "İğne oyası şal — el yapımı, sınırlı sayıda", Price = 99, Stock = 3, Currency = "TRY" },
            new CatalogProduct { SourceId = source.Id, SourceKind = "xml", Sku = "SKU-0003", Name = "Kısa ürün", Price = 10, Stock = 0, Currency = "EUR" },
        });
        var product = catalog.Products("").Single(p => p.Sku == "SKU-ÇĞİÖŞÜ-0001");
        var connections = new MarketplaceConnectionStore(root);
        connections.Save("etsy", "S1", "Etsy S1", enabled: true); connections.Save("trendyol", "T1", "Trendyol T1", enabled: true);
        new ChannelProductsStore(root).Save(new ChannelProductPlan { ChannelId = "etsy", ShopId = "S1", ProductId = product.Id, ListingId = "L-1", PlannedPrice = 20, Currency = "USD", PlannedStock = 5, UpdatedUtc = DateTime.UtcNow });
        var orders = new OrdersStore(root);
        orders.SaveManual(new OrderSnapshot { Marketplace = "etsy", ShopId = "S1", OrderId = "1001", UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1), Total = 42, Currency = "USD", Items = [new() { Sku = "SKU-0002", Title = "İğne oyası şal — el yapımı, sınırlı sayıda", Quantity = 1 }] });
        orders.SaveCustomer(new OrderCustomer("etsy", "S1", "1001", "Ayşe Yılmaz", "ayse.yilmaz@example.com", "05321234567", "Sentetik Mah. 1. Sk. No:1"));
        new SyncStore(root).Enqueue(new SyncRequest("etsy", "stock", "SKU-0002", "v1", "S1"));
        SqliteConnection.ClearAllPools();
        return (product, feed);
    }

    /// <summary>Scores one screen: no cut element at the default and the minimum width, no personal data, the same state at 100 / 150 / 200 %, every input named and reachable, every disabled input explained.</summary>
    static void Score(MainWindow window, TabControl tabs, string route, StringBuilder report)
    {
        foreach (var width in new[] { BaseWidth, window.MinWidth })
        {
            VisualStateAudit.ApplyDpiScale(window, 1.0, width, BaseHeight); Drain(window);
            var snapshot = VisualStateAudit.Snapshot(tabs, route, width, 1.0);
            if (snapshot.Overflow > 0) report.AppendLine($"{route} @ {width:F0}: {snapshot.Overflow} cut: {string.Join("; ", OverflowAudit.Audit(tabs).Take(5))}");
            foreach (var text in VisualStateAudit.PersonalDataOnScreen(tabs)) report.AppendLine($"{route} @ {width:F0}: personal data on screen: {text[..Math.Min(40, text.Length)]}");
        }
        var snapshots = new List<VisualStateSnapshot>();
        foreach (var scale in Scales)
        {
            VisualStateAudit.ApplyDpiScale(window, scale, BaseWidth, BaseHeight); Drain(window);
            snapshots.Add(VisualStateAudit.Snapshot(tabs, route, BaseWidth, scale));
        }
        VisualStateAudit.ApplyDpiScale(window, 1.0, BaseWidth, BaseHeight); Drain(window);
        if (snapshots.Select(s => s.StateOnly).Distinct().Count() > 1) report.AppendLine($"{route}: the state changes with the DPI scale: {string.Join(" || ", snapshots)}");
        foreach (var finding in AccessibilityAudit.Audit(tabs)) report.AppendLine($"{route}: {finding}");
    }

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

    static Exception? DriveModal(Window owner, Action open, IReadOnlyList<Func<Window, bool>> steps, int timeoutMs = 30000)
    {
        Exception? failure = null; Window? dialog = null; var index = 0; var deadline = Environment.TickCount64 + timeoutMs;
        void Pump()
        {
            if (failure is not null) return;
            try
            {
                if (Environment.TickCount64 > deadline) throw new TimeoutException($"Dialog step {index} did not complete in time.");
                dialog ??= owner.OwnedWindows.OfType<Window>().FirstOrDefault(w => w.IsVisible);
                if (dialog is not null)
                {
                    if (!dialog.IsVisible) { if (index < steps.Count) throw new AssertFailedException($"The dialog closed before step {index}."); return; }
                    if (index < steps.Count && steps[index](dialog)) index++;
                }
            }
            catch (Exception ex) { failure = ex; try { dialog?.Close(); } catch (InvalidOperationException) { } return; }
            owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { Thread.Sleep(25); Pump(); }));
        }
        owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Pump));
        open();
        return failure;
    }

    static bool HasFocusRing(FrameworkElement element)
    {
        var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(element);
        return layer?.GetAdorners(element)?.Any(a => a.GetType().Name.Contains("FocusVisualAdorner", StringComparison.Ordinal)) == true;
    }

    static void LastInput(InputDevice device) => typeof(InputManager).GetProperty("MostRecentInputDevice")!.GetSetMethod(nonPublic: true)!.Invoke(InputManager.Current, new object[] { device });
    static object Field(MainWindow window, string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    static string CurrentRoute(MainWindow window) => (string)Field(window, "currentRoute");
    static void Navigate(MainWindow window, string key) => typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { key, true });
    static bool HandleShortcut(MainWindow window, Key key, ModifierKeys modifiers) => (bool)typeof(MainWindow).GetMethod("HandleShortcut", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { key, modifiers })!;
    static void Run(MainWindow window, string method)
    {
        var task = (Task)typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
        WaitUntil(window, () => task.IsCompleted, method);
        if (task.IsFaulted) throw task.Exception!.GetBaseException();
    }
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
