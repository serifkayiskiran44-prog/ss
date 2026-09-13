using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #843 on the real matrix panel: with an Etsy store (live capability) and a Trendyol store (local-only in this
// build) the legend above the matrix lists exactly the states on screen with counts, each chip a focusable tab
// stop whose tooltip opens from the keyboard and reads the same in high contrast (glyph + word), and the cells
// carry the legend's description as their tooltip.
[TestClass]
public sealed class ChannelMatrixLegendUiTests
{
    [TestMethod]
    public void TheLegendListsOnlyPresentStatesWithCountsAndKeyboardTooltips()
    {
        var root = Path.Combine(Path.GetTempPath(), "matrix-legend-" + Guid.NewGuid().ToString("N"));
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
                    Assert.AreEqual(2, matrix.Items.Count);
                    var legend = Descendants(panel).OfType<WrapPanel>().Single(w => (string)w.Tag == "channel-matrix-legend");
                    var chips = legend.Children.OfType<Border>().ToList();
                    // A connection saved but never tested is NOT_CONFIGURED -- a real connection problem, so the Etsy cells read "bağlantı"; Trendyol has no live capability in this build, so its cells read "yalnız yerel" whatever its connection says.
                    CollectionAssert.AreEqual(new[] { "AUTH_ERROR", "LOCAL_ONLY" }, chips.Select(c => (string)c.Tag).ToArray(), "Only the two states on screen, in legend order.");
                    string ChipText(Border c) => ((TextBlock)c.Child).Text;
                    Assert.AreEqual("⚠ bağlantı (2)", ChipText(chips[0])); Assert.AreEqual("⊘ yalnız yerel (2)", ChipText(chips[1]));
                    Assert.IsTrue(chips.All(c => c.Focusable && System.Windows.Input.KeyboardNavigation.GetIsTabStop(c) && ToolTipService.GetShowsToolTipOnKeyboardFocus(c) == true), "Chips are tab stops with keyboard tooltips.");
                    StringAssert.Contains(chips[1].ToolTip?.ToString() ?? "", "canlı ilan yeteneği yok");
                    StringAssert.Contains(System.Windows.Automation.AutomationProperties.GetHelpText(chips[0]), "bağlantısı başarısız");
                    Assert.IsTrue(chips.All(c => ChipText(c).Any(ch => !char.IsLetterOrDigit(ch) && !char.IsWhiteSpace(ch) && ch != '(' && ch != ')')), "Every chip leads with a glyph -- colour is never the only signal.");

                    var rows = ((IEnumerable<ChannelMatrixRow>)matrix.ItemsSource).ToList();
                    var trendyolIndex = matrix.Columns.ToList().FindIndex(c => (c.Header?.ToString() ?? "").StartsWith("Trendyol", StringComparison.Ordinal)) - 2;
                    Assert.IsTrue(trendyolIndex >= 0);
                    Assert.AreEqual("⊘ yalnız yerel", rows[0].Labels[trendyolIndex]); StringAssert.Contains(rows[0].Descriptions[trendyolIndex], "canlı ilan");
                    var firstRow = Descendants(matrix).OfType<DataGridRow>().First();
                    var storeCell = Descendants(firstRow).OfType<DataGridCell>().First(c => c.Column.DisplayIndex == 2);
                    Assert.IsNotNull(storeCell.ToolTip, "A store cell carries the legend's description as its tooltip.");
                    Assert.IsTrue(ToolTipService.GetShowsToolTipOnKeyboardFocus(storeCell) == true);
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
