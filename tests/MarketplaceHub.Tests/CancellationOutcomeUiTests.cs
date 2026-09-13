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
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #890 on the real surfaces: the import page, the report run view and the sync centre say where a cancellation
// stands from the job's real state — the request waiting, a stage finishing its unit, the job stopped with what
// stayed safe, or a job that had already ended — and never from the click alone.
[TestClass]
public sealed class CancellationOutcomeUiTests
{
    [TestMethod]
    public void TheImportPageSaysWhereACancellationStandsFromTheRealState()
    {
        var root = Path.Combine(Path.GetTempPath(), "cancel-import-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            MainWindow window = null;
            try
            {
                Directory.CreateDirectory(root);
                var feed = Path.Combine(root, "feed.xml");
                File.WriteAllText(feed, "<Products><Product><Code>S1</Code><Title>Kupa</Title></Product></Products>");
                new CatalogStore(root).SaveSource(new XmlSource { Id = "feed-1", Name = "Besleme", Location = feed, ItemPath = "/Products/Product", Fields = new Dictionary<string, string> { ["Sku"] = "Code", ["Name"] = "Title" } });
                SqliteConnection.ClearAllPools();
                window = new MainWindow(root); window.Show(); Drain(window);
                Navigate(window, "xml"); Drain(window);
                var sources = (ListBox)Field(window, "sources"); sources.SelectedItem = sources.Items.OfType<XmlSource>().Single(); Drain(window);
                var progress = (ImportProgressState)Field(window, "importProgress");
                var cancel = (Button)Field(window, "importCancelButton");
                var outcome = (TextBlock)Field(window, "importCancelOutcome");
                void Render() => typeof(MainWindow).GetMethod("RenderImportProgress", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                void Click() { cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(window); }

                // The race, on the real pipeline: the read of a local feed ends before the click lands.
                var inspect = (Task)typeof(MainWindow).GetMethod("RunAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { new Func<Task>(() => (Task)typeof(MainWindow).GetMethod("InspectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!) })!;
                WaitUntil(window, () => inspect.IsCompleted, "the read");
                Assert.AreEqual(Visibility.Collapsed, outcome.Visibility, "nothing was asked");
                Click();
                StringAssert.StartsWith(outcome.Text, CancellationOutcome.CompletedWord); StringAssert.Contains(outcome.Text, "bitmişti");
                Assert.AreEqual(Visibility.Visible, outcome.Visibility);

                // A request while a stage that checks the token runs: the request waits for the next check, the button waits with it.
                progress.BeginOperation(); progress.Apply(new(ImportProgressStage.Download, ImportProgressStatus.Running)); Render();
                Assert.AreEqual(Visibility.Collapsed, outcome.Visibility, "a new operation starts with no request");
                Assert.IsTrue(cancel.IsEnabled);
                Click();
                StringAssert.StartsWith(outcome.Text, CancellationOutcome.RequestedWord);
                Assert.IsFalse(cancel.IsEnabled); StringAssert.Contains(CommandState.ReasonOf(cancel)!.Text, "yanıt bekleniyor");
                Assert.AreEqual(ImportProgressStatus.Running, progress[ImportProgressStage.Download].Status, "the click does not rewrite the stage");

                // The pipeline moves into the parse, which cannot be interrupted: stopping at a safe point.
                progress.Apply(new(ImportProgressStage.Download, ImportProgressStatus.Done)); progress.Apply(new(ImportProgressStage.Read, ImportProgressStatus.Running)); Render();
                StringAssert.StartsWith(outcome.Text, CancellationOutcome.SafePointWord); StringAssert.Contains(outcome.Text, "Oku aşaması bölünemez");

                // The pipeline answers: cancelled, and what stayed safe.
                progress.Apply(new(ImportProgressStage.Read, ImportProgressStatus.Cancelled)); Render();
                StringAssert.StartsWith(outcome.Text, CancellationOutcome.CancelledWord); StringAssert.Contains(outcome.Text, "Havuz değişmedi");
                Assert.AreEqual(Visibility.Visible, ((Button)Field(window, "importRetryButton")).Visibility);

                // Retry clears the request with the stage it resets.
                progress.Retry(); Render();
                Assert.AreEqual(Visibility.Collapsed, outcome.Visibility);
            }
            finally
            {
                try { window?.Close(); if (window is not null) Drain(window); } catch (Exception) { }
                Cleanup(root);
            }
        });
    }

    [TestMethod]
    public void TheReportRunViewTellsAStoppedRunFromOneThatFinishedInSpiteOfTheRequest()
    {
        var root = Path.Combine(Path.GetTempPath(), "cancel-report-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            Window window = null;
            try
            {
                Directory.CreateDirectory(root);
                var definition = ReportCatalog.Find("orders-csv")!; var allowed = new List<string> { "etsy|S1" }; var preferences = new UiPreferenceStore(root);
                FrameworkElement Build(bool honoursToken, TaskCompletionSource<bool> gate) => ReportParameterPanel.Build(new ReportParameterPanel.Context(definition, () => allowed, preferences, null, async (p, reporter, token) =>
                {
                    reporter.Report(new(ReportRunStage.Query, ReportRunStageStatus.Running));
                    if (honoursToken) { await Task.WhenAny(gate.Task, Task.Delay(Timeout.Infinite, token)).ConfigureAwait(true); token.ThrowIfCancellationRequested(); }
                    else await gate.Task.ConfigureAwait(true);
                    reporter.Report(new(ReportRunStage.Query, ReportRunStageStatus.Done, 1, 1)); reporter.Report(new(ReportRunStage.Generate, ReportRunStageStatus.Running)); reporter.Report(new(ReportRunStage.Generate, ReportRunStageStatus.Done, 1, 1));
                    return new ReportQueryOutcome(ReportRunState.Succeeded, new ReportResult(definition, p, Array.Empty<IReadOnlyDictionary<string, object?>>(), DateTime.UtcNow), "bitti");
                }, null, () => false));
                var host = new Border(); window = new Window { Content = host, Width = 1100, Height = 800, Left = -4000, Top = -4000 };
                window.Show(); Drain(window);
                T Setup<T>(string tag) where T : FrameworkElement => Descendants(host).OfType<T>().Single(e => (string?)e.Tag == tag);
                void Click(ButtonBase b) { b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(window); }

                // A run that honours the token: the request waits, then the runner answers and the run is cancelled.
                var gate = new TaskCompletionSource<bool>();
                host.Child = Build(honoursToken: true, gate); Drain(window);
                Click(Setup<Button>("report-param-run"));
                WaitUntil(window, () => Setup<Button>("report-run-cancel").Visibility == Visibility.Visible, "the run");
                Assert.AreEqual(Visibility.Collapsed, Setup<TextBlock>(CancellationOutcome.Tag).Visibility);
                // The click itself only asks: before the runner's answer is pumped, the words are "requested"; the answer then makes it "cancelled".
                Setup<Button>("report-run-cancel").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                StringAssert.StartsWith(Setup<TextBlock>("report-param-status").Text, CancellationOutcome.RequestedWord);
                StringAssert.StartsWith(Setup<TextBlock>(CancellationOutcome.Tag).Text, CancellationOutcome.RequestedWord);
                Drain(window);
                WaitUntil(window, () => Setup<TextBlock>(CancellationOutcome.Tag).Text.StartsWith(CancellationOutcome.CancelledWord, StringComparison.Ordinal), "the acknowledgement");
                StringAssert.Contains(Setup<TextBlock>(CancellationOutcome.Tag).Text, "Dosya yazılmadı");
                StringAssert.StartsWith(StageText(host, "query"), "⏹ Sorgu");

                // A run that finishes in spite of the request: the outcome says so, the result stands.
                var gate2 = new TaskCompletionSource<bool>();
                host.Child = Build(honoursToken: false, gate2); Drain(window);
                Click(Setup<Button>("report-param-run"));
                WaitUntil(window, () => Setup<Button>("report-run-cancel").Visibility == Visibility.Visible, "the second run");
                Click(Setup<Button>("report-run-cancel"));
                StringAssert.StartsWith(Setup<TextBlock>(CancellationOutcome.Tag).Text, CancellationOutcome.RequestedWord);
                gate2.SetResult(true);
                WaitUntil(window, () => Setup<TextBlock>("report-param-status").Text == "bitti", "the completion");
                StringAssert.StartsWith(Setup<TextBlock>(CancellationOutcome.Tag).Text, CancellationOutcome.CompletedWord); StringAssert.Contains(Setup<TextBlock>(CancellationOutcome.Tag).Text, "son güvenli noktadan sonra");
                Assert.AreEqual(Visibility.Visible, Setup<StackPanel>("report-result").Visibility, "the result stands");
                Assert.AreEqual(Visibility.Collapsed, Setup<Button>("report-run-retry").Visibility, "nothing to retry");
            }
            finally
            {
                try { window?.Close(); } catch (Exception) { }
                Cleanup(root);
            }
        });
    }

    [TestMethod]
    public void TheSyncCentreCancelsAQueuedJobAndSaysWhenAJobHadAlreadyEnded()
    {
        var root = Path.Combine(Path.GetTempPath(), "cancel-sync-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            MainWindow window = null;
            try
            {
                Directory.CreateDirectory(root);
                var sync = new SyncStore(root);
                var queued = sync.Enqueue(new SyncRequest("etsy", "stock", "SKU-1", "v1", "S1"));
                var done = sync.Enqueue(new SyncRequest("etsy", "stock", "SKU-2", "v1", "S1")); sync.TryStart(done.Id); sync.Succeed(done.Id);
                SqliteConnection.ClearAllPools();
                window = new MainWindow(root); window.Show(); Drain(window);
                Navigate(window, "sync"); Drain(window);
                var routes = (Dictionary<string, TabItem>)Field(window, "routes");
                var page = (DependencyObject)routes["sync"].Content;
                var grid = Descendants(page).OfType<DataGrid>().First(g => g.Items.OfType<SyncJob>().Any());
                var cancel = Descendants(page).OfType<Button>().Single(b => b.Tag as string == "sync-cancel");
                string Status() => Descendants(page).OfType<TextBlock>().Select(t => t.Text).FirstOrDefault(t => t.StartsWith(CancellationOutcome.CancelledWord, StringComparison.Ordinal) || t.StartsWith(CancellationOutcome.CompletedWord, StringComparison.Ordinal)) ?? "";
                void Click() { cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(window); }

                grid.SelectedItem = grid.Items.OfType<SyncJob>().Single(j => j.Id == queued.Id); Click();
                StringAssert.StartsWith(Status(), CancellationOutcome.CancelledWord); StringAssert.Contains(Status(), "istek gitmedi");
                Assert.AreEqual(SyncStatus.Cancelled, sync.Get(queued.Id).Status);

                grid.SelectedItem = grid.Items.OfType<SyncJob>().Single(j => j.Id == done.Id); Click();
                StringAssert.StartsWith(Status(), CancellationOutcome.CompletedWord); StringAssert.Contains(Status(), "bitmişti");
                Assert.AreEqual(SyncStatus.Succeeded, sync.Get(done.Id).Status, "a finished job stays finished");
            }
            finally
            {
                try { window?.Close(); if (window is not null) Drain(window); } catch (Exception) { }
                Cleanup(root);
            }
        });
    }

    static string StageText(DependencyObject host, string stage) => Descendants(Descendants(host).OfType<DockPanel>().Single(d => (string?)d.Tag == "report-run-stage-" + stage)).OfType<TextBlock>().First().Text;
    static object Field(MainWindow window, string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    static void Navigate(MainWindow window, string key) => typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { key, true });
    static void Drain(Window window) { window.UpdateLayout(); for (var i = 0; i < 4; i++) window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { })); }

    static void WaitUntil(Window window, Func<bool> condition, string what)
    {
        for (var i = 0; i < 400; i++) { Drain(window); if (condition()) return; Thread.Sleep(25); }
        Assert.Fail($"Timed out waiting for {what}.");
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
