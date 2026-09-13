using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public sealed record ProductRowStateInfo(string Key, string Label, string Glyph, int Severity, string Reason)
{
    /// <summary>Glyph plus the word, so the state is readable without colour and by a screen reader.</summary>
    public string Badge => Glyph.Length == 0 ? Label : Glyph + " " + Label;
}

/// <summary>
/// One semantic state per product row (#794), shared by the badge column, the row border and the tooltip so the
/// three can never disagree. Classification comes from the product's own persisted fields -- there is no second
/// source of truth -- and every state carries signals that survive a monochrome or high-contrast display: a
/// glyph, a word, and a left border weight. Colour is the last layer, never the only one.
/// </summary>
public static class ProductRowState
{
    public const string Normal = "normal";
    public const string Stale = "stale";
    public const string Pending = "pending";
    public const string Warning = "warning";
    public const string Error = "error";
    public const string Disabled = "disabled";

    /// <summary>How long a feed-backed product may go untouched by its source before the row calls itself stale.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);

    public static ProductRowStateInfo Describe(string key) => key switch
    {
        Disabled => new(Disabled, "Pasif", "⛔", 50, "Ürün pasif; satışa çıkmaz."),
        Error => new(Error, "Hatalı", "✖", 40, "Ürün kimliği veya kaynağı sorunlu."),
        Warning => new(Warning, "Şüpheli", "⚠", 30, "Tedarikçi verisinde anomali işaretlendi."),
        Pending => new(Pending, "Bekliyor", "◔", 20, "Gönderim denendi, sonuç bekleniyor."),
        Stale => new(Stale, "Bayat", "⏳", 10, "Kaynak veri uzun süredir güncellenmedi."),
        _ => new(Normal, "Normal", "", 0, ""),
    };

    /// <summary>
    /// Precedence, highest first: disabled (nothing else matters if the row cannot sell), error (identity or
    /// source is broken), warning (the feed looked wrong), pending (a dispatch is unresolved), stale (the feed
    /// has gone quiet). The winner drives the badge and the border; every other condition that also applies is
    /// listed in the reason so nothing is hidden by the precedence.
    /// </summary>
    public static ProductRowStateInfo Classify(CatalogProduct product, DateTime nowUtc, TimeSpan? staleAfter = null)
    {
        ArgumentNullException.ThrowIfNull(product);
        var reasons = new List<string>();
        if (!product.Active) reasons.Add("Pasif ürün");
        if (product.Duplicate) reasons.Add("Yinelenen kimlik");
        if (product.SourceMissing) reasons.Add("Kaynak kaydı bulunamıyor");
        if (product.Anomaly) reasons.Add("Tedarikçi verisinde anomali");
        var pending = product.EtsyCreationAttempted && string.IsNullOrWhiteSpace(product.EtsyListingId);
        if (pending) reasons.Add("Gönderim sonucu bekleniyor");
        // Only a feed-backed product can go stale; a manually maintained one is simply as old as the operator left it.
        var stale = !product.SourceKind.Equals("manual", StringComparison.OrdinalIgnoreCase)
            && product.SourceUpdatedUtc is { } touched && nowUtc - touched > (staleAfter ?? StaleAfter);
        if (stale) reasons.Add("Bayat kaynak verisi");

        var key = !product.Active ? Disabled
            : product.Duplicate || product.SourceMissing ? Error
            : product.Anomaly ? Warning
            : pending ? Pending
            : stale ? Stale
            : Normal;
        var info = Describe(key);
        return info with { Reason = string.Join(" · ", reasons) };
    }

    /// <summary>Left accent stripe weight -- the signal a monochrome or colour-blind display still reads.</summary>
    public static Thickness BorderThickness(string key) => Describe(key).Severity switch
    {
        >= 40 => new Thickness(5, 0, 0, 0),
        >= 10 => new Thickness(3, 0, 0, 0),
        _ => new Thickness(0),
    };

    /// <summary>
    /// The colour layer. Under high contrast the app's palette is abandoned for the user's system colours,
    /// because a hand-picked amber is exactly what a high-contrast theme exists to override.
    /// </summary>
    public static Brush AccentBrush(string key, bool highContrast)
    {
        if (highContrast)
        {
            var brush = Describe(key).Severity switch
            {
                >= 40 => SystemColors.HotTrackBrush,
                >= 20 => SystemColors.HighlightBrush,
                >= 10 => SystemColors.GrayTextBrush,
                _ => SystemColors.WindowTextBrush,
            };
            return new SolidColorBrush(((SolidColorBrush)brush).Color);
        }
        var colour = key switch
        {
            Disabled => Color.FromRgb(98, 108, 116),
            // #817: error and warning accents are the shared severity table's, so the row and the panel cannot drift.
            Error => SeverityStyle.For(SeverityLevel.Blocking, false).Accent,
            Warning => SeverityStyle.For(SeverityLevel.Warning, false).Accent,
            Pending => Color.FromRgb(52, 108, 190),
            Stale => Color.FromRgb(109, 86, 140),
            _ => Colors.Transparent,
        };
        var solid = new SolidColorBrush(colour); solid.Freeze(); return solid;
    }

    /// <summary>
    /// Row tooltip: the state and why, and nothing else. Buying cost, margin and the supplier/source identity
    /// are commercially sensitive and deliberately excluded -- a hover tooltip is the easiest thing to capture
    /// in a screenshot or a screen share. Sanitized and length-capped so no product text can turn it into a
    /// wall of text or smuggle a token through a name field.
    /// </summary>
    public static string Tooltip(ProductRowStateInfo info, CatalogProduct product)
    {
        ArgumentNullException.ThrowIfNull(info); ArgumentNullException.ThrowIfNull(product);
        // #815: the shared template -- status, reason, last change, source, next action -- so this tooltip reads
        // like every other status tooltip in the app and hides what it does not know.
        return StatusTooltip.Compose(new StatusTooltipContent(
            info.Badge, info.Reason, product.UpdatedUtc == default ? null : product.UpdatedUtc,
            string.IsNullOrWhiteSpace(product.SourceId) ? "" : "Kaynak kaydı " + product.SourceId,
            StatusTooltip.NextActionFor(info.Key),
            $"Stok: {product.Stock.ToString(CultureInfo.CurrentCulture)}"), DateTime.UtcNow);
    }

    public static bool IsHighContrast => SystemParameters.HighContrast;
}

/// <summary>Binds a whole <see cref="CatalogProduct"/> row to its badge text for the product list's state column.</summary>
public sealed class ProductRowStateBadgeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is CatalogProduct product ? ProductRowState.Classify(product, DateTime.UtcNow).Badge : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
