namespace TrMarketplaceHubDesktop;

public sealed record MarketplaceProductPanelRow(string Channel, string ShopId, string Status, string MappingId, string Capabilities, string Readiness, string LastError);

/// <summary>Builds marketplace product panels from shared connection, mapping, capability and health stores.</summary>
public static class MarketplaceProductPanelModel
{
    public static IReadOnlyList<MarketplaceProductPanelRow> Build(string productId, string? directory = null)
    {
        if (string.IsNullOrWhiteSpace(productId)) throw new ArgumentException("Ürün kimliği zorunlu.", nameof(productId));
        var mappings = new MarketplaceMappingStore(directory);
        var health = new ApiHealthStore(directory);
        return new MarketplaceConnectionStore(directory).List().Select(connection =>
        {
            var definition = MarketplaceConnectionCatalog.Get(connection.Channel);
            var mapping = mappings.Find(connection.Channel, connection.ShopId, productId);
            var api = health.Get(connection.Channel, connection.ShopId);
            var blocked = definition.LiveApiBlocked;
            var status = blocked ? "LIVE_API_BLOCKED" : !connection.Enabled ? "NOT_SUPPORTED" : api?.State is "AUTH_ERROR" or "NETWORK_ERROR" or "TIMEOUT" ? "PARTIAL" : "WORKING";
            var capabilities = definition.Capabilities.Enabled.Count == 0 ? "NOT_SUPPORTED" : string.Join(", ", definition.Capabilities.Enabled.OrderBy(x => x.ToString()));
            var readiness = blocked ? "LIVE_API_BLOCKED" : mapping is null ? "MAPPING_REQUIRED" : connection.Status == "CONNECTED_READ_ONLY" ? "READY_READ_ONLY" : "CREDENTIAL_TEST_REQUIRED";
            return new MarketplaceProductPanelRow(connection.Channel, connection.ShopId, status, mapping?.ExternalId ?? "", capabilities, readiness, api?.LastError ?? connection.LastError);
        }).ToArray();
    }
}
