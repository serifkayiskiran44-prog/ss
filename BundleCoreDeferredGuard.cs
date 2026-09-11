namespace TrMarketplaceHubDesktop;

public sealed record BundleCoreDecision(string Status, string Detail, string BundleSku);

public static class BundleCoreDeferredGuard
{
    public const string Status = "DEFERRED_BY_USER";
    public static BundleCoreDecision Evaluate(string bundleSku)
    {
        if (string.IsNullOrWhiteSpace(bundleSku)) throw new ArgumentException("Bundle SKU zorunlu.", nameof(bundleSku));
        return new(Status, "Paket/set/bundle çekirdeği kullanıcı tarafından ertelendi; stok hareketi ve order write başlatılmadı.", bundleSku.Trim());
    }
    public static void EnsureNoWrite(BundleCoreDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        throw new InvalidOperationException($"{decision.Status}: bundle operasyonu kapsam dışı; write başlatılmadı.");
    }
}
