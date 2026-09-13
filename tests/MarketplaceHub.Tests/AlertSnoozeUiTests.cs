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

// #852 on the real dashboard: an open alert's row offers a bounded snooze; choosing one moves the alert into the
// snoozed list with its end and lifts it out of the open groups; the ledger is untouched; a refresh keeps it
// snoozed while counting the sighting; a rebuilt dashboard (restart) still shows it snoozed; "Ertelemeyi kaldır"
// brings it back; the snooze store holds identity and scope only.
[TestClass]
public sealed class AlertSnoozeUiTests
{
    [TestMethod]
    public void SnoozeHidesUntilTheTimeSurvivesRefreshAndRestartAndCanBeLifted()
    {
        var root = Path.Combine(Path.GetTempPath(), "alert-snooze-ui-" + Guid.NewGuid().ToString("N"));
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
                FrameworkElement panel = null!; Window window = null!;
                void Open() { panel = DashboardPanel.Create(root, _ => { }); window = new Window { Content = panel, Width = 1300, Height = 950, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 }; window.Show(); Drain(window); WaitUntil(window, () => Descendants(panel).OfType<TextBlock>().Any(t => (string?)t.Tag == "alerts-headline"), "the dashboard load"); }
                StackPanel Centre() => Descendants(panel).OfType<StackPanel>().Single(s => s.Children.OfType<TextBlock>().Any(t => (string?)t.Tag == "alerts-headline"));
                string Headline() => Centre().Children.OfType<TextBlock>().Single(t => (string?)t.Tag == "alerts-headline").Text;
                Expander? ProductsGroup() => Centre().Children.OfType<StackPanel>().Single(s => (string?)s.Tag == "alerts-unresolved").Children.OfType<Expander>().SingleOrDefault(e => ((TextBlock)e.Header).Text.Contains("Uyarı · Ürünler"));
                StackPanel? Snoozed() => Centre().Children.OfType<StackPanel>().SingleOrDefault(s => (string?)s.Tag == "alerts-snoozed");
                LocalNotification Stock() => new NotificationStore(root).List().Single(a => a.Title == "Kritik stok");
                void Refresh() { var refresh = Descendants(panel).OfType<Button>().Single(b => b.Content as string == "Durumu yenile"); refresh.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); WaitUntil(window, () => refresh.IsEnabled, "the refresh"); Drain(window); }

                Open();
                try
                {
                    var products = ProductsGroup(); Assert.IsNotNull(products);
                    var chooser = Descendants(products!).OfType<ComboBox>().Single(c => (string?)c.Tag == "alert-snooze");
                    Assert.AreEqual(AlertSnoozeRules.Options.Count, chooser.Items.Count); Assert.IsTrue(chooser.IsTabStop); StringAssert.StartsWith(AutomationProperties.GetName(chooser), "Ertele: Kritik stok");
                    Assert.IsNull(Snoozed());

                    chooser.SelectedItem = AlertSnoozeRules.Option("1h"); Drain(window);
                    Assert.IsNull(ProductsGroup(), "A snoozed alert leaves the open groups."); StringAssert.Contains(Headline(), "1 ertelendi");
                    var snoozed = Snoozed(); Assert.IsNotNull(snoozed); Assert.AreEqual(1, snoozed!.Children.Count);
                    var row = (DockPanel)snoozed.Children[0]; var text = Descendants(row).OfType<TextBlock>().Last().Text;
                    StringAssert.Contains(text, "Kritik stok"); StringAssert.Contains(text, "daha ertelendi"); StringAssert.Contains(text, "daha ciddi bir olay ertelemeyi aşar");
                    Assert.IsTrue(Stock().IsOpen && !Stock().Acknowledged, "The ledger is untouched by a snooze.");
                    var stored = new AlertSnoozeStore(root).Active(DateTime.UtcNow).Single();
                    Assert.AreEqual(Stock().Fingerprint, stored.Fingerprint); Assert.AreEqual("products", stored.Source); Assert.AreEqual("WARNING", stored.SeverityAtSnooze);

                    Refresh();
                    Assert.IsNull(ProductsGroup(), "A refresh keeps the snooze."); Assert.AreEqual(2, Stock().Occurrences, "...while the sighting is still counted.");
                }
                finally { window.Close(); }

                // Restart: a rebuilt dashboard reads the snooze back.
                Open();
                try
                {
                    Assert.IsNull(ProductsGroup(), "The snooze survives a restart."); Assert.IsNotNull(Snoozed());
                    var lift = Descendants(Snoozed()!).OfType<Button>().Single(b => (string?)b.Tag == "alert-unsnooze");
                    Assert.IsTrue(lift.IsTabStop); lift.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(window);
                    Assert.IsNotNull(ProductsGroup(), "Lifting the snooze brings the alert back."); Assert.IsNull(Snoozed()); Assert.IsFalse(Headline().Contains("ertelendi"));
                    Assert.AreEqual(0, new AlertSnoozeStore(root).Active(DateTime.UtcNow).Count);
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
