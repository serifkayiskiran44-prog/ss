using System.Globalization;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public enum ImportOutcome { Success, Partial, Failure, Cancelled, NoOp }

public enum ImportNextActionKind { OpenProducts, ShowRejected, ShowWarnings, Retry, Reread, OpenDiagnostics }

/// <summary>A next step the summary offers; <paramref name="Route"/> is the workspace it needs, "" when it acts on the import page itself.</summary>
public sealed record ImportNextAction(ImportNextActionKind Kind, string Label, string Route = "");

/// <summary>Everything the terminal result of an import knows -- the store's summary, or the error, or the cancellation -- plus what the page knew before applying.</summary>
public sealed record ImportCompletionInput(
    string SourceName, int MappingRevision, string FeedHash, bool CompleteFeed,
    int Selected, int Rejected, int Warnings,
    ImportSummary? Result, string Error, bool Cancelled,
    DateTime StartedUtc, DateTime FinishedUtc);

public sealed record ImportCompletion(
    ImportOutcome Outcome, SeverityLevel Level, string Headline,
    int Applied, int Added, int Updated, int Unchanged, int Rejected, int Warnings,
    TimeSpan Duration, string Revision, string Detail, IReadOnlyList<ImportNextAction> NextActions)
{
    public string CountsLine => $"{N(Applied)} uygulandı ({N(Added)} yeni, {N(Updated)} güncel) · {N(Unchanged)} aynı · {N(Rejected)} reddedildi · {N(Warnings)} uyarılı";
    public string DurationLine => "Süre: " + ImportCompletionSummary.FormatDuration(Duration);
    static string N(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
}

/// <summary>
/// The completion summary of an import (#828), composed only from the terminal result: the store's
/// <see cref="ImportSummary"/>, the error, or the cancellation. Five outcomes -- success, partial (some rows
/// refused while others applied), failure (nothing written; the transaction rolled back), cancelled (same) and
/// no-op (the complete feed was already applied, or every selected row was already identical). Counts, the
/// duration, the source revision (name, mapping revision, feed-hash prefix) and the next actions the outcome
/// calls for. The error detail is redacted and a raw payload or stack trace is replaced by a pointer to the
/// diagnostics log; the source address never appears.
/// </summary>
public static class ImportCompletionSummary
{
    public static ImportCompletion Compose(ImportCompletionInput input, Func<string, bool> routeExists)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(routeExists);
        var duration = input.FinishedUtc < input.StartedUtc ? TimeSpan.Zero : input.FinishedUtc - input.StartedUtc;
        var revision = Revision(input);
        var result = input.Result;
        ImportOutcome outcome; string detail;
        if (input.Cancelled) { outcome = ImportOutcome.Cancelled; detail = "İşlem durduruldu; hiçbir satır yazılmadı, havuz değişmedi."; }
        else if (input.Error.Trim().Length > 0 || result is null) { outcome = ImportOutcome.Failure; detail = SafeError(input.Error); }
        else if (result.AlreadyApplied) { outcome = ImportOutcome.NoOp; detail = "Bu akış daha önce aynı hâliyle uygulanmıştı; havuz değişmedi."; }
        else if (result.Added + result.Updated == 0 && input.Rejected == 0) { outcome = ImportOutcome.NoOp; detail = "Seçili satırların tümü havuzdaki hâliyle aynı; havuz değişmedi."; }
        else if (input.Rejected > 0) { outcome = ImportOutcome.Partial; detail = $"{input.Rejected.ToString("N0", CultureInfo.CurrentCulture)} satır engel nedeniyle uygulanmadı; diğerleri havuza yazıldı."; }
        else { outcome = ImportOutcome.Success; detail = input.Warnings > 0 ? "Uyarılı satırlar da uygulandı; uyarılar ürün kartında görünür." : "Seçili satırların tümü havuza yazıldı."; }

        var added = outcome is ImportOutcome.Failure or ImportOutcome.Cancelled ? 0 : result?.Added ?? 0;
        var updated = outcome is ImportOutcome.Failure or ImportOutcome.Cancelled ? 0 : result?.Updated ?? 0;
        var unchanged = outcome is ImportOutcome.Failure or ImportOutcome.Cancelled ? 0 : result?.Unchanged ?? 0;
        // Nothing survives a failed or cancelled apply -- the whole selection is what was not written.
        var rejected = outcome is ImportOutcome.Failure or ImportOutcome.Cancelled ? Math.Max(0, input.Selected) : Math.Max(0, input.Rejected);
        var (level, headline) = outcome switch
        {
            ImportOutcome.Success => (SeverityLevel.Success, "Aktarım tamamlandı"),
            ImportOutcome.Partial => (SeverityLevel.Warning, "Aktarım kısmen tamamlandı"),
            ImportOutcome.Failure => (SeverityLevel.Blocking, "Aktarım başarısız"),
            ImportOutcome.Cancelled => (SeverityLevel.Warning, "Aktarım iptal edildi"),
            _ => (SeverityLevel.Info, "Değişiklik yok"),
        };
        return new(outcome, level, headline, added + updated, added, updated, unchanged, rejected, Math.Max(0, input.Warnings), duration, revision, detail, NextActions(outcome, input.Warnings, routeExists));
    }

    static IReadOnlyList<ImportNextAction> NextActions(ImportOutcome outcome, int warnings, Func<string, bool> routeExists)
    {
        var products = new ImportNextAction(ImportNextActionKind.OpenProducts, "Ürün havuzuna git", "products");
        var rejected = new ImportNextAction(ImportNextActionKind.ShowRejected, "Reddedilenleri göster");
        var warned = new ImportNextAction(ImportNextActionKind.ShowWarnings, "Uyarılı satırları göster");
        var retry = new ImportNextAction(ImportNextActionKind.Retry, "Yeniden dene");
        var reread = new ImportNextAction(ImportNextActionKind.Reread, "Kaynağı yeniden oku");
        var diagnostics = new ImportNextAction(ImportNextActionKind.OpenDiagnostics, "Tanılamayı aç", "diagnostics");
        IEnumerable<ImportNextAction> all = outcome switch
        {
            ImportOutcome.Success => warnings > 0 ? new[] { products, warned } : new[] { products },
            ImportOutcome.Partial => new[] { rejected, products },
            ImportOutcome.Failure => new[] { retry, diagnostics },
            ImportOutcome.Cancelled => new[] { retry },
            _ => new[] { reread, products },
        };
        return all.Where(a => a.Route.Length == 0 || routeExists(a.Route)).ToList();
    }

    /// <summary>Name, mapping revision and the feed's hash prefix -- a revision a person can quote, never the address.</summary>
    static string Revision(ImportCompletionInput input)
    {
        var name = AuditStore.Redact(input.SourceName ?? "").Trim();
        if (name.Length == 0) name = "Kaynak";
        var feed = input.CompleteFeed && !string.IsNullOrWhiteSpace(input.FeedHash) ? "akış " + input.FeedHash.Trim()[..Math.Min(8, input.FeedHash.Trim().Length)] : "kısmi seçim";
        return $"{name} · eşleme rev. {input.MappingRevision.ToString(CultureInfo.CurrentCulture)} · {feed}";
    }

    /// <summary>A person reads the reason; a body, a document or a stack trace is evidence for the diagnostics log.</summary>
    public static string SafeError(string error)
    {
        var raw = (error ?? "").Trim();
        if (raw.Length == 0) return "Sonuç alınamadı; ayrıntı tanılama günlüğünde.";
        if (StatusTooltip.LooksLikeRawPayload(raw)) return "Hata ayrıntısı tanılama günlüğünde.";
        var text = AuditStore.Redact(raw).Trim();
        return text.Length > 300 ? text[..300] + "…" : text;
    }

    public static string FormatDuration(TimeSpan d)
    {
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        if (d < TimeSpan.FromMinutes(1)) return d.TotalSeconds.ToString("0.#", CultureInfo.CurrentCulture) + " sn";
        return $"{(int)d.TotalMinutes} dk {d.Seconds} sn";
    }
}
