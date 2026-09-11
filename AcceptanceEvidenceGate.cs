namespace TrMarketplaceHubDesktop;

public sealed record AcceptanceEvidence(string Issue, string HeadSha, IReadOnlyList<string> Files, int TestsPassed, int TestsTotal, string PublishPath);

public static class AcceptanceEvidenceGate
{
    public static string Evaluate(AcceptanceEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (string.IsNullOrWhiteSpace(evidence.Issue) || string.IsNullOrWhiteSpace(evidence.HeadSha) || evidence.TestsTotal <= 0 || evidence.TestsPassed != evidence.TestsTotal || string.IsNullOrWhiteSpace(evidence.PublishPath)) return "BLOCKED";
        return "ACCEPTED";
    }
}
