using System.Globalization;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

/// <summary>A return/cancel request as the exception queue holds it, with the word and marker its status means.</summary>
public sealed record ReturnRequestRow(string Id, string Type, string Status, string Marker, string Word, string Message, DateTime CreatedUtc, SeverityLevel Level)
{
    /// <summary>Type, state and date only: a request message is free text from a marketplace or an operator and may name a customer, so it stays in the exception queue, not in this overview.</summary>
    public string Line => $"{Marker} {(Type == "Cancel" ? "iptal talebi" : "iade talebi")} · {Word} · {TimeDisplay.FormatDate(CreatedUtc)}";
}

/// <summary>One applied return event on the timeline.</summary>
public sealed record ReturnEventRow(DateTimeOffset AtUtc, string Sku, int Quantity, decimal Refund, string Currency, int StockRestored)
{
    public string Line => $"{TimeDisplay.Format(AtUtc)} · {Sku} × {Quantity.ToString(CultureInfo.CurrentCulture)} · iade {Refund.ToString("0.00", CultureInfo.CurrentCulture)} {Currency} · stoğa {StockRestored.ToString(CultureInfo.CurrentCulture)}";
}

public enum ReturnLineState { None, Partial, Full, Over }

/// <summary>One SKU's return position: what was ordered and fulfilled, what came back, what still can, and what a further return would put back into stock.</summary>
public sealed record ReturnLineRow(string Sku, int Ordered, int Fulfilled, int Returned, int Returnable, int? StockToRestoreIfReturned, ReturnLineState State, string Marker, string Word, SeverityLevel Level)
{
    public string Line => $"{Marker} {Sku} · {Word} ({Returned.ToString(CultureInfo.CurrentCulture)} / {Math.Min(Ordered, Fulfilled).ToString(CultureInfo.CurrentCulture)}) · kalan iade edilebilir {Returnable.ToString(CultureInfo.CurrentCulture)}" + (StockToRestoreIfReturned is { } r ? $" · iade edilirse stoğa dönecek {r.ToString(CultureInfo.CurrentCulture)}" : "");
}

public sealed record OrderReturnsSectionModel(string Headline, SeverityLevel Level, IReadOnlyList<ReturnRequestRow> Requests, IReadOnlyList<ReturnLineRow> Lines, IReadOnlyList<ReturnEventRow> Timeline, string RefundLine, SeverityLevel RefundLevel, IReadOnlyList<string> Notes)
{
    public bool HasReturns => Requests.Count > 0 || Timeline.Count > 0;
}

/// <summary>
/// The order's returns as one section (#840): the requests the exception queue holds for the order (pending,
/// preview ready, resolved, rejected -- each a marker and a word), each SKU's return position against what was
/// ordered and fulfilled (none / partial / full / over) with what still can come back and, from the real
/// reconciliation preview, what a further return would put back into stock, the applied events as a timeline,
/// and the refund consistency -- refunded against the order total, an over-refund named as a warning, a currency
/// that differs from the order's named as one. Messages are redacted and capped; nothing here reads a buyer.
/// </summary>
public static class OrderReturnsSection
{
    public const int MessageLength = 120;

    public static OrderReturnsSectionModel Compose(OrderSnapshot order, IReadOnlyList<OrderExceptionRecord> requests, IReadOnlyList<OrderReturnLedgerRow> events, OrderStockReceipt? receipt, Func<string, int, OrderReturnReconciliation?>? previewReturn)
    {
        ArgumentNullException.ThrowIfNull(order); ArgumentNullException.ThrowIfNull(requests); ArgumentNullException.ThrowIfNull(events);
        var requestRows = requests.Where(r => r.Type is "Return" or "Cancel").OrderByDescending(r => r.CreatedUtc).Select(r =>
        {
            var (marker, word, level) = (r.Status ?? "").Trim() switch
            {
                "Resolved" => ("✔", "çözüldü", SeverityLevel.Success),
                "Rejected" => ("⊘", "reddedildi", SeverityLevel.Info),
                "PreviewReady" => ("◔", "önizleme hazır", SeverityLevel.Warning),
                _ => ("⏳", "bekliyor", SeverityLevel.Warning),
            };
            return new ReturnRequestRow(r.Id, r.Type, r.Status ?? "", marker, word, SafeMessage(r.Message), r.CreatedUtc, level);
        }).ToList();

        var fulfilledBySku = (receipt?.Movements ?? new List<OrderStockMovement>()).GroupBy(m => m.Sku.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Sum(m => m.Quantity), StringComparer.OrdinalIgnoreCase);
        var returnedBySku = events.GroupBy(e => e.Sku.Trim(), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Sum(e => e.Quantity), StringComparer.OrdinalIgnoreCase);
        var lines = new List<ReturnLineRow>();
        foreach (var group in order.Items.GroupBy(i => (i.Sku ?? "").Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var sku = group.Key; if (sku.Length == 0) continue;
            var ordered = group.Sum(i => Math.Max(0, i.Quantity));
            // Without a receipt nothing was deducted; the reconciliation then caps returns at the ordered quantity, so mirror it.
            var fulfilled = receipt is null ? ordered : (fulfilledBySku.TryGetValue(sku, out var f) ? f : 0);
            var returned = returnedBySku.TryGetValue(sku, out var r) ? r : 0;
            var cap = Math.Min(ordered, fulfilled);
            var returnable = Math.Max(0, cap - returned);
            int? restore = null;
            if (returnable > 0 && previewReturn is not null) { var preview = previewReturn(sku, returnable); if (preview is not null && preview.Allowed) restore = preview.StockToRestore; }
            var (state, marker, word, level) = returned > cap ? (ReturnLineState.Over, "▼", "fazla iade", SeverityLevel.Blocking)
                : returned == 0 ? (ReturnLineState.None, "○", "iade yok", SeverityLevel.Info)
                : returned >= cap ? (ReturnLineState.Full, "↩", "tam iade", SeverityLevel.Warning)
                : (ReturnLineState.Partial, "◐", "kısmi iade", SeverityLevel.Warning);
            lines.Add(new(sku, ordered, fulfilled, returned, returnable, restore, state, marker, word, level));
        }

        var timeline = events.OrderBy(e => e.AppliedUtc).Select(e => new ReturnEventRow(e.AppliedUtc, e.Sku, e.Quantity, e.RefundAmount, e.Currency, e.StockRestored)).ToList();
        var orderCurrency = (order.Currency ?? "").Trim().ToUpperInvariant();
        var refunded = events.Sum(e => e.RefundAmount);
        var notes = new List<string>();
        string refundLine; SeverityLevel refundLevel;
        if (events.Count == 0) { refundLine = "İade tutarı yok"; refundLevel = SeverityLevel.Info; }
        else if (order.Total is { } total)
        {
            refundLine = $"İade tutarı {refunded.ToString("0.00", CultureInfo.CurrentCulture)} / {total.ToString("0.00", CultureInfo.CurrentCulture)} {orderCurrency}";
            if (refunded > total) { refundLine = "⚠ " + refundLine + " · sipariş toplamını aşıyor"; refundLevel = SeverityLevel.Blocking; notes.Add("İade edilen tutar sipariş toplamından fazla; kayıtları kontrol edin."); }
            else refundLevel = refunded == total ? SeverityLevel.Warning : SeverityLevel.Success;
        }
        else { refundLine = $"İade tutarı {refunded.ToString("0.00", CultureInfo.CurrentCulture)} {orderCurrency} · sipariş toplamı kayıtlı değil"; refundLevel = SeverityLevel.Info; }
        var foreign = events.Where(e => e.RefundAmount != 0 && orderCurrency.Length > 0 && !string.Equals((e.Currency ?? "").Trim(), orderCurrency, StringComparison.OrdinalIgnoreCase)).ToList();
        if (foreign.Count > 0) { notes.Add($"{foreign.Count.ToString(CultureInfo.CurrentCulture)} iade olayı sipariş para biriminden ({orderCurrency}) farklı birimde kaydedilmiş."); if (refundLevel < SeverityLevel.Warning) refundLevel = SeverityLevel.Warning; }

        var pending = requestRows.Count(r => r.Level == SeverityLevel.Warning);
        var parts = new List<string>();
        if (requestRows.Count > 0) parts.Add($"{requestRows.Count.ToString(CultureInfo.CurrentCulture)} talep" + (pending > 0 ? $" ({pending.ToString(CultureInfo.CurrentCulture)} açık)" : ""));
        if (timeline.Count > 0) parts.Add($"{timeline.Count.ToString(CultureInfo.CurrentCulture)} kayıtlı iade");
        var full = lines.Count(l => l.State == ReturnLineState.Full); var partial = lines.Count(l => l.State == ReturnLineState.Partial); var over = lines.Count(l => l.State == ReturnLineState.Over);
        if (over > 0) parts.Add($"{over.ToString(CultureInfo.CurrentCulture)} fazla iade");
        if (full > 0) parts.Add($"{full.ToString(CultureInfo.CurrentCulture)} tam");
        if (partial > 0) parts.Add($"{partial.ToString(CultureInfo.CurrentCulture)} kısmi");
        var headline = parts.Count == 0 ? "İade yok" : string.Join(" · ", parts);
        var sectionLevel = new[] { requestRows.Count == 0 ? SeverityLevel.Info : requestRows.Max(r => r.Level), lines.Count == 0 ? SeverityLevel.Info : lines.Max(l => l.Level), refundLevel }.Max();
        return new(headline, sectionLevel, requestRows, lines, timeline, refundLine, refundLevel, notes);
    }

    /// <summary>A request message a person can read: redacted, one line, capped; a payload is not a message.</summary>
    public static string SafeMessage(string? message)
    {
        var raw = (message ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (raw.Length == 0) return "(mesaj yok)";
        if (StatusTooltip.LooksLikeRawPayload(raw)) return StatusTooltip.RawPayloadHidden;
        var text = AuditStore.Redact(raw).Trim();
        return text.Length <= MessageLength ? text : text[..(MessageLength - 1)] + "…";
    }
}
