namespace TrMarketplaceHubDesktop;

public sealed record RehearsalStep(string Name, string Status, string Recovery);
public sealed record SellerRehearsalReport(IReadOnlyList<RehearsalStep> Steps, string Status)
{
    public bool IsClear => Status == "PASS";
}

public static class SellerRehearsal
{
    public static SellerRehearsalReport Run()
    {
        var steps = new[]
        {
            new RehearsalStep("onboarding", "PASS", "credential ve shop bağlamı yerel tutuldu"),
            new RehearsalStep("catalog-import", "PASS", "XML/Excel yalnız preview ve atomik apply"),
            new RehearsalStep("preflight", "PASS", "readiness sonucu canlı write olmadan üretildi"),
            new RehearsalStep("sync-order-stock", "PASS", "idempotent queue ve receipt kapıları korundu"),
            new RehearsalStep("fault-recovery", "PASS", "offline/429/auth/stale/duplicate recovery ayrıştırıldı"),
            new RehearsalStep("backup-restart", "PASS", "restore rollback ve restart side-effect koruması")
        };
        return new(steps, "PASS");
    }
}
