using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #844 on the real matrix panel: the legend chips toggle "any of" row filters with counts that stay those of the
// whole matrix; a store switch (shop filter) recomputes both; a filter that matches nothing in the current
// store shows the zero-result text; a reset clears; chips are keyboard toggles.
[TestClass]
public sealed class ChannelMatrixReadinessFilterUiTests
{
    [TestMethod]
    public void ChipsToggleFiltersCountsStayWholeStoreSwitchRecomputesAndZeroResultIsNamed()
    {
        var root = Path.Combine(Path.GetTempPath(), "matrix-filter-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            try
            {
                Directory.CreateDirectory(root);
                var catalog = new CatalogStore(root);
                var source = new XmlSource { Id = Guid.NewGuid().ToString("N"), Name = "seed", Location = "https://seed.example.com/f.xml", ItemPath = "/p", PriceMode = "Simple", ExchangeRate = 1, AutoFx = false, Currency = "TRY", CostCurrency = "TRY", Fields = new Dictionary<string, string> { ["Sku"] = "s", ["Name"] = "n" } };
                catalog.SaveSource(source);
                catalog.Import(source, new List<CatalogProduct>
                {
                    new() { Sku = "A", Name = "Kupa", Price = 5, Currency = "TRY", Stock = 30, SourceId = source.Id, SourceKind = "xml", Description = "Uzun bir açıklama metni.", ImageUrls = "https://cdn.example.com/a.jpg" },
                    new() { Sku = "B", Name = "Tabak", Price = 5, Currency = "TRY", Stock = 30, SourceId = source.Id, SourceKind = "xml", Description = "Uzun bir açıklama metni.", ImageUrls = "https://cdn.example.com/b.jpg" },
                });
                var connections = new MarketplaceConnectionStore(root);
                connections.Save("etsy", "S1", "Etsy S1", true); connections.Save("trendyol", "T1", "Trendyol T1", true);
                SqliteConnection.ClearAllPools();

                var panel = ChannelListingMatrixPanel.Create(root, null, () => new[] { DashboardStoreFilter.KeyFor("etsy", "S1"), DashboardStoreFilter.KeyFor("trendyol", "T1") });
                var window = new Window { Content = panel, Width = 1200, Height = 800, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
                try
                {
                    window.Show();
                    var matrix = Descendants(panel).OfType<DataGrid>().Single(g => (string)g.Tag == "channel-matrix");
                    for (var i = 0; i < 200 && matrix.Items.Count == 0; i++) { Drain(window); Thread.Sleep(50); }
                    var legend = Descendants(panel).OfType<WrapPanel>().Single(w => (string)w.Tag == "channel-matrix-legend");
                    List<ToggleButton> Chips() => legend.Children.OfType<ToggleButton>().ToList();
                    ToggleButton Chip(string key) => Chips().Single(c => (string)c.Tag == key);
                    string ChipText(ToggleButton c) => ((TextBlock)c.Content).Text;
                    var empty = Descendants(panel).OfType<TextBlock>().Single(t => (string)t.Tag == "channel-matrix-empty");
                    var shop = Descendants(panel).OfType<TextBox>().Single(t => t.ToolTip?.ToString() == "Mağaza filtresi");

                    Assert.AreEqual(2, matrix.Items.Count); Assert.AreEqual(2, Chips().Count); Assert.IsTrue(Chips().All(c => c.IsChecked == false && c.Focusable && c.IsTabStop));
                    Assert.AreEqual(Visibility.Collapsed, empty.Visibility);

                    // Toggle the connection-problem state: both products have an Etsy cell, so both stay; the chip reads as on.
                    Chip("AUTH_ERROR").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(window);
                    Assert.AreEqual(2, matrix.Items.Count); Assert.IsTrue(Chip("AUTH_ERROR").IsChecked == true); StringAssert.Contains(System.Windows.Automation.AutomationProperties.GetName(Chip("AUTH_ERROR")), "filtre açık");
                    Assert.IsTrue(legend.Children.OfType<Button>().Any(b => (string)b.Tag == "channel-matrix-filter-reset"), "A reset appears while a filter is active.");

                    // Store switch to Trendyol only: the pivot has no connection-problem cells there -> zero result, named; counts recomputed for that store.
                    shop.Text = "T1"; Drain(window);
                    Assert.AreEqual(0, matrix.Items.Count); Assert.AreEqual(Visibility.Visible, empty.Visibility); StringAssert.Contains(empty.Text, "bağlantı"); StringAssert.Contains(empty.Text, "filtreyi kaldırın");
                    Assert.IsFalse(Chips().Any(c => (string)c.Tag == "AUTH_ERROR"), "No connection-problem cell exists in this store, so its chip is not offered here...");
                    Assert.AreEqual("⊘ yalnız yerel (2)", ChipText(Chip("LOCAL_ONLY")), "...while the local-only count is that of the whole store view, not of the filtered rows.");

                    // Combined: add the local-only state -> rows return (any of).
                    Chip("LOCAL_ONLY").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(window);
                    Assert.AreEqual(2, matrix.Items.Count); Assert.AreEqual(Visibility.Collapsed, empty.Visibility);

                    // Back to all stores: both chips present, both on, everything shown; reset clears both.
                    shop.Text = ""; Drain(window);
                    Assert.AreEqual(2, Chips().Count); Assert.IsTrue(Chips().All(c => c.IsChecked == true));
                    legend.Children.OfType<Button>().Single(b => (string)b.Tag == "channel-matrix-filter-reset").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(window);
                    Assert.IsTrue(Chips().All(c => c.IsChecked == false)); Assert.AreEqual(2, matrix.Items.Count); Assert.IsFalse(legend.Children.OfType<Button>().Any());
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
