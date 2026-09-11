namespace TrMarketplaceHubDesktop;

public sealed record EntegraParityFamily(string Family, string Status, string Evidence, string NextStep);

public static class EntegraParityGapMatrix
{
    public static IReadOnlyList<EntegraParityFamily> Current { get; } =
    [
        new("catalog-orders", "PARTIAL", "Local catalog/order workflows and tests exist.", "Expand UI smoke coverage."),
        new("xml-excel-import", "PARTIAL", "Safe import/read paths exist.", "Keep unverified variant mapping deferred."),
        new("variants", "BLOCKED", "DEFERRED_BY_USER", "Require explicit scope change."),
        new("bundles", "BLOCKED", "DEFERRED_BY_USER", "Require explicit scope change."),
        new("critical-price", "BLOCKED", "DEFERRED_BY_USER", "Require explicit scope change."),
        new("fulfillment-settlement", "BLOCKED", "DEFERRED_BY_USER", "Require explicit scope change."),
        new("marketplace-write", "BLOCKED", "LIVE_API_BLOCKED", "Official capability, preview and explicit approval required.")
    ];

    public static IReadOnlyList<EntegraParityFamily> Blocked() => Current.Where(x => x.Status == "BLOCKED").ToArray();
}
