namespace TrMarketplaceHubDesktop;

public sealed record EtsyOrderEvent(string ReceiptId, string TransactionId, string ShopId, string? Sku, long? ListingId, string RawStatus, int Quantity, string EventType, DateTimeOffset AtUtc);
public sealed record EtsyOrderNormalized(string ReceiptId, string TransactionId, string ShopId, string? Sku, long? ListingId, string Status, string EventType, DateTimeOffset AtUtc);
public sealed record EtsyOrderSyncState(string ShopId, DateTimeOffset? UpdatedAfter, string Cursor, int Accepted, int Duplicates);
public sealed record EtsyRefundStockPreview(string ShopId, string ReceiptId, string? Sku, int Quantity, string Key, bool Approved);

public sealed class EtsyOrderCompletion
{
    private readonly object gate = new(); private readonly HashSet<string> keys = new(StringComparer.Ordinal); private readonly List<EtsyOrderNormalized> events = new(); private readonly List<string> unknowns = new();
    public IReadOnlyList<EtsyOrderNormalized> Events => events.ToArray(); public IReadOnlyList<string> UnknownItems => unknowns.ToArray();
    public EtsyOrderSyncState Import(string shopId, IEnumerable<EtsyOrderEvent> input, EtsyOrderSyncState state)
    { if (shopId != state.ShopId) throw new InvalidOperationException("WRONG_SHOP"); var accepted = 0; var duplicates = 0; lock (gate) foreach (var e in input.OrderBy(x => x.AtUtc)) { if (e.ShopId != shopId || e.Quantity < 0) throw new InvalidOperationException("ORDER_SCOPE_INVALID"); var key = e.ReceiptId + "|" + e.TransactionId + "|" + e.EventType; if (!keys.Add(key)) { duplicates++; continue; } if (string.IsNullOrWhiteSpace(e.Sku) || e.ListingId is null) unknowns.Add(key); events.Add(new(e.ReceiptId, e.TransactionId, e.ShopId, e.Sku, e.ListingId, Normalize(e.RawStatus), e.EventType, e.AtUtc)); accepted++; } var last = events.Where(x => x.ShopId == shopId).OrderByDescending(x => x.AtUtc).Select(x => (DateTimeOffset?)x.AtUtc).FirstOrDefault() ?? state.UpdatedAfter; return new(shopId, last, state.Cursor, state.Accepted + accepted, state.Duplicates + duplicates); }
    public EtsyRefundStockPreview PreviewRefund(string shopId, EtsyOrderNormalized order, int quantity) { if (order.ShopId != shopId || order.EventType is not ("refund" or "cancel") || quantity <= 0 || string.IsNullOrWhiteSpace(order.Sku)) throw new InvalidOperationException("REFUND_STOCK_PREVIEW_BLOCKED"); return new(shopId, order.ReceiptId, order.Sku, quantity, $"{shopId}|{order.ReceiptId}|{order.Sku}|{quantity}", false); }
    public static void EnsureApproved(EtsyRefundStockPreview preview, bool approved) { if (!approved) throw new InvalidOperationException("EXPLICIT_APPROVAL_REQUIRED"); }
    private static string Normalize(string status) => status.ToLowerInvariant() switch { "paid" or "payment" => "PAID", "shipped" => "SHIPPED", "cancelled" or "canceled" => "CANCELLED", "refunded" or "refund" => "REFUNDED", _ => "UNKNOWN" };
}
