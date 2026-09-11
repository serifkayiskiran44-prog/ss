namespace TrMarketplaceHubDesktop;

public sealed record ReportAggregate(int RowCount, decimal TotalAmount, decimal AverageAmount);

public static class ReportAnalytics
{
    public static ReportAggregate Aggregate(IEnumerable<IReadOnlyDictionary<string, object?>> rows, string amountColumn)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (string.IsNullOrWhiteSpace(amountColumn)) throw new ArgumentException("Tutar kolonu zorunlu.", nameof(amountColumn));
        var values = rows.Select(row => row.TryGetValue(amountColumn, out var value) && value is decimal amount ? amount : 0m).ToArray();
        return new(values.Length, values.Sum(), values.Length == 0 ? 0m : values.Average());
    }
}
