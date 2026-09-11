using System.Globalization;
using System.Text;

namespace TrMarketplaceHubDesktop;

public sealed record PurchasePlanningInput(string Sku, string? Brand, string? Category, string Supplier, int Stock, decimal UnitsSoldLast30Days, int MinStock, int TargetStock, decimal LastCost, string Currency, int LeadTimeDays, DateTimeOffset StockAtUtc, DateTimeOffset CostAtUtc);
public sealed record PurchaseSuggestion(PurchasePlanningInput Input, decimal DailyVelocity, decimal DaysOfCover, int SuggestedQuantity, string Status);

public static class PurchasePlanning
{
    public static PurchaseSuggestion Calculate(PurchasePlanningInput input, DateTimeOffset? now = null, TimeSpan? staleAfter = null)
    {
        if (string.IsNullOrWhiteSpace(input.Sku) || string.IsNullOrWhiteSpace(input.Supplier) || input.Stock < 0 || input.UnitsSoldLast30Days < 0 || input.MinStock < 0 || input.TargetStock < input.MinStock || input.LastCost < 0 || input.LeadTimeDays < 0) throw new ArgumentException("Planlama alanları geçersiz.");
        var velocity = decimal.Round(input.UnitsSoldLast30Days / 30m, 4);
        var cover = velocity == 0 ? decimal.MaxValue : decimal.Round(input.Stock / velocity, 2);
        var quantity = Math.Max(0, input.TargetStock - input.Stock);
        var at = now ?? DateTimeOffset.UtcNow; var age = staleAfter ?? TimeSpan.FromDays(2);
        var stale = at - input.StockAtUtc > age || at - input.CostAtUtc > age;
        var status = stale ? "STALE" : quantity > 0 ? "DRAFT_RECOMMENDATION" : "COVERED";
        return new(input, velocity, cover, quantity, status);
    }

    public static IReadOnlyList<PurchaseSuggestion> Filter(IEnumerable<PurchaseSuggestion> items, string? brand = null, string? category = null, string? supplier = null) => items.Where(x => (brand is null || string.Equals(x.Input.Brand, brand, StringComparison.OrdinalIgnoreCase)) && (category is null || string.Equals(x.Input.Category, category, StringComparison.OrdinalIgnoreCase)) && (supplier is null || x.Input.Supplier.Equals(supplier, StringComparison.OrdinalIgnoreCase))).ToArray();

    public static string ExportCsv(IEnumerable<PurchaseSuggestion> items)
    {
        var sb = new StringBuilder("SKU,Supplier,Brand,Category,Stock,DailyVelocity,DaysOfCover,SuggestedQuantity,LastCost,Currency,LeadTimeDays,Status\r\n");
        foreach (var x in items) sb.Append(string.Join(',', Escape(x.Input.Sku), Escape(x.Input.Supplier), Escape(x.Input.Brand), Escape(x.Input.Category), x.Input.Stock.ToString(CultureInfo.InvariantCulture), x.DailyVelocity.ToString(CultureInfo.InvariantCulture), x.DaysOfCover == decimal.MaxValue ? "INF" : x.DaysOfCover.ToString(CultureInfo.InvariantCulture), x.SuggestedQuantity.ToString(CultureInfo.InvariantCulture), x.Input.LastCost.ToString(CultureInfo.InvariantCulture), Escape(x.Input.Currency.ToUpperInvariant()), x.Input.LeadTimeDays.ToString(CultureInfo.InvariantCulture), Escape(x.Status))).Append("\r\n");
        return sb.ToString();
    }
    private static string Escape(string? value) { var s = value ?? ""; return s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s; }
}
