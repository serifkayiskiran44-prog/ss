namespace TrMarketplaceHubDesktop;

public sealed record ChannelListingMatrixRow(string ProductId, string Sku, string ProductName, string Channel, string ChannelName, string ShopId, string MappingStatus, string ListingId, string SyncStatus, string LastError, DateTime? LastSyncUtc, string AuthStatus, string Capabilities)
{
    public string SearchText => $"{Sku} {ProductName} {Channel} {ChannelName} {ShopId} {ListingId} {MappingStatus} {SyncStatus} {LastError}";
    public string EslemeDurumu => MarketplaceStatusText.ToTurkish(MappingStatus);
    public string EsitlemeDurumu => MarketplaceStatusText.ToTurkish(SyncStatus);
    public string BaglantiDurumu => MarketplaceStatusText.ToTurkish(AuthStatus);
    public string YapilacakIs => MarketplaceStatusText.Explain(AuthStatus, MappingStatus, ListingId, LastError);
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
        // Indexed once in O(n) instead of re-filtering+re-sorting the whole sync
        // list for every (connection,product) row - same latest-sync semantics
        // (exact Channel+ShopId+EntityId match, newest UpdatedUtc wins, ties
        // broken by original list order to match the prior stable
        // OrderByDescending().FirstOrDefault() behavior exactly). See #2596.
        var latestByKey = new Dictionary<(string Channel, string ShopId, string EntityId), Catalog.SyncJob>();
        foreach (var job in sync)
        {
            var key = (job.Channel.ToLowerInvariant(), job.ShopId, job.EntityId);
            if (!latestByKey.TryGetValue(key, out var current) || job.UpdatedUtc > current.UpdatedUtc) latestByKey[key] = job;
        }
        Catalog.SyncJob? LatestFor(string channel, string shopId, string entityId) => latestByKey.TryGetValue((channel.ToLowerInvariant(), shopId, entityId), out var found) ? found : null;
        var now = DateTime.UtcNow;
        var result = new List<ChannelListingMatrixRow>(catalog.Count * Math.Max(1, connections.Count));
        foreach (var connection in connections)
        {
            var definition = MarketplaceConnectionCatalog.Get(connection.Channel);
            foreach (var product in catalog)
            {
                plans.TryGetValue((connection.Channel, connection.ShopId, product.Id), out var plan);
                var byProduct = LatestFor(connection.Channel, connection.ShopId, product.Id);
                var byListing = !string.IsNullOrEmpty(plan?.ListingId) ? LatestFor(connection.Channel, connection.ShopId, plan.ListingId) : null;
                // A row can match sync history under two different entity keys
                // (the catalog product id, or the plan's own listing id) - pick
                // whichever is newer; on the rare exact-timestamp tie, favor the
                // product-id-keyed entry deterministically.
                Catalog.SyncJob? latest = (byProduct, byListing) switch
                {
                    (null, null) => null,
                    (var a, null) => a,
                    (null, var b) => b,
                    var (a, b) => a!.UpdatedUtc >= b!.UpdatedUtc ? a : b,
                };
                var status = string.IsNullOrWhiteSpace(plan?.ListingId) ? "MISSING" : plan.UpdatedUtc < now.AddDays(-180) ? "STALE" : latest?.Status == Catalog.SyncStatus.Failed ? "ERROR" : latest?.Status is Catalog.SyncStatus.Pending or Catalog.SyncStatus.Running ? "PENDING" : latest?.Status == Catalog.SyncStatus.Succeeded ? "SYNCED" : "DRAFT";
                var auth = connection.Status is "FAILED" or "LIVE_API_BLOCKED" or "NOT_CONFIGURED" ? "AUTH_ERROR" : connection.Status;
                var capabilities = definition.Capabilities.Enabled.Count == 0 ? "Yerel plan" : string.Join(", ", definition.Capabilities.Enabled.Select(x => x.ToString()));
                result.Add(new(product.Id, product.Sku, product.Name, connection.Channel, definition.Name, connection.ShopId, status, plan?.ListingId ?? "", latest?.Status.ToString() ?? "None", latest?.LastError ?? "", latest?.UpdatedUtc, auth, capabilities));
            }
        }
        return result;
    }

    public static IReadOnlyList<ChannelListingMatrixRow> Filter(IEnumerable<ChannelListingMatrixRow> rows, string query, string status, string channel, string shop)
    {
        return rows.Where(x => string.IsNullOrWhiteSpace(query) || x.SearchText.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase)).Where(x => status == "Tümü" || (status == "AUTH_ERROR" ? x.AuthStatus == "AUTH_ERROR" : x.MappingStatus.Equals(status, StringComparison.OrdinalIgnoreCase))).Where(x => string.IsNullOrWhiteSpace(channel) || x.Channel.Contains(channel.Trim(), StringComparison.OrdinalIgnoreCase) || x.ChannelName.Contains(channel.Trim(), StringComparison.CurrentCultureIgnoreCase)).Where(x => string.IsNullOrWhiteSpace(shop) || x.ShopId.Contains(shop.Trim(), StringComparison.CurrentCultureIgnoreCase)).OrderBy(x => x.ProductName).ThenBy(x => x.ChannelName).ThenBy(x => x.ShopId).ToList();
    }
}
