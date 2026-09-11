namespace TrMarketplaceHubDesktop;

public sealed record SupplierSource(string SourceId, string SupplierId, int Priority, bool Included, bool Healthy);
public sealed record SourceSelection(string Status, SupplierSource? Selected, IReadOnlyList<string> Reasons);

public static class LinkedSourceGraph
{
    public static SourceSelection Select(IEnumerable<SupplierSource> sources)
    {
        var rows = sources.Where(x => x.Included).OrderBy(x => x.Priority).ToArray();
        var healthy = rows.FirstOrDefault(x => x.Healthy);
        return healthy is null ? new("SOURCE_UNAVAILABLE", null, rows.Select(x => $"{x.SourceId}:unhealthy").ToArray()) : new("SELECTED", healthy, []);
    }
}
