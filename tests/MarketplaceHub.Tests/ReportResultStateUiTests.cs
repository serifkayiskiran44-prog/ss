using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #850 on the real reports panel: a listed result shows the grid and no state; a state filter that matches nothing
// shows "filtered empty" with widen / clear-state / retry (the primary takes the keyboard; every action is a tab
// stop) and clearing the state re-queries into the grid; a range far in the past widens to 90 days and re-queries;
// a store with no orders shows "true empty" whose action opens the orders screen; an unreadable order store shows
// "query failed" with a redacted line, a retry and a way to diagnostics, while the audit trail holds the exception
// type; on the setup itself a cancelled query and a result the column layout cannot show have their own states
// and actions.
[TestClass]
public sealed class ReportResultStateUiTests
{
    [TestMethod]
    public void EveryResultStateHasItsOwnSurfaceAndActionsOnTheRealPanel()
    {
        var root = Path.Combine(Path.GetTempPath(), "report-states-" + Guid.NewGuid().ToString("N"));
        var broken = Path.Combine(Path.GetTempPath(), "report-states-broken-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            try
            {
                Directory.CreateDirectory(root); Directory.CreateDirectory(Path.Combine(broken, "orders.db"));
                var orders = new OrdersStore(root); var now = DateTimeOffset.UtcNow;
                orders.SaveManual(new() { Marketplace = "etsy", ShopId = "S1", OrderId = "1001", UpdatedAt = now.AddDays(-2), Total = 12.5m, Currency = "USD", Items = [new() { Sku = "A", Title = "Kupa", Quantity = 1 }], Shipments = [new() { Id = "P1", Carrier = "Aras", TrackingNumber = "TR1", State = "InTransit" }] });
                SqliteConnection.ClearAllPools();
                var allowed = new List<string> { "etsy|S1", "trendyol|T1" }; var navigated = new List<string>();
                var panel = ReportsPanel.Create(root, navigated.Add, () => allowed, _ => Path.Combine(root, "rapor.csv"));
                var window = new Window { Content = panel, Width = 1300, Height = 900, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
                try
                {
                    window.Show(); Drain(window);
                    var host = Descendants(panel).OfType<WrapPanel>().Single(w => (string?)w.Tag == "report-cards");
                    var setupHost = Descendants(panel).OfType<Border>().Single(b => (string?)b.Tag == "report-setup-host");
                    Button Card(string key) => host.Children.OfType<Button>().Single(b => ((ReportCard)b.Tag).Key == key);
                    T Setup<T>(string tag) where T : FrameworkElement => Descendants(setupHost).OfType<T>().Single(e => (string?)e.Tag == tag);
                    void Click(ButtonBase b) { b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(window); }
                    string Status() => Setup<TextBlock>("report-param-status").Text;
                    Border State() => Setup<Border>("report-result-state");
                    string StateTitle() => Setup<TextBlock>("report-result-state-title").Text;
                    List<Button> Actions() => Setup<WrapPanel>("report-result-state-actions").Children.OfType<Button>().ToList();
                    Button Action(string key) => Actions().Single(b => (string?)b.Tag == "report-result-action-" + key);
                    void RunQuery(string expect) { Click(Setup<Button>("report-param-run")); WaitUntil(window, () => Status().Contains(expect), "the query (" + expect + ")"); Drain(window); }

                    // Listed: the grid, no state surface.
                    Click(Card("orders-csv")); RunQuery("listelendi");
                    Assert.AreEqual(Visibility.Collapsed, State().Visibility); Assert.AreEqual(Visibility.Visible, Setup<DataGrid>("report-result-grid").Visibility); Assert.AreEqual(1, Setup<DataGrid>("report-result-grid").Items.Count);

                    // Filtered empty by state: widen / clear-state / retry; the primary has the keyboard; clearing the state lists again.
                    var state = Setup<ComboBox>("report-param-state"); state.SelectedItem = state.Items.OfType<ReportStateOption>().Single(o => o.Key == "Delivered"); Drain(window);
                    RunQuery("sipariş yok");
                    Assert.AreEqual(Visibility.Visible, State().Visibility); Assert.AreEqual(Visibility.Collapsed, Setup<DataGrid>("report-result-grid").Visibility);
                    StringAssert.Contains(StateTitle(), "Filtreye uyan sipariş yok"); StringAssert.Contains(Setup<TextBlock>("report-result-state-text").Text, "1 sipariş var");
                    CollectionAssert.AreEqual(new[] { "report-result-action-widen-range", "report-result-action-clear-state", "report-result-action-retry" }, Actions().Select(b => (string)b.Tag!).ToArray());
                    Assert.IsTrue(Actions().All(b => b.Focusable && b.IsTabStop)); Assert.IsTrue(Actions()[0].IsKeyboardFocused, "The primary call to action takes the keyboard.");
                    Assert.IsFalse(Setup<Button>("report-result-export").IsEnabled, "Nothing to export.");
                    Click(Action("clear-state"));
                    WaitUntil(window, () => Status().Contains("listelendi"), "the re-query after clearing the state");
                    Assert.AreEqual("", ((ReportStateOption)Setup<ComboBox>("report-param-state").SelectedItem!).Key); Assert.AreEqual(Visibility.Collapsed, State().Visibility); Assert.AreEqual(1, Setup<DataGrid>("report-result-grid").Items.Count);

                    // Filtered empty by date: widening to 90 days re-queries and lists.
                    var from = Setup<DatePicker>("report-param-from"); var to = Setup<DatePicker>("report-param-to");
                    from.SelectedDate = DateTime.Today.AddDays(-300); to.SelectedDate = DateTime.Today.AddDays(-200); Drain(window);
                    RunQuery("sipariş yok"); StringAssert.Contains(StateTitle(), "Filtreye uyan");
                    Click(Action("widen-range"));
                    WaitUntil(window, () => Status().Contains("listelendi"), "the re-query after widening");
                    Assert.AreEqual(DateTime.UtcNow.Date.AddDays(-ReportResultStates.WidenToDays), Setup<DatePicker>("report-param-from").SelectedDate!.Value.Date); Assert.AreEqual(DateTime.UtcNow.Date, Setup<DatePicker>("report-param-to").SelectedDate!.Value.Date);
                    Assert.AreEqual(Visibility.Collapsed, State().Visibility);

                    // True empty: a store with no orders at all; its action opens the orders screen.
                    var store = Setup<ComboBox>("report-param-store"); store.SelectedItem = store.Items.OfType<ReportStoreOption>().Single(o => o.Key == "trendyol|T1"); Drain(window);
                    RunQuery("sipariş yok");
                    StringAssert.Contains(StateTitle(), "henüz sipariş yok"); CollectionAssert.AreEqual(new[] { "report-result-action-open-orders", "report-result-action-retry" }, Actions().Select(b => (string)b.Tag!).ToArray());
                    Click(Action("open-orders")); Assert.AreEqual("orders", navigated.Last());
                }
                finally { window.Close(); }

                // Query failed on the real path: the order store cannot be opened; the screen line is redacted, the audit has the type.
                var brokenPanel = ReportsPanel.Create(broken, navigated.Add, () => allowed, _ => Path.Combine(broken, "rapor.csv"));
                var brokenWindow = new Window { Content = brokenPanel, Width = 1300, Height = 900, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
                try
                {
                    brokenWindow.Show(); Drain(brokenWindow);
                    var host = Descendants(brokenPanel).OfType<WrapPanel>().Single(w => (string?)w.Tag == "report-cards");
                    var setupHost = Descendants(brokenPanel).OfType<Border>().Single(b => (string?)b.Tag == "report-setup-host");
                    T Setup<T>(string tag) where T : FrameworkElement => Descendants(setupHost).OfType<T>().Single(e => (string?)e.Tag == tag);
                    void Click(ButtonBase b) { b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(brokenWindow); }
                    Click(host.Children.OfType<Button>().Single(b => ((ReportCard)b.Tag).Key == "orders-csv"));
                    Click(Setup<Button>("report-param-run"));
                    WaitUntil(brokenWindow, () => Setup<TextBlock>("report-param-status").Text.StartsWith("Sorgu çalıştırılamadı", StringComparison.Ordinal), "the failed query");
                    Assert.AreEqual(Visibility.Visible, Setup<Border>("report-result-state").Visibility); StringAssert.Contains(Setup<TextBlock>("report-result-state-title").Text, "Sorgu çalıştırılamadı");
                    var text = Setup<TextBlock>("report-result-state-text").Text;
                    Assert.IsFalse(text.Contains(broken) || text.Contains(@":\") || text.Contains("orders.db"), "The screen never shows the path: " + text); Assert.IsTrue(text.Length <= ReportResultStates.MaxUiMessageLength);
                    var actions = Setup<WrapPanel>("report-result-state-actions").Children.OfType<Button>().ToList();
                    CollectionAssert.AreEqual(new[] { "report-result-action-retry", "report-result-action-diagnostics" }, actions.Select(b => (string)b.Tag!).ToArray());
                    Click(actions[1]); Assert.AreEqual("diagnostics", navigated.Last());
                    var audit = new AuditStore(broken).List(10).First(a => a.Module == "reports");
                    Assert.AreEqual("Failed", audit.Outcome); StringAssert.Contains(audit.Detail, "SqliteException", "The exception type goes to the safe diagnostics.");
                    Assert.AreEqual(ReportRunState.Failed, new ReportRunStore(broken).Latest()["orders-csv"].State);
                }
                finally { brokenWindow.Close(); }

                // On the setup itself: a cancelled query and a schema-incompatible result.
                var definition = ReportCatalog.Find("orders-csv")!; var preferences = new UiPreferenceStore(root);
                var gate = new TaskCompletionSource<bool>(); var calls = 0;
                var slow = ReportParameterPanel.Build(new ReportParameterPanel.Context(definition, () => allowed, preferences, navigated.Add, async (p, reporter, token) =>
                {
                    calls++; reporter.Report(new(ReportRunStage.Query, ReportRunStageStatus.Running));
                    await Task.WhenAny(gate.Task, Task.Delay(Timeout.Infinite, token)).ConfigureAwait(true); token.ThrowIfCancellationRequested();
                    return new ReportQueryOutcome(ReportRunState.Succeeded, new ReportResult(definition, p, new[] { (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["Foo"] = "x" } }, DateTime.UtcNow, 1), "1 sipariş listelendi.");
                }, null, () => false));
                var setupWindow = new Window { Content = slow, Width = 600, Height = 900, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
                try
                {
                    setupWindow.Show(); Drain(setupWindow);
                    T Setup<T>(string tag) where T : FrameworkElement => Descendants(slow).OfType<T>().Single(e => (string?)e.Tag == tag);
                    void Click(ButtonBase b) { b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(setupWindow); }
                    Click(Setup<Button>("report-param-run"));
                    WaitUntil(setupWindow, () => Setup<Button>("report-run-cancel").Visibility == Visibility.Visible, "the slow query to start");
                    Click(Setup<Button>("report-run-cancel"));
                    WaitUntil(setupWindow, () => Setup<Border>("report-result-state").Visibility == Visibility.Visible, "the cancelled state");
                    StringAssert.Contains(Setup<TextBlock>("report-result-state-title").Text, "Sorgu iptal edildi");
                    var actions = Setup<WrapPanel>("report-result-state-actions").Children.OfType<Button>().ToList();
                    Assert.AreEqual(1, actions.Count); Assert.AreEqual("Yeniden çalıştır", (string)actions[0].Content);
                    gate.SetResult(true); Click(actions[0]);
                    WaitUntil(setupWindow, () => Setup<TextBlock>("report-result-state-title").Text.Contains("kolon düzeniyle"), "the schema-incompatible state");
                    Assert.AreEqual(2, calls); StringAssert.Contains(Setup<TextBlock>("report-result-state-text").Text, "OrderId");
                    preferences.Set(ReportColumns.PreferenceKey("orders-csv"), "{stale");
                    Click(Descendants(slow).OfType<Button>().Single(b => (string?)b.Tag == "report-result-action-reset-columns"));
                    Assert.AreEqual(ReportColumns.Persist(ReportColumns.Default(ReportColumns.OrdersSchema)), PreferenceSchema.Read(preferences, ReportColumns.PreferenceKey("orders-csv")), "The action writes the default layout.");
                    StringAssert.Contains(Setup<TextBlock>("report-result-state-title").Text, "kolon düzeniyle", "The rows still lack the columns, so the state stays until a new query.");
                }
                finally { setupWindow.Close(); }
            }
            finally
            {
                foreach (var dir in new[] { root, broken })
                    for (var attempt = 0; attempt < 30; attempt++)
                    {
                        try { SqliteConnection.ClearAllPools(); if (Directory.Exists(dir)) Directory.Delete(dir, true); break; }
                        catch (IOException) { Thread.Sleep(300); }
                        catch (UnauthorizedAccessException) { Thread.Sleep(300); }
                    }
            }
        });
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
