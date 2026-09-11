namespace TrMarketplaceHubDesktop;

public sealed record StockSafetyPolicy(string Channel, string ShopId, int SafetyStock, int MaxDisplayed, int? TextStockWhenAvailable = null, int? TextStockWhenUnavailable = null);
public sealed record StockSafetyPreview(string Channel, string ShopId, string ProductId, int SupplierStock, int SellableStock, int SafetyStock, int MaxDisplayed, bool Stale, string Status, string Formula);

public static class DropshipStockSafety
{
    public static StockSafetyPreview Preview(string productId, int supplierStock, bool? textualAvailability, StockSafetyPolicy policy, DateTimeOffset capturedUtc, DateTimeOffset now, TimeSpan staleAfter)
    {
        if (string.IsNullOrWhiteSpace(productId) || supplierStock < 0 || policy.SafetyStock < 0 || policy.MaxDisplayed < 0) throw new ArgumentException("Stok güvenliği girdileri geçersiz.");
        var stale = now - capturedUtc > staleAfter; var available = textualAvailability ?? supplierStock > 0; var baseStock = available ? supplierStock : 0; var sellable = Math.Max(0, Math.Min(policy.MaxDisplayed, baseStock - policy.SafetyStock));
        if (policy.TextStockWhenAvailable is { } yes && textualAvailability == true) sellable = Math.Min(policy.MaxDisplayed, yes);
        if (policy.TextStockWhenUnavailable is { } no && textualAvailability == false) sellable = Math.Min(policy.MaxDisplayed, no);
        return new(policy.Channel, policy.ShopId, productId, supplierStock, stale ? 0 : sellable, policy.SafetyStock, policy.MaxDisplayed, stale, stale ? "BLOCKED_STALE" : "PREVIEW", $"max(0,min({policy.MaxDisplayed},{baseStock}-{policy.SafetyStock}))");
    }
    public static void EnsureScope(StockSafetyPreview preview, StockSafetyPolicy policy) { if (preview.Channel != policy.Channel || preview.ShopId != policy.ShopId) throw new InvalidOperationException("WRONG_SHOP_OR_CHANNEL"); }
    public static IReadOnlyList<StockSafetyPreview> Bulk(IEnumerable<StockSafetyPreview> previews, string channel, string shopId) => previews.Where(x => x.Channel == channel && x.ShopId == shopId).ToArray();
}
