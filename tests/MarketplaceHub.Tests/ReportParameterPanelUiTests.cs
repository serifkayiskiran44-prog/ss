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

// #847 on the real reports panel: selecting the orders CSV card opens its setup with typed controls and defaults
// (the first offered store, the last 30 days, every state); an inverted date range and a missing store block the
// run with the field and the summary saying why; a filter saves under the report's module, loads back, and a
// hostile saved payload loads as unreadable; every control is a labelled tab stop and Enter runs; the run writes
// the CSV the panel was told to, the card shows the run, and the setup stays. A screen report offers "Ekranda aç"
// instead of a run; a report without parameters says so; without an offered store the setup blocks.
[TestClass]
public sealed class ReportParameterPanelUiTests
{
    [TestMethod]
    public void TheSetupValidatesSavesLoadsRunsAndKeepsEveryControlOnTheKeyboard()
    {
        var root = Path.Combine(Path.GetTempPath(), "report-setup-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            try
            {
                Directory.CreateDirectory(root);
                var orders = new OrdersStore(root); var now = DateTimeOffset.UtcNow;
                orders.SaveManual(new() { Marketplace = "etsy", ShopId = "S1", OrderId = "1001", UpdatedAt = now.AddDays(-2), Total = 12.5m, Currency = "USD", Items = [new() { Sku = "A", Title = "Kupa", Quantity = 1 }], Shipments = [new() { Id = "P1", Carrier = "Aras", TrackingNumber = "TR1", State = "InTransit" }] });
                orders.SaveManual(new() { Marketplace = "etsy", ShopId = "S1", OrderId = "1002", UpdatedAt = now.AddDays(-3), Total = 7m, Currency = "USD", Items = [new() { Sku = "B", Title = "Tabak", Quantity = 2 }], Shipments = [new() { Id = "P2", Carrier = "Aras", TrackingNumber = "TR2", State = "Delivered" }] });
                SqliteConnection.ClearAllPools();
                var allowed = new List<string> { "etsy|S1", "trendyol|T1" }; var navigated = new List<string>(); var csvPath = Path.Combine(root, "rapor.csv");
                var panel = ReportsPanel.Create(root, navigated.Add, () => allowed, _ => csvPath);
                var window = new Window { Content = panel, Width = 1300, Height = 850, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
                try
                {
                    window.Show(); Drain(window);
                    var host = Descendants(panel).OfType<WrapPanel>().Single(w => (string?)w.Tag == "report-cards");
                    var setupHost = Descendants(panel).OfType<Border>().Single(b => (string?)b.Tag == "report-setup-host");
                    Button Card(string key) => host.Children.OfType<Button>().Single(b => ((ReportCard)b.Tag).Key == key);
                    T Setup<T>(string tag) where T : FrameworkElement => Descendants(setupHost).OfType<T>().Single(e => (string?)e.Tag == tag);
                    string RunText(Button b) => Descendants(b).OfType<TextBlock>().Single(t => (string?)t.Tag == "report-card-run").Text;
                    void Click(ButtonBase b) { b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(window); }

                    Assert.AreEqual(Visibility.Collapsed, setupHost.Visibility);
                    Click(Card("orders-csv"));
                    Assert.AreEqual(Visibility.Visible, setupHost.Visibility); Assert.AreEqual("Sipariş listesi (CSV)", Setup<TextBlock>("report-setup-title").Text);
                    var store = Setup<ComboBox>("report-param-store"); var from = Setup<DatePicker>("report-param-from"); var to = Setup<DatePicker>("report-param-to"); var state = Setup<ComboBox>("report-param-state"); var query = Setup<TextBox>("report-param-query");
                    var run = Setup<Button>("report-param-run"); var validation = Setup<TextBlock>("report-param-validation"); var status = Setup<TextBlock>("report-param-status"); var saved = Setup<ComboBox>("report-param-saved"); var savedName = Setup<TextBox>("report-param-saved-name");

                    // Defaults: the first offered store, the last 30 days, every state; the run is on, the owner button off.
                    Assert.AreEqual(2, store.Items.Count); Assert.AreEqual("etsy · S1", ((ReportStoreOption)store.SelectedItem!).Label);
                    Assert.AreEqual(DateTime.UtcNow.Date.AddDays(-30), from.SelectedDate!.Value.Date); Assert.AreEqual(DateTime.UtcNow.Date, to.SelectedDate!.Value.Date);
                    Assert.AreEqual("", ((ReportStateOption)state.SelectedItem!).Key); Assert.AreEqual(Visibility.Collapsed, validation.Visibility);
                    Assert.IsTrue(run.IsEnabled && run.Visibility == Visibility.Visible && run.IsDefault, $"Run: enabled={run.IsEnabled} visible={run.Visibility} default={run.IsDefault} validation='{validation.Text}'"); Assert.AreEqual(Visibility.Collapsed, Setup<Button>("report-param-open").Visibility, "The workspace owns this report.");

                    // Keyboard: every control is a labelled tab stop (a DatePicker hands the stop to its inner text box).
                    Assert.IsTrue(new Control[] { store, state, query, saved, savedName, run }.All(c => c.Focusable && c.IsTabStop), "Every combo, box and button is a tab stop.");
                    Assert.IsTrue(new[] { from, to }.All(p => Descendants(p).OfType<DatePickerTextBox>().Single().IsTabStop), "A date picker's text box is the tab stop.");
                    StringAssert.StartsWith(AutomationProperties.GetName(store), "Mağaza"); StringAssert.Contains(AutomationProperties.GetName(store), "zorunlu"); StringAssert.StartsWith(AutomationProperties.GetName(from), "Başlangıç tarihi");

                    // An inverted range blocks: the field and the summary both say why; fixing it re-enables the run.
                    to.SelectedDate = DateTime.Today.AddDays(-40); Drain(window);
                    Assert.AreEqual(Visibility.Visible, validation.Visibility); StringAssert.Contains(validation.Text, "Başlangıç tarihi bitişten sonra olamaz."); Assert.IsFalse(run.IsEnabled);
                    Assert.AreEqual(2, Descendants(setupHost).OfType<TextBlock>().Count(t => t.IsVisible && t.Text.Contains("bitişten sonra olamaz")), "The field's own slot and the summary.");
                    to.SelectedDate = DateTime.Today; Drain(window);
                    Assert.AreEqual(Visibility.Collapsed, validation.Visibility); Assert.IsTrue(run.IsEnabled, "Run re-enabled after fixing the range.");

                    // No store blocks.
                    store.SelectedItem = null; Drain(window);
                    StringAssert.Contains(validation.Text, "Mağaza seçin"); Assert.IsFalse(run.IsEnabled);
                    store.SelectedIndex = 0; Drain(window); Assert.IsTrue(run.IsEnabled, "Run re-enabled after choosing a store.");

                    // Save a filter, change the state, load it back.
                    state.SelectedItem = state.Items.OfType<ReportStateOption>().Single(o => o.Key == "InTransit"); savedName.Text = "Yolda"; Click(Setup<Button>("report-param-save"));
                    Assert.AreEqual(1, new UiPreferenceStore(root).ListViews("report:orders-csv").Count); StringAssert.Contains(status.Text, "kaydedildi"); Assert.AreEqual(1, saved.Items.Count); Assert.IsNotNull(saved.SelectedItem);
                    state.SelectedIndex = 0; Drain(window); Assert.AreEqual("", ((ReportStateOption)state.SelectedItem!).Key);
                    Click(Setup<Button>("report-param-load"));
                    Assert.AreEqual("InTransit", ((ReportStateOption)state.SelectedItem!).Key); StringAssert.Contains(status.Text, "yüklendi");

                    // A hostile saved payload loads as unreadable and the values stay.
                    new UiPreferenceStore(root).SaveView("report:orders-csv", "Bozuk", "{not json");
                    Click(Card("orders-csv"));
                    saved = Setup<ComboBox>("report-param-saved"); state = Setup<ComboBox>("report-param-state"); status = Setup<TextBlock>("report-param-status");
                    Assert.AreEqual(2, saved.Items.Count); saved.SelectedItem = saved.Items.OfType<SavedUiView>().Single(v => v.Name == "Bozuk"); Click(Setup<Button>("report-param-load"));
                    StringAssert.Contains(status.Text, "okunamadı"); Assert.AreEqual("", ((ReportStateOption)state.SelectedItem!).Key);
                    saved.SelectedItem = saved.Items.OfType<SavedUiView>().Single(v => v.Name == "Yolda"); Click(Setup<Button>("report-param-load"));
                    Assert.AreEqual("InTransit", ((ReportStateOption)state.SelectedItem!).Key);

                    // Run: the CSV lands where the panel was told, the card shows the run, the setup stays.
                    Click(Setup<Button>("report-param-run"));
                    WaitUntil(window, () => Setup<TextBlock>("report-param-status").Text.Contains("sipariş yazıldı"), "the run to finish");
                    var lines = File.ReadAllLines(csvPath);
                    Assert.AreEqual(2, lines.Length); StringAssert.StartsWith(lines[1], "1001;S1;Yolda;12.5;USD;");
                    StringAssert.Contains(RunText(Card("orders-csv")), "başarılı · 1 satır"); Assert.AreEqual(Visibility.Visible, setupHost.Visibility);

                    // A screen report: no run, "Ekranda aç" navigates; its schema has a query and no store.
                    Click(Card("data-quality"));
                    Assert.AreEqual(Visibility.Collapsed, Setup<Button>("report-param-run").Visibility);
                    Assert.IsTrue(Descendants(setupHost).OfType<TextBox>().Any(t => (string?)t.Tag == "report-param-query"), "Data quality takes a query."); Assert.IsFalse(Descendants(setupHost).OfType<ComboBox>().Any(c => (string?)c.Tag == "report-param-store"), "...and no store.");
                    Click(Setup<Button>("report-param-open")); Assert.AreEqual("data-quality", navigated.Last());
                    Click(Card("support-package"));
                    Assert.IsTrue(Descendants(setupHost).OfType<TextBlock>().Any(t => (string?)t.Tag == "report-setup-none"), "The support package says it has no parameters.");

                    // Without an offered store the setup itself blocks: the store combo is off with the reason, the run disabled.
                    var alone = ReportParameterPanel.Build(new ReportParameterPanel.Context(ReportCatalog.Find("orders-csv")!, () => Array.Empty<string>(), new UiPreferenceStore(root), navigated.Add, (_, _, _, _) => Task.FromResult<string?>("ran")));
                    setupHost.Child = alone; Drain(window);
                    var storeAlone = Setup<ComboBox>("report-param-store");
                    Assert.IsFalse(storeAlone.IsEnabled); Assert.IsFalse(Setup<Button>("report-param-run").IsEnabled); StringAssert.Contains(Setup<TextBlock>("report-param-validation").Text, "Mağaza seçin");
                    Assert.IsTrue(Descendants(setupHost).OfType<TextBlock>().Any(t => t.Text.Contains("sunulan mağaza yok")), "The store field's help says no store is offered.");
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
