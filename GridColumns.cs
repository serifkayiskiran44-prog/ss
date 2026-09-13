using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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

    static readonly HashSet<string> NumberWords = new(StringComparer.Ordinal) { "price", "cost", "stock", "count", "total", "quantity", "qty", "rate", "amount", "percent", "version", "days", "seconds", "duration", "margin", "fee", "weight", "desi", "score", "added", "updated", "unchanged" };
    static readonly HashSet<string> IdentifierWords = new(StringComparer.Ordinal) { "sku", "barcode", "id", "key", "code", "gtin", "asin", "ean", "tracking" };

    /// <summary>A guess from the bound path's words: a "...Label" is prose however it is named; a number or an identifier by its usual words; prose otherwise.</summary>
    public static GridTextKind KindFor(string path)
    {
        var words = Regex.Split(path ?? "", @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|[^A-Za-z0-9]+").Where(w => w.Length > 0).Select(w => w.ToLowerInvariant()).ToList();
        if (words.Count == 0 || words[^1] == "label") return GridTextKind.Text;
        if (words.Any(NumberWords.Contains)) return GridTextKind.Number;
        if (words.Any(IdentifierWords.Contains)) return GridTextKind.Identifier;
        return GridTextKind.Text;
    }

    public static DataGridTextColumn Text(string header, string path, double width, GridTextKind? kind = null, string? format = null)
        => Text(header, path, new DataGridLength(width), kind, format);

    public static DataGridTextColumn Text(string header, string path, DataGridLength width, GridTextKind? kind = null, string? format = null)
    {
        var k = kind ?? KindFor(path);
        var binding = new Binding(path) { NotifyOnTargetUpdated = k == GridTextKind.Text }; if (!string.IsNullOrEmpty(format)) binding.StringFormat = format;
        var element = new Style(typeof(TextBlock));
        element.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.NoWrap));
        element.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, k == GridTextKind.Text ? TextTrimming.CharacterEllipsis : TextTrimming.None));
        if (k == GridTextKind.Text) element.Setters.Add(new Setter(MonitorTrimmingProperty, true));
        return new DataGridTextColumn { Header = header, Binding = binding, Width = width, ElementStyle = element };
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
