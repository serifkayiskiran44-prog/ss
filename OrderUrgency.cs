using System.Globalization;

namespace TrMarketplaceHubDesktop;

/// <summary>The states an order's urgency is scored from -- ids, states, counts and timestamps; never a name, an address or an item title.</summary>
public sealed record OrderUrgencyInput(string RawStatus, IReadOnlyList<DeliverySla> Slas, DateTimeOffset LastSyncUtc, DateTimeOffset SourceUpdatedUtc, int UnmappedItems, int PendingExceptions, DateTimeOffset NowUtc);

public sealed record OrderUrgency(int Score, string Band, IReadOnlyList<string> Reasons)
{
    public string Label => Reasons.Count == 0 ? $"{Band} · {Score.ToString(CultureInfo.CurrentCulture)}" : $"{Band} · {Score.ToString(CultureInfo.CurrentCulture)} · {string.Join(", ", Reasons)}";
}

/// <summary>
/// A deterministic urgency score for the order list (#835), from states the order already carries: a delivery
/// exception or return, a delayed package (the #789 SLA), pending exceptions from the exception store, items no
/// catalogue product matches, a missing or stale sync, and a paid order that has waited unshipped. A cancelled
/// order scores 0 in its own band and nothing else counts. The same input always gives the same score; ties are
/// broken by the order's key, so two runs sort alike. A missing timestamp earns no points and says so. Reasons are
/// generic words with counts -- no customer PII ever reaches the score, the label or a log line.
/// </summary>
public static class OrderUrgencyScorer
{
    public const string CancelledBand = "iptal";
    static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    public static readonly IReadOnlyList<string> Bands = new[] { "acil", "yüksek", "normal", "düşük", CancelledBand };
    /// <summary>The bands as the UI names them -- fixed Turkish capitals, never derived with the running culture ("iptal" capitalised under en-US is "Iptal", not "İptal").</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> BandOptions = new[] { ("acil", "Acil"), ("yüksek", "Yüksek"), ("normal", "Normal"), ("düşük", "Düşük"), (CancelledBand, "İptal") };

    public static OrderUrgency Score(OrderUrgencyInput i)
    {
        ArgumentNullException.ThrowIfNull(i);
        // Turkish-aware lower-casing: an invariant lower of "İptal" is "i̇ptal" (dotted i + combining mark) and never matches "iptal".
        var raw = (i.RawStatus ?? "").Trim().ToLower(Turkish);
        if (raw.Contains("cancel", StringComparison.Ordinal) || raw.Contains("iptal", StringComparison.Ordinal) || raw.Contains("ıptal", StringComparison.Ordinal)) return new(0, CancelledBand, new[] { "sipariş iptal" });
        var score = 0; var reasons = new List<string>();
        if (i.Slas.Any(s => s.Status == "EXCEPTION")) { score += 40; reasons.Add("teslimat sorunu"); }
        if (i.Slas.Any(s => s.Status == "RETURNED")) { score += 25; reasons.Add("iade"); }
        var delayed = i.Slas.Where(s => s.Status == "DELAYED").ToList();
        if (delayed.Count > 0) { var days = delayed.Max(s => s.DaysInTransit); score += 30 + Math.Min(20, Math.Max(0, days - OrdersRules.DefaultMaxTransitDays)); reasons.Add($"gecikti {N(days)} gün"); }
        if (i.PendingExceptions > 0) { score += 20 + Math.Min(15, (i.PendingExceptions - 1) * 5); reasons.Add($"{N(i.PendingExceptions)} bekleyen istisna"); }
        if (i.UnmappedItems > 0) { score += 20 + Math.Min(15, (i.UnmappedItems - 1) * 5); reasons.Add($"{N(i.UnmappedItems)} eşlenmemiş kalem"); }
        if (i.LastSyncUtc == default) { score += 10; reasons.Add("senkron yok"); }
        else if (i.NowUtc - i.LastSyncUtc > TimeSpan.FromHours(24)) { score += 10; reasons.Add("senkron eski"); }
        var unshipped = i.Slas.Count == 0 || i.Slas.All(s => s.Status == "NOT_SHIPPED");
        if (unshipped && raw == "paid")
        {
            if (i.SourceUpdatedUtc == default) reasons.Add("zaman damgası yok");
            else
            {
                var days = Math.Max(0, (int)Math.Floor((i.NowUtc - i.SourceUpdatedUtc).TotalDays));
                if (days >= 1) { score += 10 + Math.Min(20, days * 2); reasons.Add($"{N(days)} gün bekliyor"); }
            }
        }
        return new(score, Band(score), reasons);
    }

    public static string Band(int score) => score >= 60 ? "acil" : score >= 30 ? "yüksek" : score >= 10 ? "normal" : "düşük";

    /// <summary>The input for a snapshot, from the same SLA evaluation the SLA column uses and the catalogue's SKUs.</summary>
    public static OrderUrgencyInput InputFor(OrderSnapshot order, IReadOnlySet<string> knownSkus, int pendingExceptions, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(order); ArgumentNullException.ThrowIfNull(knownSkus);
        var shippedFallback = order.SourceUpdatedAt == default ? order.UpdatedAt : order.SourceUpdatedAt;
        var slas = order.Shipments.Select(s => OrdersRules.EvaluateSla(s, shippedFallback, nowUtc)).ToList();
        var unmapped = order.Items.Count(item => string.IsNullOrWhiteSpace(item.Sku) || !knownSkus.Contains(item.Sku.Trim()));
        return new(order.RawStatus, slas, order.LastSync, order.SourceUpdatedAt, unmapped, pendingExceptions, nowUtc);
    }

    /// <summary>Highest score first; equal scores by key (ordinal), so the order is the same on every run.</summary>
    public static IReadOnlyList<T> Sort<T>(IEnumerable<T> items, Func<T, int> score, Func<T, string> key)
    {
        ArgumentNullException.ThrowIfNull(items); ArgumentNullException.ThrowIfNull(score); ArgumentNullException.ThrowIfNull(key);
        return items.OrderByDescending(score).ThenBy(key, StringComparer.Ordinal).ToList();
    }

    static string N(int value) => value.ToString(CultureInfo.CurrentCulture);
}
