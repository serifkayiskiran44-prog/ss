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

// #866 on the real main window: the sync centre's job grid and XML run grid show their UTC timestamps as the local
// wall clock in the current culture (not the raw UTC value in the en-US form a bare binding produces) with a cell
// tooltip that names the zone offset and the age, an unfinished run shows a dash for its end, and the dashboard's
// connections grid shows a dash for a connection never tested.
[TestClass]
public sealed class TimeDisplayUiTests
{
    [TestMethod]
    public void TheSyncGridsAndTheDashboardShowLocalTimesWithTooltipsAndDashes()
    {
        var root = Path.Combine(Path.GetTempPath(), "time-display-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            MainWindow window = null;
            try
            {
                Directory.CreateDirectory(root);
                var sync = new SyncStore(root); sync.Enqueue(new SyncRequest("etsy", "stock", "SKU-1", "v1"));
                var runs = new XmlRunStore(root); var runId = runs.Start("feed-1"); runs.Fail(runId, "kaynak okunamadı");
                window = new MainWindow(root); window.Show(); Drain(window);
                Navigate(window, "sync"); Drain(window);
                var tabs = (TabControl)window.FindName("ModuleTabs");

                var jobs = GridWith(window, tabs, "UpdatedUtc", "the sync jobs grid");
                var (jobCell, jobItem) = Cell(jobs, "UpdatedUtc", 0);
                var updatedUtc = ((SyncJob)jobItem).UpdatedUtc;
                Assert.AreEqual(TimeDisplay.Format(updatedUtc), ((TextBlock)jobCell.Content).Text, "the local wall clock in the current culture, not the raw UTC value");
                var tooltip = (string)jobCell.ToolTip; StringAssert.Contains(tooltip, TimeDisplay.Offset(updatedUtc, null)); StringAssert.Contains(tooltip, "önce");
                Assert.IsTrue(ToolTipService.GetShowsToolTipOnKeyboardFocus(jobCell), "the keyboard opens the tooltip on the cell");

                // The XML history is an inner tab; its grid enters the visual tree only once the tab is selected.
                var inner = Descendants(tabs).OfType<TabControl>().First(t => t.Items.OfType<TabItem>().Any(i => i.Header?.ToString() == "XML geçmişi"));
                inner.SelectedItem = inner.Items.OfType<TabItem>().First(i => i.Header?.ToString() == "XML geçmişi"); Drain(window);
                var xmlRuns = GridWith(window, tabs, "StartedUtc", "the XML runs grid");
                var (startedCell, runItem) = Cell(xmlRuns, "StartedUtc", 0); var (finishedCell, _) = Cell(xmlRuns, "FinishedUtc", 0);
                var record = (XmlRunRecord)runItem;
                Assert.AreEqual(TimeDisplay.Format(record.StartedUtc), ((TextBlock)startedCell.Content).Text);
                Assert.AreEqual(TimeDisplay.Format(record.FinishedUtc), ((TextBlock)finishedCell.Content).Text, "a run's end, or a dash when it has none");
                StringAssert.Contains((string)startedCell.ToolTip, TimeDisplay.Offset(record.StartedUtc, null));

                Navigate(window, "dashboard"); Drain(window);
                var connections = GridWith(window, tabs, "LastTestLabel", "the connections grid");
                var (lastTest, _) = Cell(connections, "LastTestLabel", 0);
                Assert.AreEqual(TimeDisplay.Missing, ((TextBlock)lastTest.Content).Text, "a connection never tested shows the shared dash");
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

    static string PathOf(DataGridColumn column) => column is DataGridBoundColumn { Binding: System.Windows.Data.Binding b } ? b.Path.Path : "";

    static DataGrid GridWith(Window window, TabControl tabs, string path, string what)
    {
        DataGrid? grid = null;
        WaitUntil(window, () => (grid = Descendants(tabs).OfType<DataGrid>().FirstOrDefault(g => g.Columns.Any(c => PathOf(c) == path) && g.Items.Count > 0)) is not null, what);
        return grid!;
    }

    static (DataGridCell Cell, object Item) Cell(DataGrid grid, string path, int index)
    {
        var column = grid.Columns.First(c => PathOf(c) == path);
        var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(index); Assert.IsNotNull(row, $"row {index} of {path}");
        return (Descendants(row).OfType<DataGridCell>().ElementAt(grid.Columns.IndexOf(column)), row.Item);
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
