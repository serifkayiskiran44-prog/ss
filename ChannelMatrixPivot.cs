namespace TrMarketplaceHubDesktop;

/// <summary>One store column of the matrix: a channel + shop pair, keyed the way the dashboard keys stores.</summary>
public sealed record ChannelMatrixColumn(string Key, string Channel, string ChannelName, string ShopId)
{
    public string Header => $"{ChannelName}\n{ShopId}";
}

/// <summary>One cell: the listing state of one product in one store, with a marker and a word that read without colour.</summary>
public sealed record ChannelMatrixCell(string MappingStatus, string AuthStatus, string ListingId, string SyncStatus, string LastError)
{
    public string Marker => AuthStatus == "AUTH_ERROR" ? "⚠" : MappingStatus switch { "SYNCED" => "✔", "ERROR" => "✖", "MISSING" => "○", "STALE" => "◔", "PENDING" => "⏳", "DRAFT" => "✎", _ => "·" };
    public string Word => AuthStatus == "AUTH_ERROR" ? "bağlantı" : MappingStatus switch { "SYNCED" => "yayında", "ERROR" => "hata", "MISSING" => "eşleme yok", "STALE" => "bayat", "PENDING" => "bekliyor", "DRAFT" => "taslak", _ => MappingStatus.ToLowerInvariant() };
    public string Label => $"{Marker} {Word}";
    public SeverityLevel Level => AuthStatus == "AUTH_ERROR" || MappingStatus == "ERROR" ? SeverityLevel.Blocking : MappingStatus is "STALE" or "MISSING" ? SeverityLevel.Warning : MappingStatus == "SYNCED" ? SeverityLevel.Success : SeverityLevel.Info;
}

/// <summary>One product row: identity first, then one cell per column in column order (an empty cell where the product has no entry for that store).</summary>
public sealed record ChannelMatrixRow(string ProductId, string Sku, string ProductName, IReadOnlyList<ChannelMatrixCell?> Cells)
{
    public static readonly ChannelMatrixCell Empty = new("NONE", "", "", "None", "");
    /// <summary>Bound by index from the grid's generated columns.</summary>
    public IReadOnlyList<string> Labels => Cells.Select(c => (c ?? Empty).Label).ToList();
    public int Problems => Cells.Count(c => c is not null && c.Level >= SeverityLevel.Warning);
}

public sealed record ChannelMatrix(IReadOnlyList<ChannelMatrixColumn> Columns, IReadOnlyList<ChannelMatrixRow> Rows)
{
    public int CellCount => Columns.Count * Rows.Count;
}

/// <summary>
/// The product × store pivot of the listing matrix (#842): the flat rows the service builds become one row per
/// product and one column per channel/shop, so the product axis can be frozen and the store axis stays in the
/// header while a large matrix scrolls. Columns exist only for stores the shell offers (the dashboard's store
/// keys): a store that is not offered has no column and its cells are dropped, whatever the flat rows carried.
/// Rows sort by product name then SKU; columns by channel name then shop, so the same data always lays out the same.
/// </summary>
public static class ChannelMatrixPivot
{
    public static ChannelMatrix Build(IEnumerable<ChannelListingMatrixRow> rows, IReadOnlyCollection<string>? allowedStoreKeys)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var list = rows.ToList();
        var allowed = allowedStoreKeys is null ? null : new HashSet<string>(allowedStoreKeys, StringComparer.Ordinal);
        var columns = list.Select(r => new ChannelMatrixColumn(DashboardStoreFilter.KeyFor(r.Channel, r.ShopId), r.Channel.Trim().ToLowerInvariant(), r.ChannelName, r.ShopId.Trim()))
            .Where(c => allowed is null || allowed.Contains(c.Key))
            .GroupBy(c => c.Key, StringComparer.Ordinal).Select(g => g.First())
            .OrderBy(c => c.ChannelName, StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.ShopId, StringComparer.Ordinal).ToList();
        var index = columns.Select((c, i) => (c.Key, i)).ToDictionary(x => x.Key, x => x.i, StringComparer.Ordinal);
        var products = list.GroupBy(r => r.ProductId, StringComparer.Ordinal).Select(g =>
        {
            var first = g.First(); var cells = new ChannelMatrixCell?[columns.Count];
            foreach (var r in g) if (index.TryGetValue(DashboardStoreFilter.KeyFor(r.Channel, r.ShopId), out var i)) cells[i] = new(r.MappingStatus, r.AuthStatus, r.ListingId, r.SyncStatus, r.LastError);
            return new ChannelMatrixRow(g.Key, first.Sku, first.ProductName, cells);
        }).OrderBy(r => r.ProductName, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Sku, StringComparer.Ordinal).ToList();
        return new(columns, products);
    }
}
