namespace TrMarketplaceHubDesktop;

public sealed record FulfillmentCoreDecision(string Status, string Detail, string OrderId);

public static class FulfillmentCoreDeferredGuard
{
    public const string Status = "DEFERRED_BY_USER";
    public static FulfillmentCoreDecision Evaluate(string orderId)
    {
        if (string.IsNullOrWhiteSpace(orderId)) throw new ArgumentException("Sipariş kimliği zorunlu.", nameof(orderId));
        return new(Status, "Fulfillment Core kullanıcı tarafından ertelendi; provider routing/request write başlatılmadı.", orderId.Trim());
    }
    public static void EnsureNoRequest(FulfillmentCoreDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        throw new InvalidOperationException($"{decision.Status}: fulfillment operasyonu kapsam dışı; request başlatılmadı.");
    }
}
