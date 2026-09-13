using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #865 on the real main window: the product grid's price and stock columns are right-aligned with tabular figures
// under right-aligned headers, the price carries two decimals and the stock none in the current culture, the
// currency is its own left column, and the name and SKU columns stay left with their headers.
[TestClass]
public sealed class NumericColumnsUiTests
{
    [TestMethod]
    public void TheProductGridAlignsItsNumbersRightUnderRightAlignedHeaders()
    {
        var root = Path.Combine(Path.GetTempPath(), "numeric-columns-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            MainWindow window = null;
            try
            {
                Directory.CreateDirectory(root);
                var store = new CatalogStore(root); var source = new XmlSource { Id = "feed-1", Name = "Fixture feed" };
                store.Import(source, new[]
                {
                    new CatalogProduct { SourceId = source.Id, Sku = "SKU-1", Name = "Kısa", Price = 1234.5m, Stock = 7, Currency = "TRY" },
                    new CatalogProduct { SourceId = source.Id, Sku = "SKU-2", Name = "Uzun adlı ürün", Price = 0.5m, Stock = 1200000, Currency = "EUR" },
                });
                window = new MainWindow(root); window.Show(); Drain(window);
                Navigate(window, "products"); Drain(window);
                var grid = (DataGrid)typeof(MainWindow).GetField("products", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                DataGridTextColumn Column(string path) => grid.Columns.OfType<DataGridTextColumn>().First(c => ((System.Windows.Data.Binding)c.Binding).Path.Path == path);
                var price = Column("Price"); var stock = Column("Stock"); var name = Column("Name"); var sku = Column("Sku");
                Assert.IsTrue(GridColumns.GetIsNumeric(price)); Assert.IsTrue(GridColumns.GetIsNumeric(stock)); Assert.IsFalse(GridColumns.GetIsNumeric(name)); Assert.IsFalse(GridColumns.GetIsNumeric(sku));

                var culture = CultureInfo.CurrentCulture;
                var byPrice = new Dictionary<string, TextBlock>();
                for (var i = 0; i < 2; i++)
                {
                    var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(i); Assert.IsNotNull(row, $"row {i}");
                    var cells = Descendants(row).OfType<DataGridCell>().ToList();
                    var product = (CatalogProduct)row.Item;
                    var priceBlock = (TextBlock)cells[grid.Columns.IndexOf(price)].Content; var stockBlock = (TextBlock)cells[grid.Columns.IndexOf(stock)].Content;
                    Assert.AreEqual(product.Price.ToString("N2", culture), priceBlock.Text, "two decimals in the current culture");
                    Assert.AreEqual(product.Stock.ToString("N0", culture), stockBlock.Text, "a count has no decimals and keeps its group separators");
                    Assert.AreEqual(TextAlignment.Right, priceBlock.TextAlignment); Assert.AreEqual(TextAlignment.Right, stockBlock.TextAlignment);
                    Assert.AreEqual(FontNumeralAlignment.Tabular, Typography.GetNumeralAlignment(priceBlock));
                    Assert.AreEqual(priceBlock.ActualWidth - priceBlock.Padding.Right, priceBlock.ContentEnd.GetCharacterRect(LogicalDirection.Backward).Right, 1.0, "the number ends on the cell's right edge");
                    Assert.AreNotEqual(TextAlignment.Right, ((TextBlock)cells[grid.Columns.IndexOf(name)].Content).TextAlignment, "prose stays left");
                    byPrice[product.Sku] = priceBlock;
                }
                Assert.AreEqual(byPrice["SKU-1"].ContentEnd.GetCharacterRect(LogicalDirection.Backward).Right, byPrice["SKU-2"].ContentEnd.GetCharacterRect(LogicalDirection.Backward).Right, 1.0, "right edges line up across rows");

                var headers = Descendants(grid).OfType<DataGridColumnHeader>().Where(h => h.Column is not null).ToList();
                Assert.AreEqual(HorizontalAlignment.Right, headers.Single(h => h.Column == price).HorizontalContentAlignment, "the price header sits over the digits");
                Assert.AreEqual(HorizontalAlignment.Right, headers.Single(h => h.Column == stock).HorizontalContentAlignment);
                Assert.AreNotEqual(HorizontalAlignment.Right, headers.Single(h => h.Column == name).HorizontalContentAlignment);
                Assert.AreNotEqual(HorizontalAlignment.Right, headers.Single(h => h.Column == sku).HorizontalContentAlignment);
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

    static void Navigate(MainWindow window, string key) => typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { key, true });
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
