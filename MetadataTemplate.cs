namespace TrMarketplaceHubDesktop;

public sealed record MetadataTemplate(string Channel, string ShopId, string Category, int Version, IReadOnlyDictionary<string, string> Values, bool IsStale = false);

public static class MetadataTemplateResolver
{
    public static IReadOnlyDictionary<string, string> Resolve(string channel, string shopId, string category, string productId, IEnumerable<MetadataTemplate> templates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel); ArgumentException.ThrowIfNullOrWhiteSpace(shopId); ArgumentException.ThrowIfNullOrWhiteSpace(category); ArgumentNullException.ThrowIfNull(templates);
        var scoped = templates.Where(x => x.Channel.Equals(channel, StringComparison.OrdinalIgnoreCase) && (x.ShopId == "*" || x.ShopId.Equals(shopId, StringComparison.Ordinal)) && x.Category.Equals(category, StringComparison.OrdinalIgnoreCase) && !x.IsStale).OrderBy(x => x.ShopId == "*" ? 0 : 1).ToArray();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var template in scoped) foreach (var pair in template.Values) result[pair.Key] = pair.Value;
        return result;
    }
}
