using System.Globalization;
using System.Text.RegularExpressions;

namespace TrMarketplaceHubDesktop;

/// <summary>What a status tooltip may say. Empty fields are not printed; there is no "—" placeholder line.</summary>
public sealed record StatusTooltipContent(string Status, string Reason = "", DateTime? LastChangeUtc = null, string Source = "", string NextAction = "", string Detail = "");

/// <summary>
/// The one status tooltip (#815): status, reason, last change, source, next action -- in that order, each on its
/// own labelled line, each hidden when empty. The reason is a sentence: newlines collapse, it is capped, and
/// anything that looks like a raw payload (a JSON or XML body, a stack trace) is replaced by a pointer to the
/// diagnostics screen rather than shown -- a tooltip is read by whoever is looking over the operator's shoulder
/// as much as by the operator. The whole text passes the central sanitizer last, so no bearer token, query
/// secret, e-mail or profile path survives whichever field it arrived in.
/// </summary>
public static class StatusTooltip
{
    public const int MaxReasonLength = 240;
    public const int MaxLength = 400;
    public const string RawPayloadHidden = "Ham yanıt gizlendi; ayrıntı için Tanılama ekranına bakın.";

    public static string Compose(StatusTooltipContent content, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(content);
        var lines = new List<string> { "Durum: " + (string.IsNullOrWhiteSpace(content.Status) ? "bilinmiyor" : Sentence(content.Status, 80)) };
        if (!string.IsNullOrWhiteSpace(content.Reason))
            lines.Add("Neden: " + (LooksLikeRawPayload(content.Reason) ? RawPayloadHidden : Sentence(content.Reason, MaxReasonLength)));
        if (content.LastChangeUtc is { } at)
            lines.Add($"Son değişiklik: {Relative(at, nowUtc)} ({TimeDisplay.Format(at)})");
        if (!string.IsNullOrWhiteSpace(content.Source)) lines.Add("Kaynak: " + Sentence(content.Source, 120));
        if (!string.IsNullOrWhiteSpace(content.Detail)) lines.Add(Sentence(content.Detail, 120));
        if (!string.IsNullOrWhiteSpace(content.NextAction)) lines.Add("Sonraki adım: " + Sentence(content.NextAction, 160));
        var text = AuditStore.Sanitize(string.Join("\n", lines)).Trim();
        return text.Length <= MaxLength ? text : text[..(MaxLength - 1)] + "…";
    }

    /// <summary>A body, a document or a stack trace is not a reason; it is evidence, and it lives in diagnostics.</summary>
    public static bool LooksLikeRawPayload(string value)
    {
        var s = (value ?? "").TrimStart();
        if (s.Length == 0) return false;
        if (s[0] is '{' or '[' && s.Contains(':')) return true;
        if (s.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) || (s[0] == '<' && s.Contains("</", StringComparison.Ordinal))) return true;
        if (Regex.IsMatch(s, @"(?m)^\s+at\s+[\w.<>`]+\(")) return true;
        return Regex.Matches(s, "\"[A-Za-z_][\\w-]*\"\\s*:").Count >= 2;
    }

    public static string Relative(DateTime thenUtc, DateTime nowUtc)
    {
        var span = nowUtc - thenUtc;
        if (span < TimeSpan.Zero) return "gelecekte";
        if (span < TimeSpan.FromMinutes(1)) return "az önce";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} dk önce";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours} sa önce";
        return $"{(int)span.TotalDays} gün önce";
    }

    /// <summary>The shared next step per semantic state, for owners that have nothing more specific to say.</summary>
    public static string NextActionFor(string stateKey) => (stateKey ?? "").Trim().ToLowerInvariant() switch
    {
        "pending" => "Sonucu bekleyin; uzarsa Sync ekranından yeniden deneyin.",
        "error" => "Kaynağı veya bağlantıyı düzeltip yeniden deneyin.",
        "stale" => "Kaynağı yeniden okuyun.",
        "unsupported" => "Bu kanal bu işlemi desteklemiyor; yerel kayıt geçerli kalır.",
        "warning" => "Tedarikçi verisini kontrol edin.",
        "disabled" => "Satışa açmak için ürünü aktife alın.",
        _ => "",
    };

    static string Sentence(string value, int max)
    {
        var flat = Regex.Replace(value ?? "", @"\s+", " ").Trim();
        return flat.Length <= max ? flat : flat[..(max - 1)] + "…";
    }
}
