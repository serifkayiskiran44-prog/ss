namespace TrMarketplaceHubDesktop;

public sealed record GaAcceptanceInput(bool PriorEvidenceVerified, bool ArtifactVerified, bool ReleaseTestsPassed, bool SelfContainedPublished, IReadOnlyList<SecurityFinding> Findings);
public sealed record GaAcceptanceResult(string Status, IReadOnlyList<string> Blockers)
{
    public bool IsReady => Status == "V1_READY";
}

public static class GaAcceptance
{
    public static GaAcceptanceResult Evaluate(GaAcceptanceInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var blockers = new List<string>();
        if (!input.PriorEvidenceVerified) blockers.Add("prior issue kanıtı doğrulanmadı");
        if (!input.ArtifactVerified) blockers.Add("release artifact doğrulanmadı");
        if (!input.ReleaseTestsPassed) blockers.Add("Release testleri geçmedi");
        if (!input.SelfContainedPublished) blockers.Add("self-contained publish eksik");
        blockers.AddRange(input.Findings.Where(x => x.Severity is "P0" or "P1").Select(x => x.Id + ": " + AuditStore.Sanitize(x.Detail)));
        return new(blockers.Count == 0 ? "V1_READY" : "NOT_READY", blockers);
    }
}
