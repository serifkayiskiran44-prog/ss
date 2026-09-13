using System.Globalization;

namespace TrMarketplaceHubDesktop;

/// <summary>One package as the shipping section lists it: what it is, where it stands, and what is wrong with it.</summary>
public sealed record ShipmentSummaryRow(string Id, string Carrier, string TrackingMasked, string StateLabel, string SlaLabel, string SlaStatus, SeverityLevel Level, string Marker, string Word, IReadOnlyList<string> Flags, int EventCount, bool HasTrackingUrl)
{
    public string Line => $"{Marker} {Word} · {Carrier} · {TrackingMasked} · {SlaLabel}" + (Flags.Count > 0 ? " · " + string.Join(" · ", Flags) : "");
}

public sealed record OrderShippingSectionModel(string Headline, SeverityLevel Level, IReadOnlyList<ShipmentSummaryRow> Rows, IReadOnlyList<string> Notes)
{
    public bool HasShipments => Rows.Count > 0;
}

/// <summary>
/// The order's shipping as one grouped section (#839): a headline that says how many packages there are (a
/// split shipment is named), one row per package with carrier, a masked tracking number, the observed state, the
/// #789 delivery SLA and the flags that need attention -- a tracking number used twice in this order, or one
/// that another order also carries (the #776 duplicate-tracking anomaly), a delayed or troubled delivery, a
/// package with no tracking at all. The level follows the worst package. Tracking numbers are shown masked
/// (first two and last four characters) and tracking links stripped to host and path in this default view; the
/// package editor below still holds the full values. Nothing here reads an address or a buyer.
/// </summary>
public static class OrderShippingSection
{
    public static OrderShippingSectionModel Compose(OrderSnapshot order, IReadOnlyDictionary<string, IReadOnlyList<string>>? trackingSeenElsewhere, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(order);
        // The caller's dictionary may compare keys any way it likes; tracking numbers are matched case-insensitively here regardless.
        var elsewhere = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in trackingSeenElsewhere ?? new Dictionary<string, IReadOnlyList<string>>())
        {
            var key = (kv.Key ?? "").Trim(); if (key.Length == 0) continue;
            elsewhere[key] = elsewhere.TryGetValue(key, out var existing) ? existing.Concat(kv.Value).Distinct(StringComparer.Ordinal).ToList() : kv.Value;
        }
        if (order.Shipments.Count == 0) return new("Paket yok", SeverityLevel.Info, Array.Empty<ShipmentSummaryRow>(), new[] { "Bu sipariş için kayıtlı paket yok; \"+ Paket ekle\" ile yerel bir paket açılabilir." });
        var shippedFallback = order.SourceUpdatedAt == default ? order.UpdatedAt : order.SourceUpdatedAt;
        var withinOrder = order.Shipments.Where(s => !string.IsNullOrWhiteSpace(s.TrackingNumber)).GroupBy(s => s.TrackingNumber.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = new List<ShipmentSummaryRow>();
        foreach (var s in order.Shipments)
        {
            var sla = OrdersRules.EvaluateSla(s, shippedFallback, nowUtc);
            var tracking = (s.TrackingNumber ?? "").Trim();
            var flags = new List<string>();
            if (tracking.Length == 0) flags.Add("takip no yok");
            if (tracking.Length > 0 && withinOrder.Contains(tracking)) flags.Add("aynı takip no bu siparişte iki kez");
            if (tracking.Length > 0 && elsewhere.TryGetValue(tracking, out var others))
            {
                var otherIds = others.Where(id => !string.Equals(id, order.OrderId, StringComparison.Ordinal)).ToList();
                if (otherIds.Count > 0) flags.Add($"takip no {N(otherIds.Count)} başka siparişte de var");
            }
            var (level, marker, word) = sla.Status switch
            {
                "DELAYED" => (SeverityLevel.Blocking, "✖", "gecikti"),
                "EXCEPTION" => (SeverityLevel.Blocking, "✖", "teslimat sorunu"),
                "RETURNED" => (SeverityLevel.Warning, "↩", "iade"),
                "DELIVERED" => (SeverityLevel.Success, "✔", "teslim edildi"),
                "IN_TRANSIT" => (SeverityLevel.Info, "→", "yolda"),
                _ => (SeverityLevel.Info, "○", "gönderilmedi"),
            };
            if (flags.Count > 0 && level < SeverityLevel.Warning) level = SeverityLevel.Warning;
            rows.Add(new(s.Id, string.IsNullOrWhiteSpace(s.Carrier) ? "taşıyıcı yok" : s.Carrier.Trim(), MaskTracking(tracking), OrdersRules.Label(s.State), sla.Label, sla.Status, level, marker, word, flags, s.Events.Count, OrdersRules.SafeTrackingUrl((s.TrackingUrl ?? "").Trim())));
        }
        var worst = rows.Max(r => r.Level);
        var parts = new List<string> { rows.Count == 1 ? "1 paket" : $"{N(rows.Count)} paket · bölünmüş gönderi" };
        var delayed = rows.Count(r => r.SlaStatus == "DELAYED"); if (delayed > 0) parts.Add($"{N(delayed)} gecikmiş");
        var trouble = rows.Count(r => r.SlaStatus is "EXCEPTION" or "RETURNED"); if (trouble > 0) parts.Add($"{N(trouble)} sorunlu");
        var delivered = rows.Count(r => r.SlaStatus == "DELIVERED"); if (delivered > 0) parts.Add($"{N(delivered)} teslim");
        var flagged = rows.Count(r => r.Flags.Count > 0); if (flagged > 0) parts.Add($"{N(flagged)} işaretli");
        var notes = new List<string>();
        if (rows.Any(r => r.Flags.Any(f => f.StartsWith("aynı takip", StringComparison.Ordinal)))) notes.Add("Aynı takip numarası iki pakette: biri yanlış girilmiş olabilir.");
        if (rows.Any(r => r.Flags.Any(f => f.Contains("başka siparişte", StringComparison.Ordinal)))) notes.Add("Bir takip numarası başka siparişlerde de kayıtlı; paketler karışmış olabilir.");
        return new(string.Join(" · ", parts), worst, rows, notes);
    }

    /// <summary>First two and last four characters survive; anything shorter than nine is shown whole -- there is nothing to hide in "AB12".</summary>
    public static string MaskTracking(string? tracking)
    {
        var t = (tracking ?? "").Trim();
        if (t.Length == 0) return "—";
        if (t.Length <= 8) return t;
        return t[..2] + new string('•', Math.Min(6, t.Length - 6)) + t[^4..];
    }

    /// <summary>A tracking link in the overview is host and path only; a query string (postcode, token) never shows.</summary>
    public static string MaskTrackingUrl(string? url)
    {
        var u = (url ?? "").Trim();
        if (u.Length == 0 || !Uri.TryCreate(u, UriKind.Absolute, out var uri)) return "";
        return $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";
    }

    static string N(int value) => value.ToString(CultureInfo.CurrentCulture);
}
