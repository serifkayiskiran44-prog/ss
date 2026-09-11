namespace TrMarketplaceHubDesktop;

public sealed record ProductWorkspaceAcceptance(string Status, IReadOnlyList<string> Blockers);

public static class ProductWorkspaceAcceptanceGate
{
    public static ProductWorkspaceAcceptance Evaluate(bool screenshotEvidenceAvailable, bool uiSmokePassed)
    {
        var blockers = new List<string>();
        if (!screenshotEvidenceAvailable) blockers.Add("P0_SCREENSHOT_EVIDENCE_MISSING");
        if (!uiSmokePassed) blockers.Add("P0_UI_SMOKE_NOT_VERIFIED");
        if (EntegraParityGapMatrix.Blocked().Any(x => x.Family is "variants" or "bundles" or "critical-price")) blockers.Add("DEFERRED_BY_USER_DOMAIN_GAPS");
        return new(blockers.Count == 0 ? "ACCEPTED" : "BLOCKED", blockers);
    }
}
