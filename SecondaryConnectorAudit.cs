namespace TrMarketplaceHubDesktop;

public sealed record SecondaryConnectorStatus(string Channel, string Status, string Detail);

/// <summary>Read-only status surface for secondary connectors; it never probes or writes a marketplace.</summary>
public static class SecondaryConnectorAudit
{
    static readonly string[] Channels = ["ozon", "allegro", "joom", "wish", "fruugo", "navlungo"];

    public static IReadOnlyList<SecondaryConnectorStatus> Snapshot()
        => Channels.Select(channel =>
        {
            var definition = MarketplaceConnectionCatalog.Get(channel);
            var status = definition.LiveApiBlocked ? "LIVE_API_BLOCKED" : definition.Capabilities.Enabled.Count == 0 ? "NOT_SUPPORTED" : "PARTIAL";
            var detail = definition.LiveApiBlocked
                ? "Resmi endpoint/scope sözleşmesi doğrulanmadı; HTTP isteği oluşturulmaz."
                : $"Catalog capability: {string.Join(", ", definition.Capabilities.Enabled.OrderBy(x => x.ToString()))}. Yazma kapsamı ayrıca onay kapısından geçer.";
            return new SecondaryConnectorStatus(definition.Id, status, detail);
        }).ToArray();
}
