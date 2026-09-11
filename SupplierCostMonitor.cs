using System.Globalization;

namespace TrMarketplaceHubDesktop;

public sealed record SupplierCostSnapshot(string SupplierId, string Source, string ProductId, string? Brand, string? Category, decimal Cost, string Currency, DateTimeOffset CapturedUtc);
public sealed record SupplierCostChange(SupplierCostSnapshot Current, SupplierCostSnapshot? Previous, decimal AmountDelta, decimal PercentDelta, bool Alert, bool Stale, string Status);

public static class SupplierCostMonitor
{
    public static SupplierCostChange Compare(SupplierCostSnapshot current, SupplierCostSnapshot? previous, decimal riseThresholdPercent = 10, decimal fallThresholdPercent = 10, TimeSpan? staleAfter = null, DateTimeOffset? now = null)
    {
        if (current.Cost < 0 || string.IsNullOrWhiteSpace(current.Currency)) throw new ArgumentException("Maliyet negatif olamaz ve para birimi zorunludur.");
        var amount = previous is null ? 0 : current.Cost - previous.Cost;
        var percent = previous is null || previous.Cost == 0 ? 0 : amount / previous.Cost * 100;
        var alert = previous is not null && ((percent >= riseThresholdPercent && riseThresholdPercent >= 0) || (percent <= -Math.Abs(fallThresholdPercent)));
        var stale = (now ?? DateTimeOffset.UtcNow) - current.CapturedUtc > (staleAfter ?? TimeSpan.FromDays(2));
        return new(current, previous, decimal.Round(amount, 4), decimal.Round(percent, 4), alert, stale, stale ? "STALE" : alert ? "ALERT" : "OK");
    }

    public static IReadOnlyList<SupplierCostChange> Filter(IEnumerable<SupplierCostChange> changes, string? source = null, string? product = null, string? brand = null, string? category = null) => changes.Where(x => (source is null || x.Current.Source.Equals(source, StringComparison.OrdinalIgnoreCase)) && (product is null || x.Current.ProductId.Equals(product, StringComparison.OrdinalIgnoreCase)) && (brand is null || string.Equals(x.Current.Brand, brand, StringComparison.OrdinalIgnoreCase)) && (category is null || string.Equals(x.Current.Category, category, StringComparison.OrdinalIgnoreCase))).ToArray();
    public static string Format(decimal amount, string currency) => amount.ToString("0.####", CultureInfo.InvariantCulture) + " " + currency.ToUpperInvariant();
    public static async Task<IReadOnlyList<SupplierCostChange>> BatchAsync(IEnumerable<(SupplierCostSnapshot Current, SupplierCostSnapshot? Previous)> pairs, decimal rise, decimal fall, CancellationToken cancellationToken = default)
    { var result = new List<SupplierCostChange>(); foreach (var pair in pairs) { cancellationToken.ThrowIfCancellationRequested(); result.Add(Compare(pair.Current, pair.Previous, rise, fall)); await Task.Yield(); } return result; }
}
