using System.Globalization;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public enum OrderLineState { Cancelled, Pending, PartiallyShipped, Shipped, PartiallyReturned, Returned, OverReturned }

/// <summary>One order line with its four quantities and the state their difference means; the marker and word carry it without colour.</summary>
public sealed record OrderLineView(string Sku, string Title, int Ordered, int Shipped, int Returned, int Cancelled, OrderLineState State, string Marker, string Word, SeverityLevel Level, string Reason)
{
    /// <summary>What is still owed: never negative -- a cancelled or over-deducted line owes nothing.</summary>
    public string Outstanding => Math.Max(0, Ordered - Shipped - Cancelled).ToString(CultureInfo.CurrentCulture);
    public string StateLabel => $"{Marker} {Word}" + (Reason.Length > 0 ? " · " + Reason : "");
}

/// <summary>
/// Order line reconciliation (#838): for every line, ordered against shipped (what the stock receipt deducted --
/// the only per-SKU fulfilment the app records), returned (the #788 ledger) and cancelled (the whole order, when
/// its status says so). The differences are states, not colours: under-shipment ("▲ eksik sevk N"), a partial
/// return ("◐ kısmi iade"), a full return ("↩ iade edildi"), and the conflict that must never be silent -- more
/// came back than went out ("▼ fazla iade N"). Titles are product names, sanitized and capped; nothing here
/// reads a buyer or an address.
/// </summary>
public static class OrderLineReconciliation
{
    public const int TitleLength = 60;

    public static IReadOnlyList<OrderLineView> Build(OrderSnapshot order, OrderStockReceipt? receipt, IReadOnlyDictionary<string, int>? returnedBySku)
    {
        ArgumentNullException.ThrowIfNull(order);
        var raw = (order.RawStatus ?? "").Trim().ToLower(CultureInfo.GetCultureInfo("tr-TR"));
        var cancelled = raw.Contains("cancel", StringComparison.Ordinal) || raw.Contains("iptal", StringComparison.Ordinal) || raw.Contains("ıptal", StringComparison.Ordinal);
        var shippedBySku = (receipt?.Movements ?? new List<OrderStockMovement>()).GroupBy(m => m.Sku.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Sum(m => m.Quantity), StringComparer.OrdinalIgnoreCase);
        var returned = returnedBySku ?? new Dictionary<string, int>();
        var rows = new List<OrderLineView>();
        // Lines with the same SKU are one reconciliation unit -- the receipt and the ledger know SKUs, not lines.
        foreach (var group in order.Items.GroupBy(i => (i.Sku ?? "").Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var sku = group.Key; var ordered = group.Sum(i => Math.Max(0, i.Quantity));
            var title = Cap(AuditStore.Redact(group.First().Title ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim());
            var shipped = sku.Length > 0 && shippedBySku.TryGetValue(sku, out var s) ? s : 0;
            var came = sku.Length > 0 && returned.TryGetValue(sku, out var r) ? r : 0;
            var line = Classify(sku, title, ordered, shipped, came, cancelled);
            rows.Add(line);
        }
        return rows;
    }

    public static OrderLineView Classify(string sku, string title, int ordered, int shipped, int returned, bool orderCancelled)
    {
        var skuLabel = string.IsNullOrWhiteSpace(sku) ? "(SKU yok)" : sku;
        if (orderCancelled) return new(skuLabel, title, ordered, shipped, returned, ordered, OrderLineState.Cancelled, "⊘", "iptal", SeverityLevel.Info, shipped > 0 ? $"sevk edilmiş {N(shipped)} adet iptal sonrası açık" : "");
        if (returned > shipped) return new(skuLabel, title, ordered, shipped, returned, 0, OrderLineState.OverReturned, "▼", "fazla iade", SeverityLevel.Blocking, $"{N(returned - shipped)} adet sevk edilenden fazla geri geldi; kayıtları kontrol edin");
        if (returned > 0 && returned == shipped && shipped >= ordered) return new(skuLabel, title, ordered, shipped, returned, 0, OrderLineState.Returned, "↩", "iade edildi", SeverityLevel.Warning, "");
        if (returned > 0) return new(skuLabel, title, ordered, shipped, returned, 0, OrderLineState.PartiallyReturned, "◐", "kısmi iade", SeverityLevel.Warning, $"{N(returned)} / {N(shipped)} geri geldi" + (shipped < ordered ? $" · {N(ordered - shipped)} adet hiç sevk edilmedi" : ""));
        if (shipped >= ordered && ordered > 0) return new(skuLabel, title, ordered, shipped, returned, 0, OrderLineState.Shipped, "＝", "tam sevk", SeverityLevel.Success, shipped > ordered ? $"{N(shipped - ordered)} adet fazla düşüldü" : "");
        if (shipped > 0) return new(skuLabel, title, ordered, shipped, returned, 0, OrderLineState.PartiallyShipped, "▲", "eksik sevk", SeverityLevel.Warning, $"{N(ordered - shipped)} adet bekliyor");
        return new(skuLabel, title, ordered, shipped, returned, 0, OrderLineState.Pending, "○", "sevk bekliyor", SeverityLevel.Info, "");
    }

    public static string Summary(IReadOnlyList<OrderLineView> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        int Count(OrderLineState s) => lines.Count(l => l.State == s);
        var parts = new List<string> { $"{N(lines.Count)} satır" };
        if (Count(OrderLineState.OverReturned) > 0) parts.Add($"{N(Count(OrderLineState.OverReturned))} fazla iade çakışması");
        if (Count(OrderLineState.PartiallyShipped) + Count(OrderLineState.Pending) > 0) parts.Add($"{N(Count(OrderLineState.PartiallyShipped) + Count(OrderLineState.Pending))} eksik sevk");
        if (Count(OrderLineState.PartiallyReturned) + Count(OrderLineState.Returned) > 0) parts.Add($"{N(Count(OrderLineState.PartiallyReturned) + Count(OrderLineState.Returned))} iadeli");
        if (Count(OrderLineState.Cancelled) > 0) parts.Add("sipariş iptal");
        return string.Join(" · ", parts);
    }

    static string Cap(string value) => value.Length <= TitleLength ? value : value[..(TitleLength - 1)] + "…";
    static string N(int value) => value.ToString(CultureInfo.CurrentCulture);
}
