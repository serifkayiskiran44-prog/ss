namespace TrMarketplaceHubDesktop;

public sealed record EtsyInventoryWriteDecision(string Status, string Detail, int PropertyCount);

public static class EtsyInventoryDeferredGuard
{
    public const string Status = "DEFERRED_BY_USER";
    public static EtsyInventoryWriteDecision Evaluate(int propertyCount, bool explicitApproval)
    {
        if (propertyCount < 0) throw new ArgumentException("Property sayısı negatif olamaz.");
        return new(Status, explicitApproval ? "Varyant inventory write kullanıcı tarafından ertelendi; HTTP isteği oluşturulmadı." : "Varyant inventory write kapsam dışı; HTTP isteği oluşturulmadı.", propertyCount);
    }
    public static void EnsureNoWrite(EtsyInventoryWriteDecision decision) => throw new InvalidOperationException($"{decision.Status}: Etsy inventory varyant write kapsam dışı; HTTP isteği oluşturulmadı.");
}
