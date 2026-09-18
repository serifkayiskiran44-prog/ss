namespace TrMarketplaceHubDesktop;

/// <summary>
/// Defines which connection rows may enter an account-scoped operational UI.
/// Seeded channel defaults remain available to settings and history, but do not
/// become accounts until they carry verified connection evidence.
/// </summary>
public static class MarketplaceOperationalAccounts
{
    public static IReadOnlyList<MarketplaceConnection> List(MarketplaceConnectionStore store, string? channel = null) =>
        store.List(false)
            .Where(connection =>
                (channel is null || connection.Channel.Equals(channel, StringComparison.OrdinalIgnoreCase)) &&
                IsEligible(connection, store))
            .ToArray();

    public static bool IsEligible(MarketplaceConnection connection, MarketplaceConnectionStore store)
    {
        if (!connection.Enabled) return false;
        if (!IsSeededDefaultIdentity(connection)) return true;
        if (HasVerifiedStatus(connection)) return true;

        var marker = store.CredentialMigration(connection.Channel);
        return marker is not null &&
            marker.ConnectionId.Equals(connection.Id, StringComparison.Ordinal) &&
            marker.ShopId.Equals(connection.ShopId, StringComparison.Ordinal);
    }

    static bool IsSeededDefaultIdentity(MarketplaceConnection connection) =>
        connection.Id.Equals(connection.Channel + ":default", StringComparison.OrdinalIgnoreCase) &&
        connection.ShopId.Equals("default", StringComparison.OrdinalIgnoreCase);

    static bool HasVerifiedStatus(MarketplaceConnection connection) =>
        connection.LastTestUtc.HasValue &&
        (connection.Status.Equals("CONNECTED_READ_ONLY", StringComparison.OrdinalIgnoreCase) ||
         connection.Status.Equals("CONNECTED", StringComparison.OrdinalIgnoreCase));
}
