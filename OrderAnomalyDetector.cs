namespace TrMarketplaceHubDesktop;

public sealed record OrderAnomaly(string Key, string Status, IReadOnlyList<string> OrderIds);

public static class OrderAnomalyDetector
{
    public static IReadOnlyList<OrderAnomaly> FindDuplicateTracking(IEnumerable<OrderSnapshot> orders)
    {
        return orders.SelectMany(o => o.Shipments.Where(s => !string.IsNullOrWhiteSpace(s.TrackingNumber)).Select(s => (o.OrderId, Tracking: s.TrackingNumber.Trim())))
            .GroupBy(x => x.Tracking, StringComparer.OrdinalIgnoreCase).Where(g => g.Select(x => x.OrderId).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(g => new OrderAnomaly(g.Key, "DUPLICATE_TRACKING", g.Select(x => x.OrderId).Distinct(StringComparer.Ordinal).ToArray())).ToArray();
    }
}
