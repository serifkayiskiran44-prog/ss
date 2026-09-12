using System.Globalization;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public sealed record ProductStockLevelInfo(string Key, string Label, string Glyph);

public sealed record ProductStockSummaryInfo(
    string OnHand,
    string Available,
    string SafetyBuffer,
    string MaximumCap,
    string Withheld,
    string Level,
    string Label,
    string Glyph,
    string Updated,
    bool IsStale,
    IReadOnlyList<string> Warnings);

/// <summary>
/// The product card's stock composition (#799): what is on hand, what the channel would actually be sent, what
/// the safety buffer and any maximum cap hold back, and how old the figure is. The available quantity is *not*
/// computed here -- <see cref="CatalogStore.PreviewStock"/> owns that projection, and this summary is handed
/// its result. Duplicating the arithmetic would let the card and the dispatcher disagree, which is exactly the
/// failure the issue's "hesaplama owner'ını kopyalama" guards against. Levels carry a glyph and a word so the
/// composition survives a monochrome display.
/// </summary>
public static class ProductStockSummary
{
    public const string Ready = "ready";
    public const string BufferOnly = "buffer-only";
    public const string Capped = "capped";
    public const string OutOfStock = "out-of-stock";
    public const string Invalid = "invalid";
    public const string NoProjection = "no-projection";

    /// <summary>Same window the row-state hierarchy uses, so "stale" means one thing across the app.</summary>
    public static TimeSpan StaleAfter => ProductRowState.StaleAfter;
    const string Dash = "—";

    public static ProductStockLevelInfo Describe(string key) => key switch
    {
        BufferOnly => new(BufferOnly, "Tamamı güvenlik payında", "▤"),
        Capped => new(Capped, "Üst sınır uygulanıyor", "⊤"),
        OutOfStock => new(OutOfStock, "Stok yok", "∅"),
        Invalid => new(Invalid, "Stok verisi geçersiz", "✖"),
        NoProjection => new(NoProjection, "Kanal projeksiyonu yok", "?"),
        _ => new(Ready, "Satışa uygun", "●"),
    };

    public static ProductStockSummaryInfo Build(CatalogProduct product, StockPolicy? policy, int? projectedAvailable, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(product);
        var warnings = new List<string>();
        if (product.Stock < 0) warnings.Add("Stok negatif görünüyor; kayıt elle düzeltilmeli.");

        var stale = !product.SourceKind.Equals("manual", StringComparison.OrdinalIgnoreCase)
            && product.SourceUpdatedUtc is { } touched && nowUtc - touched > StaleAfter;
        if (stale) warnings.Add("Stok verisi kaynaktan 7 günden uzun süredir güncellenmedi.");

        var level =
            product.Stock < 0 ? Invalid
            : projectedAvailable is null ? NoProjection
            : product.Stock == 0 ? OutOfStock
            : projectedAvailable == 0 ? BufferOnly
            : policy?.MaximumStock is { } cap && projectedAvailable == cap && product.Stock - (policy?.SafetyStock ?? 0) > cap ? Capped
            : Ready;
        var info = Describe(level);

        // "Withheld" is stated as the difference between what exists and what would be sent, rather than
        // re-derived from the buffer and cap: the projection may apply rules this card does not know about.
        var withheld = projectedAvailable is { } available && product.Stock > 0
            ? Math.Max(0, product.Stock - available).ToString("N0", CultureInfo.CurrentCulture)
            : Dash;

        return new(
            OnHand: Quantity(product.Stock),
            Available: projectedAvailable is { } value ? Quantity(value) : Dash,
            SafetyBuffer: policy is null ? Dash : Quantity(policy.SafetyStock),
            MaximumCap: policy?.MaximumStock is { } max ? Quantity(max) : Dash,
            Withheld: withheld,
            Level: level,
            Label: info.Label,
            Glyph: info.Glyph,
            Updated: product.SourceUpdatedUtc is { } when ? Ago(nowUtc - when) : Dash,
            IsStale: stale,
            Warnings: warnings);
    }

    static string Quantity(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    static string Ago(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        if (elapsed.TotalMinutes < 1) return "az önce";
        if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes} dakika önce";
        if (elapsed.TotalDays < 1) return $"{(int)elapsed.TotalHours} saat önce";
        return $"{(int)elapsed.TotalDays} gün önce";
    }
}
