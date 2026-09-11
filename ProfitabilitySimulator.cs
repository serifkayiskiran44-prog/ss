using System.Globalization;

namespace TrMarketplaceHubDesktop;

public sealed record ProfitabilityInput(string Sku, string Channel, decimal SalePrice, decimal Cost, decimal VatRate, decimal CommissionRate, decimal ShippingCost, decimal TransactionCost, string Currency, DateTimeOffset CostAtUtc, DateTimeOffset CommissionAtUtc, DateTimeOffset? ShippingAtUtc = null);
public sealed record ProfitabilityResult(ProfitabilityInput Input, decimal GrossContribution, decimal NetContribution, decimal MarginPercent, bool Stale, string Status);

public static class ProfitabilitySimulator
{
    public static ProfitabilityResult Simulate(ProfitabilityInput input, DateTimeOffset? now = null, TimeSpan? staleAfter = null)
    {
        if (input.SalePrice < 0 || input.Cost < 0 || input.VatRate < 0 || input.CommissionRate < 0 || input.ShippingCost < 0 || input.TransactionCost < 0 || string.IsNullOrWhiteSpace(input.Currency)) throw new ArgumentException("Kârlılık girdileri geçersiz.");
        var gross = input.SalePrice - input.Cost;
        var net = input.SalePrice - input.SalePrice * input.VatRate / 100m - input.SalePrice * input.CommissionRate / 100m - input.Cost - input.ShippingCost - input.TransactionCost;
        var margin = input.SalePrice == 0 ? 0 : decimal.Round(net / input.SalePrice * 100m, 4);
        var at = now ?? DateTimeOffset.UtcNow; var maxAge = staleAfter ?? TimeSpan.FromDays(2); var stale = at - input.CostAtUtc > maxAge || at - input.CommissionAtUtc > maxAge || (input.ShippingAtUtc is { } s && at - s > maxAge);
        return new(input, decimal.Round(gross, 4), decimal.Round(net, 4), margin, stale, stale ? "STALE" : net < 0 ? "NEGATIVE_MARGIN" : margin < 5 ? "LOW_MARGIN" : "OK");
    }
    public static IReadOnlyList<ProfitabilityResult> Filter(IEnumerable<ProfitabilityResult> results, string? channel = null, string? sku = null) => results.Where(x => (channel is null || x.Input.Channel.Equals(channel, StringComparison.OrdinalIgnoreCase)) && (sku is null || x.Input.Sku.Equals(sku, StringComparison.OrdinalIgnoreCase))).ToArray();
    public static string Format(decimal value, string currency) => value.ToString("0.####", CultureInfo.InvariantCulture) + " " + currency.ToUpperInvariant();
}
