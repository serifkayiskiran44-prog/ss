namespace TrMarketplaceHubDesktop;

public sealed record ChannelListingMatrixRow(string ProductId, string Sku, string ProductName, string Channel, string ChannelName, string ShopId, string MappingStatus, string ListingId, string SyncStatus, string LastError, DateTime? LastSyncUtc, string AuthStatus, string Capabilities)
{
    public string SearchText => $"{Sku} {ProductName} {Channel} {ChannelName} {ShopId} {ListingId} {MappingStatus} {SyncStatus} {LastError}";
}

public sealed class ChannelListingMatrixService
{
    readonly string? directory;
    public ChannelListingMatrixService(string? dataDirectory = null) => directory = dataDirectory;

    public IReadOnlyList<ChannelListingMatrixRow> Build()
    {
        var catalog = new Catalog.CatalogStore(directory).Products();
        var plans = new ChannelProductsStore(directory).List().ToDictionary(x => (x.ChannelId.ToLowerInvariant(), x.ShopId, x.ProductId));
        var connections = new MarketplaceConnectionStore(directory).List().Where(x => x.Enabled).ToList();
        var sync = new Catalog.SyncStore(directory).List();
        var now = DateTime.UtcNow;
        var result = new List<ChannelListingMatrixRow>(catalog.Count * Math.Max(1, connections.Count));
        foreach (var connection in connections)
        {
            var definition = MarketplaceConnectionCatalog.Get(connection.Channel);
            foreach (var product in catalog)
            {
                plans.TryGetValue((connection.Channel, connection.ShopId, product.Id), out var plan);
                var latest = sync.Where(x => x.Channel.Equals(connection.Channel, StringComparison.OrdinalIgnoreCase) && (x.EntityId == product.Id || (plan is not null && x.EntityId == plan.ListingId))).OrderByDescending(x => x.UpdatedUtc).FirstOrDefault();
                var status = string.IsNullOrWhiteSpace(plan?.ListingId) ? "MISSING" : plan.UpdatedUtc < now.AddDays(-180) ? "STALE" : latest?.Status == Catalog.SyncStatus.Failed ? "ERROR" : latest?.Status is Catalog.SyncStatus.Pending or Catalog.SyncStatus.Running ? "PENDING" : latest?.Status == Catalog.SyncStatus.Succeeded ? "SYNCED" : "DRAFT";
                var auth = connection.Status is "FAILED" or "LIVE_API_BLOCKED" or "NOT_CONFIGURED" ? "AUTH_ERROR" : connection.Status;
                var capabilities = definition.Capabilities.Enabled.Count == 0 ? LocalOnlyCapabilities : string.Join(", ", definition.Capabilities.Enabled.Select(x => x.ToString()));
                result.Add(new(product.Id, product.Sku, product.Name, connection.Channel, definition.Name, connection.ShopId, status, plan?.ListingId ?? "", latest?.Status.ToString() ?? "None", latest?.LastError ?? "", latest?.UpdatedUtc, auth, capabilities));
            }
        }
        return result;
    }

    /// <summary>#843: the capabilities text a row carries when its channel has no live operation in this build -- the one real "unsupported" fact the matrix can state.</summary>
    public const string LocalOnlyCapabilities = "Yerel plan";
    public static bool IsLocalOnly(string? capabilities) => string.Equals((capabilities ?? "").Trim(), LocalOnlyCapabilities, StringComparison.Ordinal);

    public static IReadOnlyList<ChannelListingMatrixRow> Filter(IEnumerable<ChannelListingMatrixRow> rows, string query, string status, string channel, string shop)
    {
        return rows.Where(x => string.IsNullOrWhiteSpace(query) || x.SearchText.ContainsFolded(query.Trim())).Where(x => status == "Tümü" || (status == "AUTH_ERROR" ? x.AuthStatus == "AUTH_ERROR" : x.MappingStatus.Equals(status, StringComparison.OrdinalIgnoreCase))).Where(x => string.IsNullOrWhiteSpace(channel) || x.Channel.Contains(channel.Trim(), StringComparison.OrdinalIgnoreCase) || x.ChannelName.ContainsFolded(channel.Trim())).Where(x => string.IsNullOrWhiteSpace(shop) || x.ShopId.ContainsFolded(shop.Trim())).OrderBy(x => x.ProductName).ThenBy(x => x.ChannelName).ThenBy(x => x.ShopId).ToList();
    }
}
