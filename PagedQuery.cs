namespace TrMarketplaceHubDesktop;

public sealed record PageResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

public static class PagedQuery
{
    public static PageResult<T> Execute<T>(IEnumerable<T> source, int page, int pageSize)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (page < 0 || pageSize is < 1 or > 1000) throw new ArgumentOutOfRangeException();
        var total = source is ICollection<T> collection ? collection.Count : source.Count();
        return new(source.Skip(page * pageSize).Take(pageSize).ToArray(), page, pageSize, total);
    }
}
