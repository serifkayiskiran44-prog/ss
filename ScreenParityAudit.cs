namespace TrMarketplaceHubDesktop;

public sealed record ScreenParityAuditResult(string Status, IReadOnlyList<string> MissingRoutes)
{
    public bool IsComplete => Status == "WORKING";
}

/// <summary>Validates the local navigation contract without creating network or marketplace side effects.</summary>
public static class ScreenParityAudit
{
    public static IReadOnlySet<string> RequiredRoutes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "dashboard", "products", "bulk-products", "categories", "brands", "taxonomy", "media", "xml", "excel",
        "orders", "order-exceptions", "policy-center", "sync", "automation", "marketplaces", "listing-matrix",
        "bizimhesap", "nes", "data-quality", "api-health", "messages", "reports", "readiness", "connections",
        "settings", "locale", "backup", "migration"
    };

    public static ScreenParityAuditResult Evaluate(IEnumerable<string> registeredRoutes)
    {
        ArgumentNullException.ThrowIfNull(registeredRoutes);
        var registered = registeredRoutes.Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = RequiredRoutes.Where(route => !registered.Contains(route)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        return new(missing.Length == 0 ? "WORKING" : "BLOCKED", missing);
    }
}
