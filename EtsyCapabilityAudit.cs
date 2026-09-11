namespace TrMarketplaceHubDesktop;

public sealed record EtsyCapability(string Name, string Operation, string Method, string PathTemplate, string Scope, string Status, string Evidence);

public static class EtsyCapabilityAudit
{
    public static IReadOnlyList<EtsyCapability> OfficialManifest { get; } = new[]
    {
        new EtsyCapability("listing-read", "READ", "GET", "/v3/application/shops/{shop_id}/listings", "listings_r", "VERIFIED", "Etsy Open API v3 request/listings reference"),
        new EtsyCapability("listing-inventory-read", "READ", "GET", "/v3/application/listings/{listing_id}/inventory", "listings_r", "VERIFIED", "Etsy inventory and shipping migration/tutorial"),
        new EtsyCapability("listing-update", "WRITE", "PATCH", "/v3/application/shops/{shop_id}/listings/{listing_id}", "listings_w", "PREVIEW_ONLY", "Official listing tutorial; explicit approval required"),
        new EtsyCapability("orders-read", "READ", "GET", "/v3/application/shops/{shop_id}/receipts", "transactions_r", "VERIFIED", "Etsy Open API v3 official reference"),
        new EtsyCapability("images-write", "WRITE", "POST", "/v3/application/shops/{shop_id}/listings/{listing_id}/images", "listings_w", "BLOCKED", "No automatic media write in this release")
    };
    public static IReadOnlyList<EtsyCapability> MissingOrBlocked() => OfficialManifest.Where(x => x.Status is "BLOCKED" or "PREVIEW_ONLY").ToArray();
    public static bool UsesDedicatedInventoryPath(string path) => path.Contains("/listings/", StringComparison.Ordinal) && path.EndsWith("/inventory", StringComparison.Ordinal);
}
