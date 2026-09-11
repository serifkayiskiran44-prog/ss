namespace TrMarketplaceHubDesktop;

public sealed record MarketplaceCapabilityAuditRow(
    string Channel,
    string Name,
    IReadOnlySet<MarketplaceOperation> Capabilities,
    bool LiveApiBlocked,
    string Decision,
    string DocumentationUrl);

public sealed record MarketplaceCapabilityAuditResult(
    IReadOnlyList<MarketplaceCapabilityAuditRow> Rows,
    IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Validates the explicit capability catalog without probing live marketplaces.</summary>
public static class MarketplaceCapabilityAudit
{
    public static MarketplaceCapabilityAuditResult Run()
    {
        var errors = new List<string>();
        var rows = MarketplaceConnectionCatalog.All.Select(definition =>
        {
            if (string.IsNullOrWhiteSpace(definition.DocumentationUrl) || !Uri.TryCreate(definition.DocumentationUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                errors.Add($"{definition.Id}: resmi HTTPS dokümantasyon referansı yok.");
            if (definition.LiveApiBlocked && definition.Capabilities.Enabled.Count != 0)
                errors.Add($"{definition.Id}: LIVE_API_BLOCKED kanalda canlı capability işaretlenmiş.");
            return new MarketplaceCapabilityAuditRow(
                definition.Id,
                definition.Name,
                definition.Capabilities.Enabled,
                definition.LiveApiBlocked,
                definition.LiveApiBlocked ? "LIVE_API_BLOCKED" : "CATALOG_DECLARED",
                definition.DocumentationUrl);
        }).ToArray();
        var duplicateIds = rows.GroupBy(x => x.Channel, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1).Select(x => x.Key);
        foreach (var duplicate in duplicateIds) errors.Add($"{duplicate}: duplicate channel kaydı.");
        return new(rows, errors);
    }
}
