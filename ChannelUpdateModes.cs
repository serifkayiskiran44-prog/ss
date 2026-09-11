namespace TrMarketplaceHubDesktop;

public enum ChannelUpdateMode { OnlyPrice, OnlyStock, Content, Full }
public sealed record ChannelUpdatePlan(ChannelUpdateMode Mode, IReadOnlySet<string> IncludedFields, IReadOnlySet<string> ExcludedFields, string Status);

public static class ChannelUpdateModePlanner
{
    private static readonly IReadOnlySet<string> Price = new HashSet<string>(["price"], StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlySet<string> Stock = new HashSet<string>(["stock"], StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlySet<string> Content = new HashSet<string>(["title", "description", "category", "brand", "images"], StringComparer.OrdinalIgnoreCase);
    public static ChannelUpdatePlan Preview(ChannelUpdateMode mode, IEnumerable<string> requestedFields)
    {
        var requested = requestedFields.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allowed = mode switch { ChannelUpdateMode.OnlyPrice => Price, ChannelUpdateMode.OnlyStock => Stock, ChannelUpdateMode.Content => Content, _ => requested };
        var included = requested.Intersect(allowed, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excluded = requested.Except(included, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new(mode, included, excluded, excluded.Count == 0 ? "READY" : "PARTIAL");
    }
}
