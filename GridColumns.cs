using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace TrMarketplaceHubDesktop;

/// <summary>What a grid text column holds, and so whether it may be trimmed: prose can be, an identifier or a number never is.</summary>
public enum GridTextKind { Text, Identifier, Number }

/// <summary>The tooltip's text: the cell's value through the central redaction, a raw payload hidden whole, nothing for an empty cell.</summary>
public sealed class GridTooltipConverter : IValueConverter
{
    public static string? Redact(string? text)
    {
        var raw = (text ?? "").Trim(); if (raw.Length == 0) return null;
        return StatusTooltip.LooksLikeRawPayload(raw) ? StatusTooltip.RawPayloadHidden : AuditStore.Redact(raw);
    }
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Redact(value?.ToString());
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Whether a column header belongs to a number column (#865): the shared header style aligns it right. Takes the header and its DisplayIndex, which is coerced once the column is attached.</summary>
public sealed class NumericHeaderConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        => values.Length > 0 && values[0] is System.Windows.Controls.Primitives.DataGridColumnHeader header && header.Column is not null && GridColumns.GetIsNumeric(header.Column);
    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// The grid text column standard (#864). Prose (a title, a source, a status, an error) sits on one line and is
/// trimmed with an ellipsis; while it is trimmed -- and only then -- its cell carries a tooltip with the full text,
/// which the keyboard opens on the focused cell as the mouse does on hover. An identifier or a number is never
/// trimmed and never gets a tooltip. The tooltip passes the redaction every other surface uses. The column adds
/// nothing to the cell's style, so a grid's own cell style (density, selection, focus) stays in force; trimming is
/// measured by the text block itself (the framework on .NET 8 exposes no trimmed flag), on size and text changes.
/// </summary>
public static class GridColumns
{
    public static readonly DependencyProperty IsTrimmedProperty = DependencyProperty.RegisterAttached("IsTrimmed", typeof(bool), typeof(GridColumns), new PropertyMetadata(false));
    public static bool GetIsTrimmed(DependencyObject element) => (bool)element.GetValue(IsTrimmedProperty);
    public static void SetIsTrimmed(DependencyObject element, bool value) => element.SetValue(IsTrimmedProperty, value);

    /// <summary>Set by the prose column's element style: the text block measures its own trimming and hands the cell its tooltip.</summary>
    public static readonly DependencyProperty MonitorTrimmingProperty = DependencyProperty.RegisterAttached("MonitorTrimming", typeof(bool), typeof(GridColumns), new PropertyMetadata(false, OnMonitorChanged));
    public static bool GetMonitorTrimming(DependencyObject element) => (bool)element.GetValue(MonitorTrimmingProperty);
    public static void SetMonitorTrimming(DependencyObject element, bool value) => element.SetValue(MonitorTrimmingProperty, value);

    /// <summary>Set on a number column so the shared header style (the token file) can align its header over the digits.</summary>
    public static readonly DependencyProperty IsNumericProperty = DependencyProperty.RegisterAttached("IsNumeric", typeof(bool), typeof(GridColumns), new PropertyMetadata(false));
    public static bool GetIsNumeric(DependencyObject element) => (bool)element.GetValue(IsNumericProperty);
    public static void SetIsNumeric(DependencyObject element, bool value) => element.SetValue(IsNumericProperty, value);

    static readonly HashSet<string> TextTailWords = new(StringComparer.Ordinal) { "label", "source", "currency", "kind", "status", "name", "text", "mode", "reason", "note", "message", "error", "path", "url", "location", "utc", "date", "time", "at", "class", "mark", "title", "order" };
    static readonly HashSet<string> NumberWords = new(StringComparer.Ordinal) { "price", "cost", "stock", "count", "total", "quantity", "qty", "rate", "amount", "percent", "version", "days", "seconds", "duration", "margin", "fee", "weight", "desi", "score", "added", "updated", "unchanged", "ordered", "shipped", "returned", "cancelled", "outstanding", "orders" };
    static readonly HashSet<string> IdentifierWords = new(StringComparer.Ordinal) { "sku", "barcode", "id", "key", "code", "gtin", "asin", "ean", "tracking" };
    static readonly HashSet<string> MoneyWords = new(StringComparer.Ordinal) { "price", "cost", "total", "amount", "fee", "margin" };
    static readonly HashSet<string> CountWords = new(StringComparer.Ordinal) { "stock", "count", "quantity", "qty" };

    static List<string> Words(string? path) => Regex.Split(path ?? "", @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|[^A-Za-z0-9]+").Where(w => w.Length > 0).Select(w => w.ToLowerInvariant()).ToList();

    /// <summary>
    /// A guess from the bound path's words. The last word decides first: a "...Label", "...Source", "...Currency" or "...Utc" is
    /// prose however it starts (a PriceSource is a source, a CostCurrency a code); a last word that is a number or an identifier
    /// word settles it; then any word does (a FormulaPriceTry is money, TrackingNumbers are identifiers); prose otherwise.
    /// </summary>
    public static GridTextKind KindFor(string path)
    {
        var words = Words(path);
        if (words.Count == 0) return GridTextKind.Text;
        var last = words[^1];
        if (TextTailWords.Contains(last)) return GridTextKind.Text;
        if (NumberWords.Contains(last)) return GridTextKind.Number;
        if (IdentifierWords.Contains(last)) return GridTextKind.Identifier;
        if (words.Any(NumberWords.Contains)) return GridTextKind.Number;
        if (words.Any(IdentifierWords.Contains)) return GridTextKind.Identifier;
        return GridTextKind.Text;
    }

    /// <summary>The decimals a number column shows unless the caller says otherwise: a count none, money two places, anything else as the value comes.</summary>
    public static string? DefaultFormat(string path)
    {
        var words = Words(path);
        if (words.Count == 0 || KindFor(path) != GridTextKind.Number) return null;
        if (CountWords.Contains(words[^1])) return "N0";
        if (MoneyWords.Contains(words[^1]) || words.Any(MoneyWords.Contains)) return "N2";
        return null;
    }

    public static DataGridTextColumn Text(string header, string path, double width, GridTextKind? kind = null, string? format = null)
        => Text(header, path, new DataGridLength(width), kind, format);

    public static DataGridTextColumn Text(string header, string path, DataGridLength width, GridTextKind? kind = null, string? format = null)
    {
        var k = kind ?? KindFor(path);
        if (k == GridTextKind.Number && string.IsNullOrEmpty(format)) format = DefaultFormat(path);
        // A binding formats with the element's xml:lang (en-US unless a window says otherwise), not the thread's culture: a Turkish
        // machine would read "1,234.50" in a grid beside "1.234,50" everywhere else. The column formats with the current culture.
        var binding = new Binding(path) { NotifyOnTargetUpdated = k == GridTextKind.Text, ConverterCulture = CultureInfo.CurrentCulture }; if (!string.IsNullOrEmpty(format)) binding.StringFormat = format;
        var element = new Style(typeof(TextBlock));
        element.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.NoWrap));
        element.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, k == GridTextKind.Text ? TextTrimming.CharacterEllipsis : TextTrimming.None));
        if (k == GridTextKind.Text) element.Setters.Add(new Setter(MonitorTrimmingProperty, true));
        if (k == GridTextKind.Number)
        {
            // Digits line up down the column: right-aligned, tabular figures (every digit the same width); the header follows through the shared style.
            element.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
            element.Setters.Add(new Setter(Typography.NumeralAlignmentProperty, FontNumeralAlignment.Tabular));
        }
        var column = new DataGridTextColumn { Header = header, Binding = binding, Width = width, ElementStyle = element };
        if (k == GridTextKind.Number) SetIsNumeric(column, true);
        return column;
    }

    /// <summary>Whether the block's text is wider than the block: the trimmed state the framework does not expose.</summary>
    public static bool IsTrimmed(TextBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (block.TextTrimming == TextTrimming.None || string.IsNullOrEmpty(block.Text) || block.ActualWidth <= 0) return false;
        var typeface = new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch);
        var full = new FormattedText(block.Text, CultureInfo.CurrentUICulture, block.FlowDirection, typeface, block.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(block).PixelsPerDip).WidthIncludingTrailingWhitespace;
        return full > block.ActualWidth - block.Padding.Left - block.Padding.Right + 0.5;
    }

    static void OnMonitorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block || e.NewValue is not true) return;
        block.SizeChanged += (_, _) => Update(block);
        block.Loaded += (_, _) => Update(block);
        // A recycled row keeps its text block and only rebinds it: the column's binding announces the new text (NotifyOnTargetUpdated).
        block.TargetUpdated += (_, _) => block.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Update(block)));
    }

    static void Update(TextBlock block)
    {
        var trimmed = IsTrimmed(block);
        SetIsTrimmed(block, trimmed);
        DependencyObject? node = block;
        while (node is not null && node is not DataGridCell) node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        if (node is not DataGridCell cell) return;
        if (trimmed) { cell.ToolTip = GridTooltipConverter.Redact(block.Text); ToolTipService.SetShowsToolTipOnKeyboardFocus(cell, true); }
        else cell.ClearValue(FrameworkElement.ToolTipProperty); // a style-provided tooltip, if the grid has one, comes back
    }
}
