namespace TrMarketplaceHubDesktop;

public sealed record XmlVariantMappingDecision(string Status, string Detail, string SourceId);

public static class XmlVariantMappingDeferredGuard
{
    public const string Status = "DEFERRED_BY_USER";

    public static XmlVariantMappingDecision Evaluate(string sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("XML kaynak kimliği zorunlu.", nameof(sourceId));
        return new(Status, "XML varyant mapping kullanıcı tarafından ertelendi; normal XML akışı korunarak variant import başlatılmadı.", sourceId.Trim());
    }

    public static void EnsureNoWrite(XmlVariantMappingDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        throw new InvalidOperationException($"{decision.Status}: XML varyant mapping kapsam dışı; write başlatılmadı.");
    }
}
