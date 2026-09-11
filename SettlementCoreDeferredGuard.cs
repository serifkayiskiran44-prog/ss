namespace TrMarketplaceHubDesktop;

public sealed record SettlementCoreDecision(string Status, string Detail, string PeriodId);

public static class SettlementCoreDeferredGuard
{
    public const string Status = "DEFERRED_BY_USER";
    public static SettlementCoreDecision Evaluate(string periodId)
    {
        if (string.IsNullOrWhiteSpace(periodId)) throw new ArgumentException("Mutabakat dönemi zorunlu.", nameof(periodId));
        return new(Status, "Hakediş/mutabakat Core kullanıcı tarafından ertelendi; payment import ve muhasebe write başlatılmadı.", periodId.Trim());
    }
    public static void EnsureNoWrite(SettlementCoreDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        throw new InvalidOperationException($"{decision.Status}: settlement operasyonu kapsam dışı; write başlatılmadı.");
    }
}
