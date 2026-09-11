namespace TrMarketplaceHubDesktop;

public sealed record AuditPage(IReadOnlyList<AuditEvent> Items, DateTime? NextBeforeUtc, bool HasMore);

public static class AuditPaging
{
    public static AuditPage Read(AuditStore store, int pageSize, DateTime? beforeUtc = null, string? query = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (pageSize is < 1 or > AuditStore.RetentionLimit) throw new ArgumentOutOfRangeException(nameof(pageSize));
        var items = store.List(AuditStore.RetentionLimit, query).Where(x => !beforeUtc.HasValue || x.AtUtc < beforeUtc.Value).Take(pageSize + 1).ToArray();
        var hasMore = items.Length > pageSize;
        var page = items.Take(pageSize).ToArray();
        return new(page, hasMore && page.Length > 0 ? page[^1].AtUtc : null, hasMore);
    }
}
