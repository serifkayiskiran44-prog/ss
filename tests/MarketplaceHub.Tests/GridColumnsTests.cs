using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #864 (DESIGN: DataGrid text truncation and tooltip policy). A shared text column: prose (a title, a source, a
// status, an error) is trimmed with an ellipsis on one line and, only while it is trimmed, carries a tooltip with
// the full text that the keyboard can open on the cell as the mouse can on hover; an identifier does the same
// (#867); a number is never trimmed and never gets a tooltip. The tooltip text passes the redaction that every
// other surface uses, so a token, an address or a raw payload never opens in a bubble.
[TestClass]
public sealed class GridColumnsTests
{
    sealed record Row(string Title, string Sku, decimal Price);

    [TestMethod]
    public void ProseIsTrimmedWithARedactedKeyboardTooltipAndIdentifiersAreNot()
    {
        RunSta(() =>
        {
            Assert.AreEqual(GridTextKind.Number, GridColumns.KindFor("Price")); Assert.AreEqual(GridTextKind.Number, GridColumns.KindFor("Stock")); Assert.AreEqual(GridTextKind.Number, GridColumns.KindFor("TotalCount")); Assert.AreEqual(GridTextKind.Number, GridColumns.KindFor("VatRate"));
            Assert.AreEqual(GridTextKind.Identifier, GridColumns.KindFor("Sku")); Assert.AreEqual(GridTextKind.Identifier, GridColumns.KindFor("Barcode")); Assert.AreEqual(GridTextKind.Identifier, GridColumns.KindFor("OrderId")); Assert.AreEqual(GridTextKind.Identifier, GridColumns.KindFor("ShopId"));
            Assert.AreEqual(GridTextKind.Text, GridColumns.KindFor("Name")); Assert.AreEqual(GridTextKind.Text, GridColumns.KindFor("LastError")); Assert.AreEqual(GridTextKind.Text, GridColumns.KindFor("Status")); Assert.AreEqual(GridTextKind.Text, GridColumns.KindFor("Location"));

            // The prose column: one line, an ellipsis, a cell tooltip only while trimmed, and the keyboard can open it.
            var title = GridColumns.Text("Başlık", "Title", 80);
            Assert.AreEqual(TextTrimming.CharacterEllipsis, Setter(title.ElementStyle, TextBlock.TextTrimmingProperty)); Assert.AreEqual(TextWrapping.NoWrap, Setter(title.ElementStyle, TextBlock.TextWrappingProperty));
            Assert.AreEqual(true, Setter(title.ElementStyle, GridColumns.MonitorTrimmingProperty), "The text block watches its own trimming.");
            Assert.IsNull(title.CellStyle, "The column leaves the grid's own cell style (density, selection, focus) in place; the tooltip lands on the cell while trimmed.");

            // An identifier too long for its column ends in an ellipsis and carries itself whole in the tooltip (#867: a hard cut hides more); a number is never trimmed and never gets a tooltip.
            var sku = GridColumns.Text("SKU", "Sku", 80);
            Assert.AreEqual(TextTrimming.CharacterEllipsis, Setter(sku.ElementStyle, TextBlock.TextTrimmingProperty)); Assert.AreEqual(true, Setter(sku.ElementStyle, GridColumns.MonitorTrimmingProperty));
            var price = GridColumns.Text("Fiyat", "Price", 80, format: "N2");
            Assert.AreEqual(TextTrimming.None, Setter(price.ElementStyle, TextBlock.TextTrimmingProperty));
            Assert.IsFalse(price.ElementStyle.Setters.OfType<Setter>().Any(s => s.Property == GridColumns.MonitorTrimmingProperty), "a number: never trimmed, never a tooltip");
            Assert.AreEqual("N2", ((Binding)price.Binding).StringFormat);
            // A column is never narrower than its own header (#867).
            Assert.IsTrue(GridColumns.HeaderMinWidth("Sipariş durumu (kaynak)") > 143, "the header's text, its padding and the sort arrow's room");
            Assert.AreEqual(GridColumns.HeaderMinWidth("Sipariş durumu (kaynak)"), GridColumns.Text("Sipariş durumu (kaynak)", "RawStatus", 150).MinWidth);
            Assert.AreEqual(0, GridColumns.HeaderMinWidth(""));

            // The tooltip converter redacts and hides what must not open in a bubble.
            var converter = new GridTooltipConverter();
            Assert.IsNull(converter.Convert("", typeof(object), null, CultureInfo.CurrentCulture)); Assert.IsNull(converter.Convert(null, typeof(object), null, CultureInfo.CurrentCulture));
            var redacted = (string)converter.Convert("Kaynak hatası: Authorization: Bearer abc123xyz; mail ali@example.com", typeof(object), null, CultureInfo.CurrentCulture)!;
            Assert.IsFalse(redacted.Contains("abc123xyz")); Assert.IsFalse(redacted.Contains("ali@example.com")); StringAssert.Contains(redacted, "Kaynak hatası");
            Assert.AreEqual(StatusTooltip.RawPayloadHidden, converter.Convert("{\"error\":\"invalid_client\",\"detail\":\"x\"}", typeof(object), null, CultureInfo.CurrentCulture));

            // In a real grid: a long Turkish and Unicode title in a narrow column is trimmed and carries the full, redacted text; widened, it is not trimmed and carries nothing; the identifier beside it is never trimmed.
            var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, Width = 400, Height = 120, ItemsSource = new List<Row> { new("Şüpheli işlemlerin çözümlenmesi ve iade uzlaşması — 日本語 テキスト ✔ Authorization: Bearer top-secret-token", "SKU-ÇĞİÖŞÜ-000123456789", 1234.5m) } };
            var titleColumn = GridColumns.Text("Başlık", "Title", 120); var skuColumn = GridColumns.Text("SKU", "Sku", 60); var priceColumn = GridColumns.Text("Fiyat", "Price", 70, format: "N2");
            grid.Columns.Add(titleColumn); grid.Columns.Add(skuColumn); grid.Columns.Add(priceColumn);
            var window = new Window { Content = grid, Width = 500, Height = 200, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
            try
            {
                window.Show(); Drain(window);
                var cells = Descendants(grid).OfType<DataGridCell>().ToList(); Assert.AreEqual(3, cells.Count);
                var titleCell = cells[0]; var titleText = (TextBlock)titleCell.Content;
                Assert.IsTrue(GridColumns.GetIsTrimmed(titleText), "A long title in a 120-DIP column is trimmed.");
                var bubble = (string)titleCell.ToolTip; StringAssert.StartsWith(bubble, "Şüpheli işlemlerin"); StringAssert.Contains(bubble, "日本語"); Assert.IsFalse(bubble.Contains("top-secret-token"), "The tooltip is redacted.");
                Assert.IsTrue(ToolTipService.GetShowsToolTipOnKeyboardFocus(titleCell), "The keyboard opens the tooltip on the cell.");
                var skuText = (TextBlock)cells[1].Content; Assert.IsTrue(GridColumns.GetIsTrimmed(skuText), "A 24-character identifier in a 60-DIP column ends in an ellipsis."); Assert.AreEqual(TextTrimming.CharacterEllipsis, skuText.TextTrimming); Assert.AreEqual("SKU-ÇĞİÖŞÜ-000123456789", (string)cells[1].ToolTip, "and carries itself whole in the tooltip.");
                var priceText = (TextBlock)cells[2].Content; Assert.AreEqual(TextTrimming.None, priceText.TextTrimming); Assert.IsNull(cells[2].ToolTip, "a number is never trimmed and has no tooltip");
                titleColumn.Width = 900; Drain(window);
                Assert.IsFalse(GridColumns.GetIsTrimmed(titleText), "Widened, the title is whole."); Assert.IsNull(titleCell.ToolTip, "No tooltip when nothing is hidden.");
            }
            finally { window.Close(); }
        });
    }

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
