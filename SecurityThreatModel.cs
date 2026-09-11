namespace TrMarketplaceHubDesktop;

public sealed record SecurityFinding(string Id, string Severity, string Area, string Detail);
public sealed record SecurityAssessment(string Status, IReadOnlyList<SecurityFinding> Findings)
{
    public bool HasReleaseBlocker => Findings.Any(x => x.Severity is "P0" or "P1");
}

public static class SecurityThreatModel
{
    public static SecurityAssessment Assess(IEnumerable<SecurityFinding> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        var rows = findings.Where(x => x is not null).Select(x => x with { Detail = AuditStore.Sanitize(x.Detail) }).ToArray();
        return new(rows.Any(x => x.Severity is "P0" or "P1") ? "BLOCKED" : rows.Any(x => x.Severity == "P2") ? "CONDITIONAL" : "CLEAR", rows);
    }
}
