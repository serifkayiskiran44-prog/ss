namespace TrMarketplaceHubDesktop;

public sealed record CriticalPriceDecision(string Status, string Detail, string ProductId);

public static class CriticalPriceDeferredGuard
{
    public const string Status = "DEFERRED_BY_USER";
    public static CriticalPriceDecision Evaluate(string productId)
    {
        if (string.IsNullOrWhiteSpace(productId)) throw new ArgumentException("Ürün kimliği zorunlu.", nameof(productId));
        return new(Status, "Kritik/akıllı/tarih bazlı fiyat politikaları kullanıcı tarafından ertelendi; scheduled price write başlatılmadı.", productId.Trim());
    }
    public static void EnsureNoWrite(CriticalPriceDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        throw new InvalidOperationException($"{decision.Status}: kritik fiyat kapsam dışı; write başlatılmadı.");
    }
}
