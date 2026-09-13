using System.Globalization;

namespace TrMarketplaceHubDesktop;

/// <summary>Three tiers: the identity, the where-and-what, then the operational facts -- each a short line the header can wrap.</summary>
public sealed record OrderDetailHeaderModel(string Title, string FullId, string Store, string StatusWord, string Payment, string Sla, string Exceptions, SeverityLevel Level, string Glyph)
{
    public string Secondary => $"{Store} · {StatusWord}";
    public string Tertiary => $"{Payment} · {Sla} · {Exceptions}";
    /// <summary>What a screen reader gets: every tier, the full id, no ellipsis.</summary>
    public string AutomationText => $"Sipariş {FullId}. {Secondary}. {Tertiary}.";
}

/// <summary>
/// The order detail's sticky header (#837): id first (elided in the middle past 24 characters, the full id kept
/// for the tooltip and the automation name), then channel/store and the order's status word, then payment,
/// delivery SLA and the exception summary. Missing payment says so instead of showing a blank; a cancelled order
/// leads with "İptal" and its SLA is not judged. The level (and glyph) follows the worst fact -- a delivery
/// exception or a delay blocks, pending exceptions warn, cancelled is informational, otherwise success -- so the
/// accent is an echo, never the only signal. Nothing here reads an item, a buyer or an address.
/// </summary>
public static class OrderDetailHeader
{
    public const int MaxIdChars = 24;

    public static OrderDetailHeaderModel Compose(OrderSnapshot order, int pendingExceptions, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(order);
        var id = (order.OrderId ?? "").Trim(); if (id.Length == 0) id = "(numarasız)";
        var raw = (order.RawStatus ?? "").Trim().ToLower(CultureInfo.GetCultureInfo("tr-TR"));
        var cancelled = raw.Contains("cancel", StringComparison.Ordinal) || raw.Contains("iptal", StringComparison.Ordinal) || raw.Contains("ıptal", StringComparison.Ordinal);
        var statusWord = cancelled ? "İptal" : raw switch { "paid" => "Ödendi", "shipped" => "Gönderildi", "" => "Durum yok", _ => Cap(order.RawStatus.Trim(), 40) };
        var store = $"{Cap(Blank(order.Marketplace, "kanal yok"), 30)} · {Cap(Blank(order.ShopId, "mağaza yok"), 30)}";
        var paymentRaw = (order.PaymentStatus ?? "").Trim();
        var payment = paymentRaw.Length == 0 || string.Equals(paymentRaw, "Bilinmiyor", StringComparison.OrdinalIgnoreCase) ? "Ödeme bilgisi yok" : "Ödeme: " + Cap(paymentRaw, 30);
        var sla = cancelled ? "SLA değerlendirilmez" : order.Shipments.Count == 0 ? "Paket yok" : order.SlaLabelAt(nowUtc);
        var exceptions = pendingExceptions > 0 ? $"{pendingExceptions.ToString(CultureInfo.CurrentCulture)} bekleyen istisna" : "istisna yok";
        var level = cancelled ? SeverityLevel.Info
            : sla.StartsWith("Gecikti", StringComparison.Ordinal) || sla == "Teslimat sorunu" ? SeverityLevel.Blocking
            : pendingExceptions > 0 || sla == "İade" ? SeverityLevel.Warning
            : SeverityLevel.Success;
        var glyph = level switch { SeverityLevel.Blocking => "✖", SeverityLevel.Warning => "⚠", SeverityLevel.Info => "ℹ", _ => "✔" };
        return new(Elide(id), id, store, statusWord, payment, sla, exceptions, level, glyph);
    }

    /// <summary>A long id keeps both ends: the start people recognise and the end that tells siblings apart.</summary>
    public static string Elide(string id)
    {
        if (id.Length <= MaxIdChars) return id;
        var keep = MaxIdChars - 1; var head = keep / 2 + keep % 2; var tail = keep / 2;
        return id[..head] + "…" + id[^tail..];
    }

    static string Blank(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    static string Cap(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}
