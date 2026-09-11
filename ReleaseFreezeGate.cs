namespace TrMarketplaceHubDesktop;

public sealed record ReleaseFreezeResult(string Status, IReadOnlyList<string> Blockers);

/// <summary>Final local release decision; deferred and LIVE_API_BLOCKED states are not invented as P0/P1 failures.</summary>
public static class ReleaseFreezeGate
{
    public static ReleaseFreezeResult Evaluate(IReadOnlyList<ProductionReadinessCheck> checks, bool artifactVerified)
    {
        var blockers = checks
            .Where(check => check.Status is "ERROR" or "BLOCKED" && IsP0P1(check.Detail))
            .Select(check => $"{check.Key}: {AuditStore.Sanitize(check.Detail)}")
            .ToList();
        if (!artifactVerified) blockers.Add("release-artifact: doğrulanmış self-contained artifact yok.");
        return blockers.Count == 0 ? new("V1_READY", blockers) : new("FROZEN", blockers);
    }

    static bool IsP0P1(string detail) => detail.Contains("P0", StringComparison.OrdinalIgnoreCase) || detail.Contains("P1", StringComparison.OrdinalIgnoreCase);
}
