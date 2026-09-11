namespace TrMarketplaceHubDesktop;

public sealed record StoreOnboardingState(string Channel, string ShopId, string Stage, string Status, string Detail)
{
    public bool CanResume => Status is "IN_PROGRESS" or "BLOCKED";
}

public static class StoreOnboardingLifecycle
{
    public static StoreOnboardingState Begin(string channel, string shopId)
    {
        if (string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(shopId)) throw new ArgumentException("Kanal ve mağaza kimliği gerekli.");
        _ = MarketplaceConnectionCatalog.Get(channel);
        return new(channel.Trim().ToLowerInvariant(), shopId.Trim(), "CONNECTION_TEST", "IN_PROGRESS", "Credential bağlamı ve read-only bağlantı testi bekleniyor.");
    }

    public static StoreOnboardingState CompleteReadOnlyTest(StoreOnboardingState state, bool success, string detail)
    {
        ArgumentNullException.ThrowIfNull(state);
        var safe = AuditStore.Sanitize(detail ?? "");
        return success ? state with { Stage = "CAPABILITY_DISCOVERY", Status = "IN_PROGRESS", Detail = "Read-only bağlantı testi geçti; capability keşfi bekleniyor." }
            : state with { Status = safe.Contains("LIVE_API_BLOCKED", StringComparison.OrdinalIgnoreCase) ? "BLOCKED" : "FAILED", Detail = safe };
    }
}
