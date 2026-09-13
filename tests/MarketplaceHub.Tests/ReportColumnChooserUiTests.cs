using System;
using System.Collections.Generic;
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

// #849 on the real reports panel: a query shows the result grid with the schema's default columns (the classified
// tracking column hidden) at DIP widths; a layout persisted before the panel was built (a restart) drives the
// grid's order and visibility; the chooser -- driven inside its modal loop -- searches, hides, moves and saves,
// and the grid and the preference follow; with the PII policy off the classified column is offered disabled, with
// it on it can be shown and every value is masked; the export writes exactly the visible columns in their order;
// a hundred columns render in the chooser and its search narrows them.
[TestClass]
public sealed class ReportColumnChooserUiTests
{
    [TestMethod]
    public void TheResultGridFollowsTheChooserThePolicyAndThePersistedLayoutAndTheExportWritesTheVisibleColumns()
    {
        var root = Path.Combine(Path.GetTempPath(), "report-columns-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            try
            {
                Directory.CreateDirectory(root);
                var orders = new OrdersStore(root); var now = DateTimeOffset.UtcNow;
                orders.SaveManual(new() { Marketplace = "etsy", ShopId = "S1", OrderId = "1001", UpdatedAt = now.AddDays(-2), Total = 12.5m, Currency = "USD", Items = [new() { Sku = "A", Title = "Kupa", Quantity = 1 }], Shipments = [new() { Id = "P1", Carrier = "Aras", TrackingNumber = "TRK000000001001", State = "InTransit" }] });
                orders.SaveManual(new() { Marketplace = "etsy", ShopId = "S1", OrderId = "1002", UpdatedAt = now.AddDays(-3), Total = 7m, Currency = "USD", Items = [new() { Sku = "B", Title = "Tabak", Quantity = 2 }], Shipments = [new() { Id = "P2", Carrier = "Aras", TrackingNumber = "TRK000000001002", State = "Delivered" }] });
                var catalog = new CatalogStore(root); SqliteConnection.ClearAllPools();
                var allowed = new List<string> { "etsy|S1" }; var navigated = new List<string>(); var csvPath = Path.Combine(root, "rapor.csv");
                FrameworkElement panel = null!; Window window = null!;
                void Open()
                {
                    panel = ReportsPanel.Create(root, navigated.Add, () => allowed, _ => csvPath);
                    window = new Window { Content = panel, Width = 1300, Height = 900, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
                    window.Show(); Drain(window);
                }
                Open();
                try
                {
                    WrapPanel Host() => Descendants(panel).OfType<WrapPanel>().Single(w => (string?)w.Tag == "report-cards");
                    Border SetupHost() => Descendants(panel).OfType<Border>().Single(b => (string?)b.Tag == "report-setup-host");
                    Button Card(string key) => Host().Children.OfType<Button>().Single(b => ((ReportCard)b.Tag).Key == key);
                    T Setup<T>(string tag) where T : FrameworkElement => Descendants(SetupHost()).OfType<T>().Single(e => (string?)e.Tag == tag);
                    void Click(ButtonBase b) { b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(window); }
                    string Status() => Setup<TextBlock>("report-param-status").Text;
                    DataGrid Grid() => Setup<DataGrid>("report-result-grid");
                    string[] Keys() => Grid().Columns.OrderBy(c => c.DisplayIndex).Select(c => ReportParameterPanel.ColumnKey(c)!).ToArray();
                    void RunQuery() { Click(Setup<Button>("report-param-run")); WaitUntil(window, () => Status().Contains("listelendi"), "the query"); }

                    // Default columns at DIP widths, the classified one hidden, two rows.
                    Click(Card("orders-csv")); RunQuery();
                    Assert.AreEqual(Visibility.Visible, Setup<StackPanel>("report-result").Visibility); Assert.AreEqual(2, Grid().Items.Count);
                    CollectionAssert.AreEqual(ReportColumns.Default(ReportColumns.OrdersSchema).VisibleKeys.ToArray(), Keys());
                    Assert.IsTrue(Grid().Columns.All(c => Math.Abs(c.Width.Value - ReportColumns.OrdersSchema.Single(s => s.Key == ReportParameterPanel.ColumnKey(c)).Width) < 0.5), "DIP widths from the schema.");
                    Assert.AreEqual("Sipariş no", (string)Grid().Columns[0].Header); StringAssert.Contains(Setup<TextBlock>("report-result-summary").Text, "2 satır · 6 / 7 kolon görünür");
                    StringAssert.StartsWith(Setup<TextBlock>("report-run-headline").Text, "Sonuç hazır");

                    // The chooser, driven inside its modal loop: search narrows; hide Currency; move UpdatedUtc up; Tracking is offered but closed by policy; save.
                    var failure = DriveModal(window, () => Setup<Button>("report-result-columns").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)), new Func<Window, bool>[]
                    {
                        d => Descendants(d).OfType<ListBox>().Any(l => (string?)l.Tag == "report-columns-list"),
                        d =>
                        {
                            var list = Descendants(d).OfType<ListBox>().Single(l => (string?)l.Tag == "report-columns-list"); var search = Descendants(d).OfType<TextBox>().Single(t => (string?)t.Tag == "report-columns-search");
                            List<ListBoxItem> Items() => list.Items.OfType<ListBoxItem>().Where(i => i.Tag is string).ToList();
                            CheckBox Check(string key) => (CheckBox)Items().Single(i => (string)i.Tag == key).Content;
                            Assert.AreEqual(7, Items().Count); Assert.IsTrue(list.Items.OfType<ListBoxItem>().Count(i => i.Tag is null) >= 5, "Group headers.");
                            var tracking = Check("Tracking"); Assert.IsFalse(tracking.IsEnabled); StringAssert.Contains((string)tracking.Content, ReportColumns.PolicyClosedWord); Assert.IsFalse(tracking.IsChecked == true);
                            search.Text = "Tutar"; d.UpdateLayout(); Assert.AreEqual(1, Items().Count); Assert.AreEqual("Price", (string)Items()[0].Tag);
                            search.Text = ""; d.UpdateLayout();
                            Check("Currency").IsChecked = false; Check("Currency").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                            list.SelectedItem = Items().Single(i => (string)i.Tag == "UpdatedUtc");
                            var up = Descendants(d).OfType<Button>().Single(b => (string?)b.Tag == "report-columns-up"); up.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); up.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); d.UpdateLayout();
                            CollectionAssert.AreEqual(new[] { "OrderId", "ShopId", "Status", "UpdatedUtc", "Price", "Currency", "Tracking" }, Items().Select(i => (string)i.Tag).ToArray());
                            StringAssert.Contains(Descendants(d).OfType<TextBlock>().Single(t => (string?)t.Tag == "report-columns-summary").Text, "5 / 7 kolon görünür");
                            Descendants(d).OfType<Button>().Single(b => b.Content as string == ReportColumnChooserDialog.SaveLabel).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); return true;
                        },
                    });
                    if (failure is not null) throw failure;
                    Drain(window);
                    CollectionAssert.AreEqual(new[] { "OrderId", "ShopId", "Status", "UpdatedUtc", "Price" }, Keys(), "The grid follows the saved layout.");
                    var persisted = ReportColumns.Resolve(ReportColumns.OrdersSchema, new UiPreferenceStore(root).Get("report-columns:orders-csv"), false);
                    CollectionAssert.AreEqual(new[] { "OrderId", "ShopId", "Status", "UpdatedUtc", "Price" }, persisted.VisibleKeys.ToArray(), "The preference holds the layout.");

                    // The export writes exactly the visible columns in their order.
                    Click(Setup<Button>("report-result-export")); WaitUntil(window, () => Status().Contains("sipariş yazıldı"), "the export");
                    var lines = File.ReadAllLines(csvPath); Assert.AreEqual("OrderId;ShopId;Status;UpdatedUtc;Price", lines[0]); Assert.AreEqual(3, lines.Length);

                    // A grid reorder by the operator persists too.
                    Grid().Columns.Single(c => ReportParameterPanel.ColumnKey(c) == "Price").DisplayIndex = 0; Drain(window);
                    CollectionAssert.AreEqual(new[] { "Price", "OrderId", "ShopId", "Status", "UpdatedUtc" }, ReportColumns.Resolve(ReportColumns.OrdersSchema, new UiPreferenceStore(root).Get("report-columns:orders-csv"), false).VisibleKeys.ToArray());

                    // Restart: a new panel reads the persisted layout back.
                    window.Close(); Open();
                    Click(Card("orders-csv")); RunQuery();
                    CollectionAssert.AreEqual(new[] { "Price", "OrderId", "ShopId", "Status", "UpdatedUtc" }, Keys(), "The layout survives a restart.");

                    // Policy on: the classified column can be shown, and every value is the masked form.
                    catalog.SavePiiRevealPolicy(new PiiRevealPolicy(Allowed: true));
                    Click(Card("orders-csv")); RunQuery();
                    failure = DriveModal(window, () => Setup<Button>("report-result-columns").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)), new Func<Window, bool>[]
                    {
                        d => Descendants(d).OfType<ListBox>().Any(l => (string?)l.Tag == "report-columns-list"),
                        d =>
                        {
                            var list = Descendants(d).OfType<ListBox>().Single(l => (string?)l.Tag == "report-columns-list");
                            var tracking = (CheckBox)list.Items.OfType<ListBoxItem>().Single(i => (string?)i.Tag == "Tracking").Content;
                            Assert.IsTrue(tracking.IsEnabled); StringAssert.Contains((string)tracking.Content, "maskeli");
                            tracking.IsChecked = true; tracking.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                            Descendants(d).OfType<Button>().Single(b => b.Content as string == ReportColumnChooserDialog.SaveLabel).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); return true;
                        },
                    });
                    if (failure is not null) throw failure;
                    Drain(window);
                    Assert.IsTrue(Keys().Contains("Tracking"));
                    var rows = Grid().Items.OfType<IReadOnlyDictionary<string, object?>>().ToList();
                    Assert.AreEqual(2, rows.Count); Assert.IsTrue(rows.All(r => ((string)r["Tracking"]!).Contains("••") && !((string)r["Tracking"]!).Contains("TRK000000")), "Masked in the rows themselves.");
                    Click(Setup<Button>("report-result-export")); WaitUntil(window, () => Status().Contains("sipariş yazıldı"), "the second export");
                    var csv = File.ReadAllText(csvPath); StringAssert.Contains(csv, "Tracking"); Assert.IsFalse(csv.Contains("TRK000000"), "The file never carries a raw tracking number.");

                    // Policy back off: the column disappears from the grid on the next query even though the layout still names it.
                    catalog.SavePiiRevealPolicy(new PiiRevealPolicy(Allowed: false));
                    Click(Card("orders-csv")); RunQuery();
                    Assert.IsFalse(Keys().Contains("Tracking"), "A policy that is off now outranks the saved layout.");

                    // A hundred columns in the chooser, searchable.
                    var hundred = ReportColumns.Default(Enumerable.Range(0, 100).Select(i => new ReportColumn($"col-{i:D3}", $"Kolon {i:D3}", $"Grup {i / 10}", Width: 100)).ToList());
                    var dialog = ReportColumnChooserDialog.Build(null, hundred, true, _ => { });
                    dialog.Show(); Drain(dialog);
                    try
                    {
                        var list = Descendants(dialog).OfType<ListBox>().Single(l => (string?)l.Tag == "report-columns-list");
                        Assert.AreEqual(100, list.Items.OfType<ListBoxItem>().Count(i => i.Tag is string)); Assert.AreEqual(10, list.Items.OfType<ListBoxItem>().Count(i => i.Tag is null));
                        var search = Descendants(dialog).OfType<TextBox>().Single(t => (string?)t.Tag == "report-columns-search"); search.Text = "Grup 4"; Drain(dialog);
                        Assert.AreEqual(10, list.Items.OfType<ListBoxItem>().Count(i => i.Tag is string));
                    }
                    finally { dialog.Close(); }
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

    /// <summary>Drives the modal dialog <paramref name="open"/> shows from inside ShowDialog's nested loop (see ChannelMatrixBulkUiTests).</summary>
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
