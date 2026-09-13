using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #865 (DESIGN: Numeric column alignment standard). A number column (a price, a stock, a count) is right-aligned with
// tabular figures so digits line up down the column, its header sits over the digits (right-aligned too), and its
// decimals follow one rule by kind: money two places, counts none, unless the caller says otherwise. A negative keeps
// its sign, zero is written out, a large value carries the culture's group separators, and a currency is never a
// symbol pasted into the number: it lives in its own text column. The numbers are device-independent.
[TestClass]
public sealed class NumericColumnsTests
{
    sealed record Row(string Title, string Currency, decimal Price, int Stock);

    [TestMethod]
    public void NumbersAreRightAlignedWithTabularFiguresAndKindFormats()
    {
        RunSta(() =>
        {
            // The formats by kind: money two places, counts none, anything else only what the caller says.
            Assert.AreEqual("N2", GridColumns.DefaultFormat("Price")); Assert.AreEqual("N2", GridColumns.DefaultFormat("FormulaPriceTry")); Assert.AreEqual("N2", GridColumns.DefaultFormat("Cost"));
            Assert.AreEqual("N0", GridColumns.DefaultFormat("Stock")); Assert.AreEqual("N0", GridColumns.DefaultFormat("TotalCount")); Assert.AreEqual("N0", GridColumns.DefaultFormat("Quantity"));
            Assert.IsNull(GridColumns.DefaultFormat("VatRate")); Assert.IsNull(GridColumns.DefaultFormat("Name")); Assert.IsNull(GridColumns.DefaultFormat("DurationSeconds"));

            // The column shape: right-aligned tabular digits and the kind's format; an explicit format wins; prose and identifiers stay left.
            var price = GridColumns.Text("Fiyat", "Price", 80);
            Assert.AreEqual(TextAlignment.Right, Setter(price.ElementStyle, TextBlock.TextAlignmentProperty));
            Assert.AreEqual(FontNumeralAlignment.Tabular, Setter(price.ElementStyle, Typography.NumeralAlignmentProperty));
            Assert.IsTrue(GridColumns.GetIsNumeric(price), "The column says it is numeric, so the shared header style can align its header.");
            Assert.AreEqual("N2", ((Binding)price.Binding).StringFormat);
            Assert.AreEqual("N4", ((Binding)GridColumns.Text("Kur", "AppliedTryRate", 80, format: "N4").Binding).StringFormat);
            foreach (var column in new[] { GridColumns.Text("Ad", "Name", 100), GridColumns.Text("SKU", "Sku", 80) })
            {
                Assert.IsFalse(column.ElementStyle.Setters.OfType<Setter>().Any(s => s.Property == TextBlock.TextAlignmentProperty), $"{column.Header} is not right-aligned");
                Assert.IsFalse(GridColumns.GetIsNumeric(column)); Assert.IsNull(((Binding)column.Binding).StringFormat);
            }

            // In a real grid under the shared styles: negative, zero and large values, two currencies, aligned right edges, right-aligned headers, and the same DIP numbers at 2x.
            var rows = new List<Row> { new("Kısa", "TRY", -1234.5m, 0), new("Orta uzunlukta ad", "EUR", 0m, 12), new("Uzun", "USD", 1234567.891m, 1200000) };
            var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, Width = 420, Height = 160, ItemsSource = rows };
            var title = GridColumns.Text("Ad", "Title", 90); var currency = GridColumns.Text("Döviz", "Currency", 60); var priceColumn = GridColumns.Text("Fiyat", "Price", 120); var stock = GridColumns.Text("Stok", "Stock", 100);
            grid.Columns.Add(title); grid.Columns.Add(currency); grid.Columns.Add(priceColumn); grid.Columns.Add(stock);
            var window = new Window { Content = grid, Width = 520, Height = 240, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
            window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = DesignTokens.Source });
            try
            {
                window.Show(); Drain(window);
                var culture = CultureInfo.CurrentCulture;
                for (var scale = 1; scale <= 2; scale++)
                {
                    if (scale == 2) { grid.LayoutTransform = new ScaleTransform(2, 2); Drain(window); }
                    var priceBlocks = new List<TextBlock>(); var stockBlocks = new List<TextBlock>(); var titleBlocks = new List<TextBlock>();
                    for (var i = 0; i < rows.Count; i++)
                    {
                        var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(i); Assert.IsNotNull(row, $"row {i} at {scale}x");
                        var cells = Descendants(row).OfType<DataGridCell>().ToList();
                        titleBlocks.Add((TextBlock)cells[0].Content); priceBlocks.Add((TextBlock)cells[2].Content); stockBlocks.Add((TextBlock)cells[3].Content);
                        Assert.AreEqual(rows[i].Price.ToString("N2", culture), priceBlocks[i].Text, $"price {i} at {scale}x");
                        Assert.AreEqual(rows[i].Stock.ToString("N0", culture), stockBlocks[i].Text, $"stock {i} at {scale}x");
                        Assert.AreEqual(rows[i].Currency, ((TextBlock)cells[1].Content).Text, "the currency is its own column");
                        Assert.IsFalse(priceBlocks[i].Text.Any(c => c is '₺' or '€' or '$' or '£'), "a number never carries a currency symbol");
                        Assert.AreEqual(TextAlignment.Right, priceBlocks[i].TextAlignment); Assert.AreEqual(FontNumeralAlignment.Tabular, Typography.GetNumeralAlignment(priceBlocks[i]));
                    }
                    StringAssert.StartsWith(priceBlocks[0].Text, culture.NumberFormat.NegativeSign, "a negative keeps its sign");
                    Assert.AreEqual(0m.ToString("N2", culture), priceBlocks[1].Text, "zero is written out");
                    StringAssert.Contains(priceBlocks[2].Text, culture.NumberFormat.NumberGroupSeparator, "a large value carries group separators");
                    StringAssert.Contains(stockBlocks[2].Text, culture.NumberFormat.NumberGroupSeparator);

                    // Right edges line up on the cell's right padding edge whatever the digit count; a title's left edge sits on the left.
                    var rightEdges = priceBlocks.Select(RightEdge).ToList();
                    foreach (var (block, edge) in priceBlocks.Zip(rightEdges)) Assert.AreEqual(block.ActualWidth - block.Padding.Right, edge, 1.0, $"a right-aligned number ends at the right edge ({block.Text}) at {scale}x");
                    Assert.AreEqual(rightEdges.Max() - rightEdges.Min(), 0, 1.0, $"right edges line up at {scale}x");
                    Assert.AreEqual(titleBlocks[0].Padding.Left, LeftEdge(titleBlocks[0]), 1.0, "prose starts at the left");

                    var headers = Descendants(grid).OfType<DataGridColumnHeader>().Where(h => h.Column is not null).ToList();
                    Assert.AreEqual(HorizontalAlignment.Right, headers.Single(h => h.Column == priceColumn).HorizontalContentAlignment, $"the price header sits over the digits at {scale}x");
                    Assert.AreEqual(HorizontalAlignment.Right, headers.Single(h => h.Column == stock).HorizontalContentAlignment);
                    Assert.AreNotEqual(HorizontalAlignment.Right, headers.Single(h => h.Column == title).HorizontalContentAlignment, "a prose header stays left");
                    Assert.AreNotEqual(HorizontalAlignment.Right, headers.Single(h => h.Column == currency).HorizontalContentAlignment);
                }
            }
            finally { window.Close(); }
        });
    }

    static double RightEdge(TextBlock block) => block.ContentEnd.GetCharacterRect(LogicalDirection.Backward).Right;
    static double LeftEdge(TextBlock block) => block.ContentStart.GetCharacterRect(LogicalDirection.Forward).Left;

    static object Setter(Style style, DependencyProperty property) => style.Setters.OfType<Setter>().Single(s => s.Property == property).Value;

    static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        var count = node is Visual ? VisualTreeHelper.GetChildrenCount(node) : 0;
        for (var i = 0; i < count; i++) { var child = VisualTreeHelper.GetChild(node, i); yield return child; foreach (var d in Descendants(child)) yield return d; }
    }

    static void Drain(Window window) { window.UpdateLayout(); for (var i = 0; i < 4; i++) window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { })); }

    static void RunSta(Action body)
    {
        Exception failure = null;
        var thread = new Thread(() => { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); try { body(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new AssertFailedException(failure.ToString());
    }
}
