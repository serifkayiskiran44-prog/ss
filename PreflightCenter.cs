namespace TrMarketplaceHubDesktop;

public sealed record PreflightItem(string Key, string Status, string Detail);
public sealed record PreflightResult(string Status, IReadOnlyList<PreflightItem> Items)
{
    public bool IsReady => Status == "CODEX_READY";
    public IReadOnlyList<PreflightItem> BlockingItems => Items.Where(x => x.Status is "BLOCKED" or "ERROR").ToArray();
}

/// <summary>Combines local readiness evidence without contacting or mutating a marketplace.</summary>
public static class PreflightCenter
{
    public static PreflightResult Evaluate(IEnumerable<PreflightItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var materialized = items.Where(x => x is not null).ToArray();
        var status = materialized.Any(x => x.Status is "BLOCKED" or "ERROR") ? "BLOCKED"
            : materialized.Any(x => x.Status == "WARN") ? "PARTIAL" : "CODEX_READY";
        return new(status, materialized);
    }

    public static PreflightResult FromEtsy(EtsyReadinessReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return Evaluate(report.Checks.Select(x => new PreflightItem(x.Key, x.Status, x.Detail)));
    }
}
