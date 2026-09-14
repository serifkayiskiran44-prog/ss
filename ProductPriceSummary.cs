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
    IReadOnlyList<string> Order,
    string CostOrigin = "", // #922: the words for where the cost came from
    string CostCompleteness = "UNKNOWN", string MissingFees = "", // #927: COMPLETE, INCOMPLETE_COST, or UNKNOWN without a rule to ask; the fees the rule lacks, named
    string MarginKind = "approximate", IReadOnlyList<string>? Breakdown = null); // #928: "net" when the money gate computed it from the rule, with its explanation; "approximate" otherwise

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
    public const string IncompleteCost = "incomplete-cost"; // #927: a required fee is missing -- no margin is shown as an estimate

    /// <summary>Same 24 hour window the money calculator uses for an FX snapshot, so the card and the gate agree on "stale".</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);
    /// <summary>Below this approximate margin the card says "thin" -- a prompt to check, never a block.</summary>
    public const decimal ThinMarginPercent = 10m;
    const string Dash = "—";

    public static ProductPriceSummaryInfo Build(CatalogProduct product, DateTime nowUtc, Func<string, XmlSource?>? sourceById = null, PricePolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(product);
        var warnings = new List<string>();

        var hasPrice = product.Price > 0;
        if (!hasPrice) warnings.Add("Satış fiyatı girilmemiş.");
        if (product.Cost <= 0) warnings.Add("Alış fiyatı girilmemiş; kâr hesaplanamaz."); // #922: never a margin against a cost nobody entered

        var sameCurrency = string.Equals(product.Currency?.Trim(), product.CostCurrency?.Trim(), StringComparison.OrdinalIgnoreCase);
        if (hasPrice && product.Cost > 0 && !sameCurrency) warnings.Add("Alış ve satış para birimleri farklı; yaklaşık kâr hesaplanamıyor.");

        string marginLevel = Unknown, marginText = Dash; var marginKind = "approximate"; IReadOnlyList<string>? breakdown = null;
        // #927: with a rule to ask, a required fee it lacks makes the cost incomplete -- the card names the fees and shows no margin at all rather than an estimate.
        var missingFees = policy is null ? Array.Empty<string>() : MoneyPriceCalculator.MissingFees(policy.CommissionPercent, policy.EstimatedShippingTry, policy.TransactionCostTry);
        var completeness = policy is null ? "UNKNOWN" : missingFees.Count > 0 ? "INCOMPLETE_COST" : "COMPLETE";
        if (missingFees.Count > 0) { marginLevel = IncompleteCost; warnings.Add($"Eksik maliyet kalemleri: {string.Join(", ", missingFees)}; net kâr tahmin edilmez."); }
        // #928: with a complete rule in the product's currency the card shows the money gate's own net -- the same result and the same lines the preview shows -- instead of an approximation.
        else if (policy is not null && hasPrice && product.Cost > 0 && string.Equals(product.Currency?.Trim(), policy.Currency, StringComparison.OrdinalIgnoreCase) && Net(product, policy, nowUtc) is { } net)
        {
            marginKind = "net"; breakdown = net.Explanation; marginText = net.MarginPercent.ToString("N0", CultureInfo.CurrentCulture) + "%";
            marginLevel = net.NetContribution < 0 ? Negative : net.MarginPercent < ThinMarginPercent ? Thin : Healthy;
            if (marginLevel == Negative) warnings.Add("Net kâr negatif: kuralın masrafları düşülünce satış fiyatı maliyeti karşılamıyor.");
            else if (marginLevel == Thin) warnings.Add($"Net kâr düşük (%{ThinMarginPercent.ToString("N0", CultureInfo.CurrentCulture)} altında).");
        }
        else if (hasPrice && sameCurrency && product.Cost > 0)
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
            MarginCaveat: marginKind == "net" ? "Net: kuralın komisyon, kargo, işlem ve KDV'si düşüldü; gönderim öncesi para kapısıyla aynı hesap." : "Yaklaşık: komisyon, kargo ve vergi gönderim öncesi hesaplanır.", // #928
            Calculated: calculatedAt is { } when ? Ago(nowUtc - when) : "Hesaplanmadı",
            IsCalculationStale: stale,
            Warnings: warnings,
            Order: ["Satış fiyatı", "Kâr (yaklaşık)", "Son hesaplama"],
            CostOrigin: CostProvenance.Describe(product, sourceById, nowUtc), // #922: the net margin names where its cost came from
            CostCompleteness: completeness, MissingFees: string.Join(", ", missingFees), // #927
            MarginKind: marginKind, Breakdown: breakdown); // #928
    }

    /// <summary>#928: the money gate's result for the product's own price under the rule, when the gate could compute it (ready or a negative margin); null when it could not, so the card falls back to its approximation.</summary>
    static MoneyPriceResult? Net(CatalogProduct product, PricePolicy policy, DateTime nowUtc)
    {
        var isTry = string.Equals(policy.Currency, "TRY", StringComparison.OrdinalIgnoreCase); var now = new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc));
        var result = MoneyPriceCalculator.Calculate(new MoneyPriceInput(product.Sku, policy.Channel, policy.Shop, product.Price, product.Cost, policy.Currency)
        {
            CommissionRatePercent = policy.CommissionPercent, EstimatedShipping = policy.EstimatedShippingTry, TransactionCost = policy.TransactionCostTry, VatRatePercent = policy.VatRatePercent ?? product.VatRate, VatIncludedInSale = policy.VatIncludedInSale,
            FxRateTryPerUnit = isTry ? null : policy.TryPerUnit, FxSnapshotUtc = isTry ? now : policy.FxRateObservedUtc, AsOfUtc = now,
        });
        return result.Status is MoneyPriceStatus.Ready or MoneyPriceStatus.BlockedNegativeMargin ? result : null;
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
