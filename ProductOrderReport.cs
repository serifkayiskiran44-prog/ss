namespace TrMarketplaceHubDesktop;

public sealed record ProductOrderReportResult(int OrderCount, int Quantity, string LatestOrderId, string LatestChannelShop)
{
    public static readonly ProductOrderReportResult Empty = new(0, 0, "—", "—");
}

public static class ProductOrderReport
{
    public static ProductOrderReportResult Build(string? sku, IEnumerable<OrderSnapshot> orders)
    {
        if (string.IsNullOrWhiteSpace(sku) || orders is null) return ProductOrderReportResult.Empty;
        var matches = orders
            .Where(order => order.Items.Any(item => string.Equals(item.Sku, sku.Trim(), StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(order => order.UpdatedAt)
            .ToList();
        if (matches.Count == 0) return ProductOrderReportResult.Empty;
        var latest = matches[0];
        return new(matches.Count, matches.Sum(order => order.Items.Where(item => string.Equals(item.Sku, sku.Trim(), StringComparison.OrdinalIgnoreCase)).Sum(item => item.Quantity)), latest.OrderId, $"{latest.Marketplace} / {latest.ShopId}");
    }
}
