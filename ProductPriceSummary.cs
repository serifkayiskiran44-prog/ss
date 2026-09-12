using System.Globalization;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public sealed record ProductPriceSummaryInfo(
    string SalePrice,
    string Currency,
    string Cost,
    string CostCurrency,
    string Margin,
    string MarginLevel,
    string MarginCaveat,
    string Calculated,
    bool IsCalculationStale,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Order);

/// <summary>
/// The product card's pricing block (#798): sale price, currency, when it was last calculated and whether the
/// margin deserves attention, in one ranked order so the card stops presenting four equally-weighted numbers.
/// It adds no pricing behaviour -- every figure already exists on the product, and the authoritative decision
/// stays the dispatch-time money preflight (#285) with the operator's minimum-margin guard (#790). What is
/// shown here is explicitly approximate, and it is labelled as such rather than implying a verdict.
/// </summary>
public static class ProductPriceSummary
{
    public const string Unknown = "unknown";
    public const string Negative = "negative";
    public const string Thin = "thin";
    public const string Healthy = "healthy";

    /// <summary>Same 24 hour window the money calculator uses for an FX snapshot, so the card and the gate agree on "stale".</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);
    /// <summary>Below this approximate margin the card says "thin" -- a prompt to check, never a block.</summary>
    public const decimal ThinMarginPercent = 10m;
    const string Dash = "—";

    public static ProductPriceSummaryInfo Build(CatalogProduct product, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(product);
        var warnings = new List<string>();

        var hasPrice = product.Price > 0;
        if (!hasPrice) warnings.Add("Satış fiyatı girilmemiş.");

        var sameCurrency = string.Equals(product.Currency?.Trim(), product.CostCurrency?.Trim(), StringComparison.OrdinalIgnoreCase);
        if (hasPrice && product.Cost > 0 && !sameCurrency) warnings.Add("Alış ve satış para birimleri farklı; yaklaşık kâr hesaplanamıyor.");

        string marginLevel = Unknown, marginText = Dash;
        if (hasPrice && sameCurrency && product.Cost > 0)
        {
            var percent = (product.Price - product.Cost) / product.Price * 100m;
            marginText = percent.ToString("N0", CultureInfo.CurrentCulture) + "%";
            marginLevel = percent < 0 ? Negative : percent < ThinMarginPercent ? Thin : Healthy;
            if (marginLevel == Negative) warnings.Add("Yaklaşık kâr negatif: satış fiyatı alış fiyatının altında.");
            else if (marginLevel == Thin) warnings.Add($"Yaklaşık kâr düşük (%{ThinMarginPercent.ToString("N0", CultureInfo.CurrentCulture)} altında); komisyon ve kargo sonrası zarar edebilir.");
        }

        var calculatedAt = product.FxRateDate;
        var stale = calculatedAt is { } at && nowUtc - at > StaleAfter;
        if (stale) warnings.Add("Fiyat hesabı 24 saatten eski; göndermeden önce yenileyin.");

        return new(
            SalePrice: hasPrice ? product.Price.ToString("N2", CultureInfo.CurrentCulture) : Dash,
            Currency: Clamp(product.Currency),
            Cost: product.Cost > 0 ? product.Cost.ToString("N2", CultureInfo.CurrentCulture) : Dash,
            CostCurrency: Clamp(product.CostCurrency),
            Margin: marginText,
            MarginLevel: marginLevel,
            MarginCaveat: "Yaklaşık: komisyon, kargo ve vergi gönderim öncesi hesaplanır.",
            Calculated: calculatedAt is { } when ? Ago(nowUtc - when) : "Hesaplanmadı",
            IsCalculationStale: stale,
            Warnings: warnings,
            Order: ["Satış fiyatı", "Kâr (yaklaşık)", "Son hesaplama"]);
    }

    // A currency label sits beside the price, so it is clamped rather than allowed to run over it; the price
    // itself is always a separate field, never concatenated, for the same reason.
    static string Clamp(string? currency)
    {
        var text = (currency ?? "").Trim().ToUpperInvariant();
        if (text.Length == 0) return Dash;
        return text.Length > 6 ? text[..6] : text;
    }

    static string Ago(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        if (elapsed.TotalMinutes < 1) return "az önce";
        if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes} dakika önce";
        if (elapsed.TotalDays < 1) return $"{(int)elapsed.TotalHours} saat önce";
        return $"{(int)elapsed.TotalDays} gün önce";
    }
}
