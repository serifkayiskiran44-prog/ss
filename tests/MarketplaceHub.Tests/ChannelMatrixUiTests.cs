using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

// #842: a 1000 × 20 pivot rendered in a window scrolls both ways with the product columns frozen in place and the
// store headers above, virtualized, with a cell selectable from the keyboard; and the real panel builds its
// matrix from the store, offering only the shell's stores -- a connection the shell does not offer has no column.
[TestClass]
public sealed class ChannelMatrixUiTests
{
    static ChannelListingMatrixRow Row(string productId, string name, string channel, string channelName, string shop, string mapping) =>
        new(productId, "SKU-" + productId, name, channel, channelName, shop, mapping, "", "None", "", null, "CONNECTED", "ProductsRead");

    [TestMethod]
    public void AThousandByTwentyMatrixKeepsItsAxesWhileScrollingAndStaysVirtualizedAndKeyboardSelectable()
    {
        RunSta(() =>
        {
            var statuses = new[] { "SYNCED", "MISSING", "ERROR", "STALE", "PENDING", "DRAFT" };
            var rows = new List<ChannelListingMatrixRow>(20000);
            for (var p = 0; p < 1000; p++) for (var s = 0; s < 20; s++) rows.Add(Row($"p{p:D4}", $"Ürün {p:D4}", "etsy", "Etsy", $"S{s:D2}", statuses[(p + s) % statuses.Length]));
            var matrix = ChannelMatrixPivot.Build(rows, null);
            var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, EnableRowVirtualization = true, EnableColumnVirtualization = true, SelectionUnit = DataGridSelectionUnit.Cell, SelectionMode = DataGridSelectionMode.Single, HeadersVisibility = DataGridHeadersVisibility.Column };
            VirtualizingPanel.SetIsVirtualizing(grid, true); VirtualizingPanel.SetVirtualizationMode(grid, VirtualizationMode.Recycling);
            ChannelListingMatrixPanel.RenderMatrix(grid, matrix);
            var window = new Window { Content = grid, Width = 900, Height = 500, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
            try
            {
                window.Show(); Drain(window);
                Assert.AreEqual(22, grid.Columns.Count); Assert.AreEqual(2, grid.FrozenColumnCount, "SKU and product are the frozen row axis.");
                Assert.AreEqual(1000, grid.Items.Count);
                var realized = Descendants(grid).OfType<DataGridRow>().Count();
                Assert.IsTrue(realized > 0 && realized < 100, $"Row virtualization: {realized} rows realized of 1000.");
                Assert.IsTrue(grid.Columns.All(c => c.Width.IsAbsolute), "DIP column widths.");

                var scroll = Descendants(grid).OfType<ScrollViewer>().First();
                var firstRow = Descendants(grid).OfType<DataGridRow>().First();
                var skuCell = Descendants(firstRow).OfType<DataGridCell>().First(c => c.Column.DisplayIndex == 0);
                var skuBefore = skuCell.TransformToAncestor(window).Transform(new Point(0, 0)).X;
                var header = Descendants(grid).OfType<DataGridColumnHeadersPresenter>().First();
                var headerBefore = header.TransformToAncestor(window).Transform(new Point(0, 0)).Y;

                scroll.ScrollToHorizontalOffset(scroll.ScrollableWidth); scroll.ScrollToVerticalOffset(scroll.ScrollableHeight / 2); Drain(window);
                Assert.IsTrue(scroll.HorizontalOffset > 0 && scroll.VerticalOffset > 0, "Both axes scrolled.");
                var rowNow = Descendants(grid).OfType<DataGridRow>().First(r => r.IsVisible);
                var skuNow = Descendants(rowNow).OfType<DataGridCell>().First(c => c.Column.DisplayIndex == 0);
                Assert.AreEqual(skuBefore, skuNow.TransformToAncestor(window).Transform(new Point(0, 0)).X, 0.5, "The frozen SKU column has not moved horizontally.");
                Assert.AreEqual(headerBefore, header.TransformToAncestor(window).Transform(new Point(0, 0)).Y, 0.5, "The store headers have not moved vertically.");
                Assert.IsTrue(Descendants(rowNow).OfType<DataGridCell>().Any(c => c.Column.DisplayIndex >= 20), "Far-right store columns are what is in view now.");
                Assert.IsTrue(Descendants(grid).OfType<DataGridRow>().Count() < 100, "Still virtualized after scrolling.");

                var target = (ChannelMatrixRow)grid.Items[500];
                grid.ScrollIntoView(target, grid.Columns[5]); grid.CurrentCell = new DataGridCellInfo(target, grid.Columns[5]); grid.SelectedCells.Clear(); grid.SelectedCells.Add(grid.CurrentCell); Drain(window);
                Assert.IsTrue(grid.CurrentCell.IsValid && grid.CurrentCell.Item == target && grid.CurrentCell.Column.DisplayIndex == 5, "A single store cell is the current, keyboard-navigable cell.");
                Assert.AreEqual(1, grid.SelectedCells.Count);
                StringAssert.Contains(System.Windows.Automation.AutomationProperties.GetName(Descendants(grid).OfType<DataGridRow>().First(r => r.Item == target)), "sorunlu mağaza");
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void ThePanelBuildsTheMatrixFromTheStoreAndOffersOnlyTheShellsStores()
    {
        var root = Path.Combine(Path.GetTempPath(), "matrix-panel-" + Guid.NewGuid().ToString("N"));
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
                connections.Save("etsy", "S1", "Etsy S1", true); connections.Save("ebay", "E9", "eBay E9", true);
                SqliteConnection.ClearAllPools();

                var offered = new[] { DashboardStoreFilter.KeyFor("etsy", "S1") };
                var panel = ChannelListingMatrixPanel.Create(root, null, () => offered);
                var window = new Window { Content = panel, Width = 1200, Height = 800, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
                try
                {
                    window.Show();
                    var matrix = Descendants(panel).OfType<DataGrid>().Single(g => (string)g.Tag == "channel-matrix");
                    for (var i = 0; i < 200 && matrix.Items.Count == 0; i++) { Drain(window); Thread.Sleep(50); }
                    Assert.AreEqual(2, matrix.Items.Count, "One row per product.");
                    var headers = matrix.Columns.Select(c => c.Header?.ToString() ?? "").ToList();
                    CollectionAssert.AreEqual(new[] { "SKU", "Ürün", "Etsy\nS1" }, headers.ToArray(), "Only the offered store has a column; eBay E9 exists but is not offered by the shell.");
                    Assert.AreEqual(Visibility.Visible, matrix.Visibility, "The matrix is the default view.");
                    var view = Descendants(panel).OfType<ComboBox>().Single(c => System.Windows.Automation.AutomationProperties.GetName(c) == "Görünüm");
                    view.SelectedIndex = 1; Drain(window);
                    Assert.AreEqual(Visibility.Collapsed, matrix.Visibility, "The flat list is still available as a view.");
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
