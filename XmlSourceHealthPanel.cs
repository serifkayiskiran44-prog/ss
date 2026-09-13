using System.Globalization;
using System.Text.RegularExpressions;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public enum SourceHealthVerdict { Healthy, Degraded, Failed, NeverRun }

public enum SourceHealthActionKind { Refresh, Check, Restart, Preview }

public sealed record SourceHealthAction(SourceHealthActionKind Kind, string Label);

public sealed record SourceHealthLine(string Label, string Value, SeverityLevel Level, string Detail = "");

/// <summary>Everything the detail panel is composed from -- recorded on the source, in the run store, in the quarantine, or on the page.</summary>
public sealed record SourceHealthFacts(
    string HealthState, DateTimeOffset? CheckedUtc, long? LatencyMs, int? HttpStatus, string HealthError,
    DateTime? LastSuccessUtc, int LastSuccessCount, string FeedState,
    int MappingRevision, int LastAppliedMappingRevision, string LastShapeFingerprint, string? CurrentShapeFingerprint,
    SourceRunSnapshot? LastRun, int ProductCount, int QuarantinePending, int QuarantineWarning,
    int? ValidationBlocking, int? ValidationWarning, int? ValidationRows, DateTime? ValidationUtc,
    ImportProgressStage? RetryStage, bool ImportRunning)
{
    /// <summary>#892: the source's credential state word (see <see cref="SourceCredentialHealth"/>); empty when unknown.</summary>
    public string CredentialState { get; init; } = "";
}

public sealed record SourceHealthPanelModel(SourceHealthVerdict Verdict, SeverityLevel Level, string Headline, IReadOnlyList<SourceHealthLine> Lines, IReadOnlyList<SourceHealthAction> Actions);

/// <summary>
/// The XML source detail health panel (#830): reachability, latency, last success, schema drift, last validation
/// and retry state as one composed view with a single verdict -- healthy, degraded (reachable but slow, drifted,
/// incomplete, quarantined or with blocking validation), failed (unreachable, or the last run failed with nothing
/// succeeding since) or never-run. Every value comes from what is already recorded; nothing here probes. Text
/// shown for an error is redacted: user information, tokens, Authorization/Basic/Bearer headers and secret query
/// parameters are masked and a payload or a stack trace is replaced by a pointer to the diagnostics log.
/// </summary>
public static class XmlSourceHealthPanel
{
    // A header name may be followed by a scheme word before the credential ("Authorization: Basic dXNl…"); the scheme is not the secret, what follows it is.
    static readonly Regex AuthHeader = new(@"(?i)\b(authorization|proxy-authorization|x-api-key|api[_-]?key)\b\s*[:=]?\s*(?:(?:basic|bearer|digest|token)\s+)?[A-Za-z0-9+/=._\-]{4,}", RegexOptions.Compiled);
    static readonly Regex AuthScheme = new(@"(?i)\b(basic|bearer|digest)\s+[A-Za-z0-9+/=._\-]{4,}", RegexOptions.Compiled);
    static readonly Regex UserInfo = new(@"(?i)(https?://)[^/@\s]+@", RegexOptions.Compiled);

    public static SourceHealthPanelModel Compose(SourceHealthFacts f, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(f);
        var health = (f.HealthState ?? "").Trim().ToUpperInvariant();
        var checkedOnce = health.Length > 0 && health != "NEVER_CHECKED";
        var reachable = checkedOnce && health == "HEALTHY";
        var runFailed = f.LastRun is { Status: "Failed" or "Abandoned" } && (f.LastSuccessUtc is null || f.LastRun.StartedUtc > f.LastSuccessUtc);
        var slow = f.LatencyMs is { } ms && ms >= XmlSourceListGrouping.DegradedLatencyMs;
        var shapeDrift = f.CurrentShapeFingerprint is { Length: > 0 } cur && f.LastShapeFingerprint.Length > 0 && cur != f.LastShapeFingerprint;
        var mappingDrift = f.LastAppliedMappingRevision > 0 && f.MappingRevision != f.LastAppliedMappingRevision;
        var incomplete = string.Equals(f.FeedState, "INCOMPLETE", StringComparison.OrdinalIgnoreCase);
        var everRan = f.LastRun is not null || f.LastSuccessUtc is not null;
        var lines = new List<SourceHealthLine>
        {
            Reachability(health, checkedOnce, f, nowUtc),
            f.LatencyMs is { } l ? new("Gecikme", $"{l.ToString("N0", CultureInfo.CurrentCulture)} ms" + (slow ? " · yavaş" : ""), slow ? SeverityLevel.Warning : SeverityLevel.Success) : new("Gecikme", "ölçülmedi", SeverityLevel.Info),
            f.LastSuccessUtc is { } ok
                ? new("Son başarı", $"{StatusTooltip.Relative(ok, nowUtc)} · {f.LastSuccessCount.ToString("N0", CultureInfo.CurrentCulture)} ürün" + (incomplete ? " · sonraki aktarım tamamlanmadı" : ""), incomplete ? SeverityLevel.Warning : SeverityLevel.Success)
                : new("Son başarı", "hiç başarılı olmadı", everRan ? SeverityLevel.Warning : SeverityLevel.Info),
            Drift(f, shapeDrift, mappingDrift),
            Validation(f, nowUtc),
            Retry(f, runFailed),
            new("Havuz", $"{f.ProductCount.ToString("N0", CultureInfo.CurrentCulture)} ürün" + (f.QuarantinePending + f.QuarantineWarning > 0 ? $" · kaynakta kayıp: {f.QuarantinePending.ToString("N0", CultureInfo.CurrentCulture)} bekleyen / {f.QuarantineWarning.ToString("N0", CultureInfo.CurrentCulture)} uyarı" : ""), f.QuarantinePending > 0 ? SeverityLevel.Warning : SeverityLevel.Info),
            Credential(f),
        };
        var verdict = !checkedOnce && !everRan ? SourceHealthVerdict.NeverRun
            : (checkedOnce && !reachable) || runFailed || f.RetryStage is not null || SourceCredentialHealth.ShouldFailFast(f.CredentialState) ? SourceHealthVerdict.Failed
            : slow || shapeDrift || mappingDrift || incomplete || f.QuarantinePending > 0 || f.ValidationBlocking > 0 ? SourceHealthVerdict.Degraded
            : SourceHealthVerdict.Healthy;
        var (level, headline) = verdict switch
        {
            SourceHealthVerdict.Healthy => (SeverityLevel.Success, "Sağlıklı"),
            SourceHealthVerdict.Degraded => (SeverityLevel.Warning, "Kısmen sorunlu"),
            SourceHealthVerdict.Failed => (SeverityLevel.Blocking, "Başarısız"),
            _ => (SeverityLevel.Info, "Hiç çalışmadı"),
        };
        var actions = new List<SourceHealthAction> { new(SourceHealthActionKind.Refresh, "Yenile"), new(SourceHealthActionKind.Check, "Erişimi kontrol et") };
        if (verdict == SourceHealthVerdict.Failed && !f.ImportRunning) actions.Add(new(SourceHealthActionKind.Restart, "Yeniden başlat"));
        if (shapeDrift || mappingDrift) actions.Add(new(SourceHealthActionKind.Preview, "Yeniden önizle"));
        return new(verdict, level, headline, lines, actions);
    }

    static SourceHealthLine Reachability(string health, bool checkedOnce, SourceHealthFacts f, DateTime nowUtc)
    {
        if (!checkedOnce) return new("Erişim", "hiç kontrol edilmedi", SeverityLevel.Info);
        var when = f.CheckedUtc is { } at ? " · " + StatusTooltip.Relative(at.UtcDateTime, nowUtc) : "";
        var http = f.HttpStatus is { } code ? $" · HTTP {code}" : "";
        return health == "HEALTHY"
            ? new("Erişim", "erişilebilir" + http + when, SeverityLevel.Success)
            : new("Erişim", XmlSourceListGrouping.HealthWord(health) + http + when, SeverityLevel.Blocking, SafeError(f.HealthError));
    }

    /// <summary>#892: the credential as a word -- gerekmiyor, kayıtlı · doğrulandı, eksik, reddedildi, okunamadı -- never a value.</summary>
    static SourceHealthLine Credential(SourceHealthFacts f) { var v = SourceCredentialHealth.Describe(f.CredentialState); return new("Kimlik bilgisi", v.Word, v.Level, v.Detail); }

    static SourceHealthLine Drift(SourceHealthFacts f, bool shapeDrift, bool mappingDrift)
    {
        if (f.LastAppliedMappingRevision <= 0) return new("Şema kayması", "henüz uygulanmadı", SeverityLevel.Info);
        if (shapeDrift) return new("Şema kayması", "XML yapısı son uygulamadan farklı", SeverityLevel.Warning, "Alan yolları değişmiş olabilir; yeniden önizleyin.");
        if (mappingDrift) return new("Şema kayması", $"eşleme rev. {f.MappingRevision.ToString(CultureInfo.CurrentCulture)} · uygulanan rev. {f.LastAppliedMappingRevision.ToString(CultureInfo.CurrentCulture)}", SeverityLevel.Warning, "Eşleme son uygulamadan sonra değişti.");
        return new("Şema kayması", f.CurrentShapeFingerprint is { Length: > 0 } ? "yok · yapı ve eşleme son uygulamayla aynı" : "yok · eşleme son uygulamayla aynı (yapı okunmadı)", SeverityLevel.Success);
    }

    static SourceHealthLine Validation(SourceHealthFacts f, DateTime nowUtc)
    {
        if (f.ValidationRows is not { } rows || f.ValidationUtc is not { } at) return new("Son doğrulama", "önizleme yapılmadı", SeverityLevel.Info);
        var blocking = f.ValidationBlocking ?? 0; var warning = f.ValidationWarning ?? 0;
        return new("Son doğrulama", $"{rows.ToString("N0", CultureInfo.CurrentCulture)} satır · {blocking.ToString("N0", CultureInfo.CurrentCulture)} engel · {warning.ToString("N0", CultureInfo.CurrentCulture)} uyarı · {StatusTooltip.Relative(at, nowUtc)}", blocking > 0 ? SeverityLevel.Blocking : warning > 0 ? SeverityLevel.Warning : SeverityLevel.Success);
    }

    static SourceHealthLine Retry(SourceHealthFacts f, bool runFailed)
    {
        if (f.ImportRunning) return new("Yeniden deneme", "aktarım sürüyor", SeverityLevel.Info);
        if (f.RetryStage is { } stage) return new("Yeniden deneme", $"{ImportProgressState.Stages.First(s => s.Stage == stage).Label} aşamasında durdu · yeniden denenebilir", SeverityLevel.Warning);
        if (runFailed) return new("Yeniden deneme", $"son çalıştırma {(f.LastRun!.Status == "Abandoned" ? "yarım kaldı" : "başarısız")} · yeniden başlatılabilir", SeverityLevel.Warning, SafeError(f.LastRun.Error));
        return new("Yeniden deneme", "gerekmiyor", SeverityLevel.Success);
    }

    /// <summary>A reason a person can read; never a header, a credential, a body or a trace.</summary>
    public static string SafeError(string? error)
    {
        var raw = (error ?? "").Trim();
        if (raw.Length == 0) return "";
        if (StatusTooltip.LooksLikeRawPayload(raw)) return "Ayrıntı tanılama günlüğünde.";
        var text = UserInfo.Replace(raw, "$1[user]@");
        text = AuthHeader.Replace(text, "$1 [redacted]");
        text = AuthScheme.Replace(text, "$1 [redacted]");
        text = MarketplaceConnectionStore.RedactSecrets(AuditStore.Redact(text)).Trim();
        return text.Length > 200 ? text[..200] + "…" : text;
    }
}
