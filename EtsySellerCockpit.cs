namespace TrMarketplaceHubDesktop;

public sealed record EtsyCockpitSnapshot(string ShopId, string AuthStatus, EtsyScopeReadiness Scopes, int ListingCount, int OrderCount, string SyncHealth, int DriftCount, int DeadLetterCount, bool DryRun, DateTimeOffset AtUtc);
public sealed record EtsyCockpitReadiness(string Status, IReadOnlyList<string> Missing, string Detail);

public static class EtsySellerCockpit
{
    public static EtsyCockpitReadiness Evaluate(EtsyCockpitSnapshot snapshot)
    { var missing = new List<string>(); if (snapshot.AuthStatus != "PASS") missing.Add("auth"); if (snapshot.Scopes.Status != "READY") missing.Add("scopes"); if (snapshot.SyncHealth is "FAILED" or "BLOCKED") missing.Add("sync"); if (snapshot.DriftCount > 0) missing.Add("drift"); if (snapshot.DeadLetterCount > 0) missing.Add("dead-letter"); return new(missing.Count == 0 ? "READY_READ_ONLY" : "ATTENTION", missing, snapshot.DryRun ? "Dry-run/read-only cockpit; write ayrı onay kapısı." : "Read-only cockpit; write ayrı onay kapısı."); }
    public static void EnsureReadOnly(EtsyCockpitSnapshot snapshot) { if (!snapshot.DryRun) throw new InvalidOperationException("LIVE_READ_REQUIRES_EXPLICIT_OPERATOR_RUN"); }
}
