using System.Globalization;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

/// <summary>What the risk is judged from: the product's recorded stock and its source freshness, the open order quantity when the orders are known, the last stock synchronisation when the queue is known, and the moment.</summary>
public sealed record OversellInput(string Sku, int Stock, FreshnessState StockFreshness, string FreshnessWords, int? OpenOrderQuantity, DateTime? LastStockSyncUtc, DateTime NowUtc);

/// <summary>One reason the score rose: its key, its points and its words.</summary>
public sealed record OversellFactor(string Key, int Points, string Words);

/// <summary>The verdict: the level, the score, every factor, the inputs that were missing, whether the risk is resolved (nothing raises it), and the words.</summary>
public sealed record OversellRiskView(string Level, int Score, IReadOnlyList<OversellFactor> Factors, IReadOnlyList<string> MissingInputs, bool Resolved, string Words)
{
    public const string Low = "LOW", Medium = "MEDIUM", High = "HIGH";
}

/// <summary>
/// The oversell risk score (#934). Four things make a marketplace sell what the shelf no longer holds: a source stock
/// that is stale, frozen or unobserved; a stock too low once the open orders are taken off it; open orders pressing
/// on what is left; and a stock synchronisation that has not run in a day. Each is a factor with points and words;
/// the sum is a level — low under 30, medium under 60, high from 60 — and a resolved risk is one nothing raises.
/// An input nobody could supply (the orders, the synchronisation queue) is named as missing and the score says it
/// rests on what is known, never assumed clean. Decision support only: nothing here closes a listing or changes a
/// stock. Words carry SKUs, counts and hours only.
/// </summary>
public static class OversellRisk
{
    public const string FreshnessFactor = "freshness", NetStockFactor = "net-stock", PressureFactor = "pressure", SyncLagFactor = "sync-lag";
    public const string OrdersInput = "açık siparişler", SyncInput = "stok senkronu";
    public static readonly TimeSpan SyncLagLimit = TimeSpan.FromHours(24);
    public const int LowStock = 3;

    public static OversellRiskView Evaluate(OversellInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var factors = new List<OversellFactor>(); var missing = new List<string>();
        switch (input.StockFreshness)
        {
            case FreshnessState.Stale: factors.Add(new(FreshnessFactor, 35, $"kaynak stoku bayat ({input.FreshnessWords})")); break;
            case FreshnessState.Frozen: factors.Add(new(FreshnessFactor, 35, $"kaynak stoku tazelenmez ({input.FreshnessWords})")); break;
            case FreshnessState.Unknown: factors.Add(new(FreshnessFactor, 25, "stok gözlem zamanı yok")); break;
        }
        if (input.OpenOrderQuantity is { } open)
        {
            var net = input.Stock - open;
            if (net <= 0) factors.Add(new(NetStockFactor, 40, $"açık siparişler stoku aşıyor (stok {N(input.Stock)}, açık {N(open)})"));
            else if (net <= LowStock) factors.Add(new(NetStockFactor, 20, $"net stok düşük ({N(net)}: stok {N(input.Stock)}, açık {N(open)})"));
            if (open > 0 && open * 2 >= Math.Max(1, input.Stock)) factors.Add(new(PressureFactor, 10, $"rezervasyon baskısı (açık {N(open)}, stok {N(input.Stock)})"));
        }
        else
        {
            missing.Add(OrdersInput);
            if (input.Stock <= LowStock) factors.Add(new(NetStockFactor, 15, $"stok düşük ({N(input.Stock)}); açık siparişler bilinmiyor"));
        }
        if (input.LastStockSyncUtc is { } synced)
        {
            var lag = input.NowUtc - synced; if (lag < TimeSpan.Zero) lag = TimeSpan.Zero;
            if (lag > SyncLagLimit) factors.Add(new(SyncLagFactor, 15, $"stok senkronu {N((int)lag.TotalHours)} sa önce"));
        }
        else missing.Add(SyncInput);
        var score = Math.Min(100, factors.Sum(f => f.Points));
        var level = score >= 60 ? OversellRiskView.High : score >= 30 ? OversellRiskView.Medium : OversellRiskView.Low;
        var resolved = factors.Count == 0;
        var label = level == OversellRiskView.High ? "yüksek" : level == OversellRiskView.Medium ? "orta" : "düşük";
        var body = resolved ? "risk çözüldü; hiçbir etken yok" : string.Join("; ", factors.Select(f => f.Words + $" (+{N(f.Points)})"));
        var words = $"oversell riski {label} ({N(score)}/100): {body}" + (missing.Count > 0 ? $"; eksik girdi: {string.Join(", ", missing)}; skor bilinen girdilerle" : "") + "; karar desteği, otomatik satış kapatma yok";
        return new(level, score, factors, missing, resolved, words);
    }

    /// <summary>The quantity still to ship for a SKU: every item of every order that is not cancelled and has no shipment delivered or returned.</summary>
    public static int OpenQuantity(IEnumerable<OrderSnapshot> orders, string sku)
    {
        ArgumentNullException.ThrowIfNull(orders);
        var key = (sku ?? "").Trim();
        if (key.Length == 0) return 0;
        var total = 0;
        foreach (var order in orders)
        {
            if (order is null) continue;
            var status = UiSearch.Fold(order.RawStatus ?? "");
            if (status.Contains("iptal", StringComparison.Ordinal) || status.Contains("cancel", StringComparison.Ordinal)) continue;
            if (order.Shipments.Any(s => s.State is "Delivered" or "Returned")) continue;
            total += order.Items.Where(i => string.Equals((i.Sku ?? "").Trim(), key, StringComparison.OrdinalIgnoreCase)).Sum(i => Math.Max(0, i.Quantity));
        }
        return total;
    }

    /// <summary>The newest succeeded stock synchronisation among the product's jobs, or null when there was none.</summary>
    public static DateTime? LastStockSync(IEnumerable<SyncJob>? jobs) => jobs?.Where(j => j is not null && j.Status == SyncStatus.Succeeded && string.Equals(j.Operation, "stock", StringComparison.OrdinalIgnoreCase)).Select(j => (DateTime?)j.UpdatedUtc).Max();

    /// <summary>The verdict for a product from what the caller can supply: the product's freshness from its sources, the open orders when given, the stock synchronisation from its jobs.</summary>
    public static OversellRiskView Judge(CatalogProduct product, IEnumerable<SyncJob>? jobs, IEnumerable<OrderSnapshot>? orders, Func<string, XmlSource?> sourceById, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(product); ArgumentNullException.ThrowIfNull(sourceById);
        var freshness = ProductFreshness.Evaluate(product, sourceById, nowUtc).Fields.Single(f => f.Field == "Stock");
        return Evaluate(new(product.Sku, product.Stock, freshness.State, freshness.Words, orders is null ? null : OpenQuantity(orders, product.Sku), LastStockSync(jobs), nowUtc));
    }

    static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
