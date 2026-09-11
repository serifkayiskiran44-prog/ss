namespace TrMarketplaceHubDesktop;

public sealed record StockLocation(string Id, string ShopId, string Name, string Kind);
public sealed record LocationStock(string LocationId, string ShopId, string ProductId, int Quantity, string Source, DateTimeOffset AtUtc);
public sealed record MultiLocationStockView(string ShopId, string ProductId, int Total, int Visible, int NegativeLocations, IReadOnlyList<string> Anomalies);

public static class MultiLocationStock
{
    public static MultiLocationStockView Summarize(IEnumerable<StockLocation> locations, IEnumerable<LocationStock> snapshots, string shopId, string productId, Func<StockLocation, bool>? visible = null)
    {
        var known = locations.Where(x => x.ShopId == shopId).ToDictionary(x => x.Id, StringComparer.Ordinal);
        var scoped = snapshots.Where(x => x.ShopId == shopId && x.ProductId == productId && known.ContainsKey(x.LocationId)).ToArray();
        var anomalies = new List<string>();
        if (snapshots.Any(x => x.ProductId == productId && (x.ShopId != shopId || !known.ContainsKey(x.LocationId)))) anomalies.Add("WRONG_LOCATION_OR_SHOP");
        if (scoped.Any(x => x.Quantity < 0)) anomalies.Add("NEGATIVE_STOCK");
        var duplicate = scoped.GroupBy(x => x.LocationId).Any(g => g.Count() > 1); if (duplicate) anomalies.Add("MULTIPLE_SNAPSHOTS");
        var total = scoped.GroupBy(x => x.LocationId).Sum(g => g.OrderByDescending(x => x.AtUtc).First().Quantity);
        var visibleTotal = scoped.Where(x => visible?.Invoke(known[x.LocationId]) ?? true).GroupBy(x => x.LocationId).Sum(g => g.OrderByDescending(x => x.AtUtc).First().Quantity);
        return new(shopId, productId, total, visibleTotal, scoped.Count(x => x.Quantity < 0), anomalies);
    }

    public static IReadOnlyList<LocationStock> Timeline(IEnumerable<LocationStock> snapshots, string locationId, string shopId, string productId) => snapshots.Where(x => x.LocationId == locationId && x.ShopId == shopId && x.ProductId == productId).OrderByDescending(x => x.AtUtc).ToArray();
    public static IReadOnlyList<LocationStock> Filter(IEnumerable<LocationStock> snapshots, string? source, string? productId) => snapshots.Where(x => (source is null || x.Source.Equals(source, StringComparison.OrdinalIgnoreCase)) && (productId is null || x.ProductId.Equals(productId, StringComparison.OrdinalIgnoreCase))).ToArray();
}
