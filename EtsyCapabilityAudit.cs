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
        new EtsyCapability("images-write", "WRITE", "POST", "/v3/application/shops/{shop_id}/listings/{listing_id}/images", "listings_w", "BLOCKED", "No automatic media write in this release"),
        // Verified against https://developers.etsy.com/documentation/essentials/webhooks/ (fetched 2026-09-12): only
        // order.paid/canceled/shipped/delivered exist; more events are explicitly "coming soon", and subscription is
        // configured through Etsy's Webhook Portal UI, not a documented REST endpoint.
        new EtsyCapability("webhooks-order-lifecycle", "WEBHOOK", "N/A", "Webhook Portal UI (order.paid, order.canceled, order.shipped, order.delivered only)", "n/a (portal-configured)", "PARTIAL", "Etsy Open API v3 Webhooks essentials page, fetched 2026-09-12"),
        // Verified: Etsy Open API v3 has no conversations/messages endpoint (community discussion etsy/open-api#1547
        // requests it as a missing feature; no such endpoint exists in the official reference).
        new EtsyCapability("conversations-message", "MESSAGE", "N/A", "N/A (no endpoint exists)", "n/a", "UNSUPPORTED", "etsy/open-api discussion #1547 and Etsy Open API v3 reference, fetched 2026-09-12"),
        // Verified against https://developer.etsy.com/documentation/tutorials/payments (fetched 2026-09-12): "The Open
        // API v3 endpoints for payments and the shop ledger are read-only operations ... refunds are not automatic."
        new EtsyCapability("receipt-refund", "RETURN", "N/A", "N/A (no refund-creation endpoint exists)", "n/a", "UNSUPPORTED", "Etsy Open API v3 Payments tutorial, fetched 2026-09-12")
    };
    public static IReadOnlyList<EtsyCapability> MissingOrBlocked() => OfficialManifest.Where(x => x.Status is "BLOCKED" or "PREVIEW_ONLY" or "UNSUPPORTED").ToArray();
    public static bool UsesDedicatedInventoryPath(string path) => path.Contains("/listings/", StringComparison.Ordinal) && path.EndsWith("/inventory", StringComparison.Ordinal);
}
