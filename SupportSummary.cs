using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;

namespace TrMarketplaceHubDesktop;

/// <summary>What a support summary is about besides the failure: the screen, the source, the operation's correlation, the recent audit events, the moment.</summary>
public sealed record SupportSummaryContext(string? Route = null, string? SourceLabel = null, string? Correlation = null, IReadOnlyList<AuditEvent>? RecentEvents = null, DateTime? AtUtc = null);

/// <summary>
/// Safe copy summary (#884). One click turns a failure or a run into a support text an operator can paste anywhere:
/// the version, the moment, the screen and source, the correlation id, the failure's class and one-line summary,
/// the exception chain as type names and sanitized messages (five levels, then a note — never a stack frame, never
/// a raw body), and the last audit events as safe summaries. Every line passes the central redaction last, so an
/// e-mail, a phone number, a bearer value, a query secret or a user path never reaches the clipboard. The clipboard
/// itself may be unavailable (another process holds it, no desktop): <see cref="SafeClipboard.TryCopy"/> says so
/// instead of throwing, and the surfaces then show the text for a manual copy.
/// </summary>
public static class SupportSummary
{
    public const string Header = "MarketplaceHub destek özeti"; // #2562: canonical product name
    public const int MaxDepth = 5;
    public const int MaxLineLength = 300;
    public const int MaxRecentEvents = 10;
    public const string DeeperNote = "… (daha derin iç hatalar kısaltıldı)";
    public const string Copied = "Destek özeti panoya kopyalandı; kişisel veri ve gizli değerler maskelendi.";
    public const string ClipboardUnavailable = "Pano kullanılamadı; özet aşağıda, elle kopyalayın.";
    public const string CopyLabel = "Özeti kopyala";
    public const string CopyName = "Destek özetini kopyala";

    /// <summary>The summary of a failure, with whatever context the surface knows.</summary>
    public static string Compose(Exception? error, SupportSummaryContext? context = null)
    {
        var lines = Head(context);
        if (error is not null)
        {
            var model = ErrorBanner.Describe(error, retryAvailable: false);
            lines.Add($"Sınıf: {model.Title} ({(model.Recovery == ErrorRecovery.Retryable ? "yeniden denenebilir" : "kalıcı")})");
            lines.Add($"Özet: {model.Text}");
            var current = error is AggregateException { InnerException: { } first } ? first : error;
            var depth = 0;
            for (; current is not null && depth < MaxDepth; depth++, current = current.InnerException)
                lines.Add($"{(depth == 0 ? "Hata" : $"İç hata {depth}")}: {current.GetType().Name}: {Safe(current.Message)}");
            if (current is not null) lines.Add(DeeperNote);
        }
        Tail(lines, context);
        return Finish(lines);
    }

    /// <summary>The summary of an audit row (a recorded failure or run) and the rows related to it.</summary>
    public static string ComposeFromAudit(AuditEvent record, IReadOnlyList<AuditEvent>? related = null, SupportSummaryContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        var head = context is null ? new SupportSummaryContext(Correlation: record.Correlation) : context with { Correlation = CorrelationChain.IsId(context.Correlation) ? context.Correlation : record.Correlation };
        var lines = Head(head);
        lines.Add($"Kayıt: {Safe(record.Module)}/{Safe(record.Action)} · {Safe(record.Outcome)} · {record.AtUtc:yyyy-MM-dd HH:mm:ss} UTC");
        if (!string.IsNullOrWhiteSpace(record.Marketplace)) lines.Add($"Mağaza: {CorrelationChain.StoreKeyOf(record)}");
        if (!string.IsNullOrWhiteSpace(record.OrderId)) lines.Add($"Sipariş: {Safe(record.OrderId)}");
        if (!string.IsNullOrWhiteSpace(record.ProductId)) lines.Add($"Ürün: {Safe(record.ProductId)}");
        lines.Add($"Özet: {CorrelationChain.Summarize(record.Detail)}");
        Tail(lines, new SupportSummaryContext(RecentEvents: related ?? context?.RecentEvents));
        return Finish(lines);
    }

    static List<string> Head(SupportSummaryContext? context)
    {
        var lines = new List<string> { Header, $"Sürüm: {AppVersion.Display}", $"Zaman (UTC): {(context?.AtUtc ?? DateTime.UtcNow):yyyy-MM-dd HH:mm:ss}" };
        if (!string.IsNullOrWhiteSpace(context?.Route)) lines.Add($"Ekran: {Safe(context!.Route)}");
        if (!string.IsNullOrWhiteSpace(context?.SourceLabel)) lines.Add($"Kaynak: {Safe(context!.SourceLabel)}");
        var correlation = CorrelationChain.SafeId(context?.Correlation);
        if (correlation.Length > 0) lines.Add($"Korelasyon: {correlation}");
        return lines;
    }

    static void Tail(List<string> lines, SupportSummaryContext? context)
    {
        var recent = context?.RecentEvents;
        if (recent is null || recent.Count == 0) return;
        lines.Add("Son olaylar:");
        foreach (var e in recent.Take(MaxRecentEvents))
            lines.Add($"  {e.AtUtc:HH:mm:ss} {Safe(e.Module)}/{Safe(e.Action)} {Safe(e.Outcome)}{(CorrelationChain.IsId(e.Correlation) ? " [" + e.Correlation + "]" : "")}: {CorrelationChain.Summarize(e.Detail)}");
    }

    // The central redaction runs over every finished line, last, so nothing composed above can slip past it.
    static string Finish(List<string> lines) => string.Join(Environment.NewLine, lines.Select(l => AuditStore.Redact(l)));

    /// <summary>A field for the summary: a raw body replaced, redacted, one line, capped.</summary>
    public static string Safe(string? text)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0) return "";
        if (StatusTooltip.LooksLikeRawPayload(value)) return StatusTooltip.RawPayloadHidden;
        value = Regex.Replace(AuditStore.Redact(value), @"\s+", " ").Trim();
        return value.Length <= MaxLineLength ? value : value[..(MaxLineLength - 1)] + "…";
    }
}

/// <summary>The clipboard as a best-effort sink: a copy either lands or is reported as unavailable — never an exception out of a click.</summary>
public static class SafeClipboard
{
    /// <summary>How text reaches the clipboard; tests replace it to record or to fail.</summary>
    public static Action<string> Setter { get; set; } = text => Clipboard.SetDataObject(text, true);

    public static bool TryCopy(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try { Setter(text); return true; }
        catch (Exception error) when (error is COMException or ExternalException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException) { return false; }
    }
}
