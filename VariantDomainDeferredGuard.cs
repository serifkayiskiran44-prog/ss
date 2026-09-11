namespace TrMarketplaceHubDesktop;

public sealed record VariantDomainDecision(string Status, string Detail, string RequestedOperation);

public static class VariantDomainDeferredGuard
{
    public const string Status = "DEFERRED_BY_USER";

    public static VariantDomainDecision Evaluate(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("Operasyon adı boş olamaz.", nameof(operation));

        return new(Status, "Ortak varyant/seçenek domain çekirdeği kullanıcı tarafından ertelendi; kalıcı model veya write başlatılmadı.", operation.Trim());
    }

    public static void EnsureNoWrite(VariantDomainDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        throw new InvalidOperationException($"{decision.Status}: varyant domain operasyonu kapsam dışı; write başlatılmadı.");
    }
}
