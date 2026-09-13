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

// #848 on the real reports panel: a query shows the query and generate rows done with the export pending; a
// failed export shows the export row failed, sanitized diagnostics, "Tanılamaya git" (which navigates) and
// "Yeniden dene", and the retry re-exports into the same file without asking again; a good export ends with every
// row done and the file written; on the setup itself a long query shows the query row indeterminate while the
// total is unknown, the cancel button stops it, the cancelled stage is marked, and retry re-runs the query.
[TestClass]
public sealed class ReportRunProgressUiTests
{
    [TestMethod]
    public void TheRunViewShowsStagesFailureDiagnosticsRetryAndCancel()
    {
        var root = Path.Combine(Path.GetTempPath(), "report-run-ui-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            try
            {
                Directory.CreateDirectory(root);
                var orders = new OrdersStore(root); var now = DateTimeOffset.UtcNow;
                orders.SaveManual(new() { Marketplace = "etsy", ShopId = "S1", OrderId = "1001", UpdatedAt = now.AddDays(-2), Total = 12.5m, Currency = "USD", Items = [new() { Sku = "A", Title = "Kupa", Quantity = 1 }], Shipments = [new() { Id = "P1", Carrier = "Aras", TrackingNumber = "TR1", State = "InTransit" }] });
                SqliteConnection.ClearAllPools();
                var allowed = new List<string> { "etsy|S1" }; var navigated = new List<string>();
                var goodPath = Path.Combine(root, "rapor.csv"); var blockedPath = Path.Combine(root, "dir.csv"); Directory.CreateDirectory(blockedPath);
                var chosen = blockedPath; var asked = 0;
                var panel = ReportsPanel.Create(root, navigated.Add, () => allowed, _ => { asked++; return chosen; });
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
                    string StageText(string stage) => Descendants(Setup<DockPanel>("report-run-stage-" + stage)).OfType<TextBlock>().First().Text;

                    Click(Card("orders-csv"));
                    var progress = Setup<StackPanel>("report-run-progress");
                    Assert.AreEqual(Visibility.Collapsed, progress.Visibility, "No run, no run view.");

                    // The query: two rows done, the export pending, the result on screen.
                    Click(Setup<Button>("report-param-run"));
                    WaitUntil(window, () => Status().Contains("listelendi"), "the query");
                    Assert.AreEqual(Visibility.Visible, progress.Visibility); Assert.AreEqual(0, asked, "A query asks for no file.");
                    StringAssert.StartsWith(StageText("query"), "✔ Sorgu"); StringAssert.StartsWith(StageText("generate"), "✔ Üretim"); StringAssert.StartsWith(StageText("export"), "○ Dışa aktarma");
                    StringAssert.StartsWith(Setup<TextBlock>("report-run-headline").Text, "Sonuç hazır"); Assert.AreEqual(Visibility.Collapsed, Setup<Button>("report-run-retry").Visibility);

                    // A failed export: the row, the diagnostics, the buttons.
                    Click(Setup<Button>("report-result-export"));
                    WaitUntil(window, () => Status().StartsWith("Rapor yazılamadı", StringComparison.Ordinal), "the failed export");
                    Assert.AreEqual(1, asked);
                    StringAssert.StartsWith(StageText("query"), "✔ Sorgu"); StringAssert.StartsWith(StageText("export"), "✖ Dışa aktarma");
                    var diagnostics = Setup<TextBlock>("report-run-diagnostics");
                    Assert.AreEqual(Visibility.Visible, diagnostics.Visibility); Assert.IsTrue(diagnostics.Text.Length > 0); Assert.IsFalse(diagnostics.Text.Contains(root), "The diagnostics never show the path.");
                    StringAssert.StartsWith(Setup<TextBlock>("report-run-headline").Text, "Dışa aktarma başarısız");
                    Assert.AreEqual(Visibility.Visible, Setup<Button>("report-run-retry").Visibility); Assert.AreEqual(Visibility.Collapsed, Setup<Button>("report-run-cancel").Visibility); Assert.AreEqual(Visibility.Visible, Setup<Button>("report-run-diagnostics-open").Visibility);
                    Click(Setup<Button>("report-run-diagnostics-open")); Assert.AreEqual("diagnostics", navigated.Last());
                    Assert.IsTrue(Setup<Button>("report-param-run").IsEnabled && Setup<Button>("report-result-export").IsEnabled, "Both actions are available again after a failure.");

                    // Retry re-exports into the same file without asking; the same directory blocks again, so it fails the same way and asks nothing.
                    Click(Setup<Button>("report-run-retry"));
                    WaitUntil(window, () => Status().StartsWith("Rapor yazılamadı", StringComparison.Ordinal), "the retried export");
                    Assert.AreEqual(1, asked, "A retry never asks for the file again.");

                    // A good export: every row done, the file written, retry and diagnostics gone, the card shows the run.
                    chosen = goodPath; Click(Setup<Button>("report-result-export"));
                    WaitUntil(window, () => Status().Contains("sipariş yazıldı"), "the good export");
                    Assert.AreEqual(2, asked); Assert.IsTrue(File.Exists(goodPath));
                    Assert.IsTrue(ReportRunProgressState.Stages.All(s => StageText(s.Stage.ToString().ToLowerInvariant()).StartsWith("✔", StringComparison.Ordinal)));
                    Assert.AreEqual("Tamamlandı", Setup<TextBlock>("report-run-headline").Text);
                    Assert.AreEqual(Visibility.Collapsed, Setup<Button>("report-run-retry").Visibility); Assert.AreEqual(Visibility.Collapsed, Setup<TextBlock>("report-run-diagnostics").Visibility);
                    Assert.IsTrue(Descendants(setupHost).OfType<ProgressBar>().Where(b => ((string)b.Tag!).StartsWith("report-run-bar", StringComparison.Ordinal)).All(b => !b.IsIndeterminate && b.Value == 100));
                    StringAssert.Contains(Descendants(Card("orders-csv")).OfType<TextBlock>().Single(t => (string?)t.Tag == "report-card-run").Text, "başarılı · 1 satır");

                    // A long query on the setup itself: unknown total -> indeterminate query bar; cancel stops it at its stage; retry re-runs the query.
                    var gate = new TaskCompletionSource<bool>(); CancellationToken seen = default; var calls = 0; var definition = ReportCatalog.Find("orders-csv")!;
                    var slow = ReportParameterPanel.Build(new ReportParameterPanel.Context(definition, () => allowed, new UiPreferenceStore(root), navigated.Add, async (p, reporter, token) =>
                    {
                        calls++; seen = token; reporter.Report(new(ReportRunStage.Query, ReportRunStageStatus.Running));
                        await Task.WhenAny(gate.Task, Task.Delay(Timeout.Infinite, token)).ConfigureAwait(true);
                        token.ThrowIfCancellationRequested();
                        reporter.Report(new(ReportRunStage.Query, ReportRunStageStatus.Done, 1, 1)); reporter.Report(new(ReportRunStage.Generate, ReportRunStageStatus.Running)); reporter.Report(new(ReportRunStage.Generate, ReportRunStageStatus.Done, 1, 1));
                        return new ReportQueryOutcome(ReportRunState.Succeeded, new ReportResult(definition, p, Array.Empty<IReadOnlyDictionary<string, object?>>(), DateTime.UtcNow), "bitti");
                    }, null, () => false));
                    setupHost.Child = slow; Drain(window);
                    Click(Setup<Button>("report-param-run"));
                    WaitUntil(window, () => Setup<StackPanel>("report-run-progress").Visibility == Visibility.Visible && Setup<Button>("report-run-cancel").Visibility == Visibility.Visible, "the long run to start");
                    Assert.IsTrue(Setup<ProgressBar>("report-run-bar-query").IsIndeterminate, "An unknown total is an indeterminate bar."); StringAssert.StartsWith(Setup<TextBlock>("report-run-headline").Text, "Sorgu sürüyor");
                    Assert.IsFalse(Setup<Button>("report-param-run").IsEnabled, "No second run while one runs.");
                    Click(Setup<Button>("report-run-cancel"));
                    WaitUntil(window, () => Status().Contains("iptal"), "the cancellation");
                    Assert.IsTrue(seen.IsCancellationRequested);
                    StringAssert.StartsWith(StageText("query"), "⏹ Sorgu");
                    Assert.AreEqual("İptal edildi; dosya yazılmadı.", Setup<TextBlock>("report-run-diagnostics").Text);
                    Assert.AreEqual(Visibility.Visible, Setup<Button>("report-run-retry").Visibility); Assert.AreEqual(Visibility.Collapsed, Setup<Button>("report-run-cancel").Visibility); Assert.AreEqual(Visibility.Collapsed, Setup<Button>("report-run-diagnostics-open").Visibility, "A cancellation is not a fault to diagnose.");
                    Assert.IsTrue(Setup<Button>("report-param-run").IsEnabled);
                    gate.SetResult(true); Click(Setup<Button>("report-run-retry"));
                    WaitUntil(window, () => Status() == "bitti", "the retried long run");
                    Assert.AreEqual(2, calls, "A retry after a cancelled query runs the query again."); Assert.AreEqual(Visibility.Collapsed, Setup<Button>("report-run-retry").Visibility);
                    Assert.AreEqual(Visibility.Visible, Setup<StackPanel>("report-result").Visibility); StringAssert.StartsWith(Setup<TextBlock>("report-result-summary").Text, "0 satır");
                }
                finally { window.Close(); }
            }
            finally
            {
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    try { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
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
