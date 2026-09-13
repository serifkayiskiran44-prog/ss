using System.Globalization;

namespace TrMarketplaceHubDesktop;

public enum ReportRunStage { Query, Generate, Export }

public enum ReportRunStageStatus { Pending, Running, Done, Failed, Cancelled }

/// <summary>One real event from a report run: a stage started, advanced (<paramref name="Done"/> of an optional <paramref name="Total"/>), finished, failed or was cancelled.</summary>
public sealed record ReportRunProgressEvent(ReportRunStage Stage, ReportRunStageStatus Status, long Done = 0, long? Total = null, DateTime AtUtc = default, string Note = "");

public sealed record ReportRunStageProgress(ReportRunStage Stage, string Label, ReportRunStageStatus Status, long Done, long? Total, DateTime? StartedUtc, TimeSpan Elapsed, string Note)
{
    /// <summary>A percentage exists only when the total is known and positive; otherwise the bar is indeterminate.</summary>
    public double? Percent => Total is { } t && t > 0 ? Math.Min(100d, 100d * Done / t) : null;
    public bool IsIndeterminate => Status == ReportRunStageStatus.Running && Percent is null;
    public string Counter => Total is { } t ? $"{Done.ToString("N0", CultureInfo.CurrentCulture)} / {t.ToString("N0", CultureInfo.CurrentCulture)}" : Done > 0 ? Done.ToString("N0", CultureInfo.CurrentCulture) : "";
}

/// <summary>
/// A report run as stages (#848) -- query, generate, export -- fed only by the runner's real events. A stage with
/// an unknown total shows a count and an indeterminate bar, never a guessed percentage; cancellation lands on the
/// running stage and leaves the rest pending; a failure keeps its sanitized note as the terminal diagnostics.
/// Notes pass the uncapped redaction, so a stage can say "1.200 / 4.000 kayıt" but never a query text.
/// </summary>
public sealed class ReportRunProgressState
{
    public static readonly IReadOnlyList<(ReportRunStage Stage, string Label)> Stages = new[] { (ReportRunStage.Query, "Sorgu"), (ReportRunStage.Generate, "Üretim"), (ReportRunStage.Export, "Dışa aktarma") };

    readonly Dictionary<ReportRunStage, ReportRunStageProgress> stages = Stages.ToDictionary(s => s.Stage, s => new ReportRunStageProgress(s.Stage, s.Label, ReportRunStageStatus.Pending, 0, null, null, TimeSpan.Zero, ""));

    public IReadOnlyList<ReportRunStageProgress> Snapshot(DateTime nowUtc) => Stages.Select(s => WithElapsed(stages[s.Stage], nowUtc)).ToList();
    public ReportRunStageProgress this[ReportRunStage stage] => stages[stage];
    public ReportRunStage? Running => stages.Values.FirstOrDefault(s => s.Status == ReportRunStageStatus.Running)?.Stage;
    /// <summary>The stage that failed or was cancelled, if any.</summary>
    public ReportRunStage? Failed => stages.Values.FirstOrDefault(s => s.Status is ReportRunStageStatus.Failed or ReportRunStageStatus.Cancelled)?.Stage;
    public bool IsComplete => stages.Values.All(s => s.Status == ReportRunStageStatus.Done);
    public bool IsTerminal => IsComplete || Failed is not null;
    public bool IsCancelled => stages.Values.Any(s => s.Status == ReportRunStageStatus.Cancelled);
    /// <summary>The terminal diagnostics: the failed stage's sanitized note, the cancellation sentence, or nothing.</summary>
    public string Diagnostics => Failed is { } f ? (stages[f].Status == ReportRunStageStatus.Cancelled ? "İptal edildi; dosya yazılmadı." : stages[f].Note.Length > 0 ? stages[f].Note : "Hata ayrıntısı yok.") : "";

    public void Apply(ReportRunProgressEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var current = stages[e.Stage];
        var at = e.AtUtc == default ? DateTime.UtcNow : e.AtUtc;
        var note = AuditStore.Redact(e.Note ?? "").Trim();
        stages[e.Stage] = e.Status switch
        {
            ReportRunStageStatus.Running => current with { Status = ReportRunStageStatus.Running, Done = e.Done, Total = e.Total ?? current.Total, StartedUtc = current.Status == ReportRunStageStatus.Running ? current.StartedUtc ?? at : at, Note = note.Length > 0 ? note : current.Note },
            ReportRunStageStatus.Done => current with { Status = ReportRunStageStatus.Done, Done = e.Total ?? (e.Done > 0 ? e.Done : current.Done), Total = e.Total ?? current.Total, StartedUtc = current.StartedUtc ?? at, Elapsed = current.StartedUtc is { } s ? at - s : TimeSpan.Zero, Note = note.Length > 0 ? note : current.Note },
            ReportRunStageStatus.Failed => current with { Status = ReportRunStageStatus.Failed, Elapsed = current.StartedUtc is { } s ? at - s : TimeSpan.Zero, Note = note },
            ReportRunStageStatus.Cancelled => current with { Status = ReportRunStageStatus.Cancelled, Elapsed = current.StartedUtc is { } s ? at - s : TimeSpan.Zero, Note = note.Length > 0 ? note : "İptal edildi" },
            _ => Reset(current),
        };
        if (e.Status == ReportRunStageStatus.Running) foreach (var later in Stages.Where(s => s.Stage > e.Stage)) stages[later.Stage] = Reset(stages[later.Stage]);
    }

    /// <summary>A cancellation lands on whatever is running; when nothing runs yet, on the query.</summary>
    public void Cancel(DateTime nowUtc) => Apply(new(Running ?? ReportRunStage.Query, ReportRunStageStatus.Cancelled, AtUtc: nowUtc));

    /// <summary>One line for the whole run.</summary>
    public string Headline(DateTime nowUtc)
    {
        if (IsComplete) return "Tamamlandı";
        if (Failed is { } f) { var stage = stages[f]; return $"{stage.Label} {StatusWord(stage.Status)}" + (stage.Status == ReportRunStageStatus.Failed && stage.Note.Length > 0 ? ": " + stage.Note : ""); }
        if (Running is { } r) { var stage = WithElapsed(stages[r], nowUtc); return $"{stage.Label} sürüyor" + (stage.Counter.Length > 0 ? " · " + stage.Counter : "") + (stage.Percent is { } p ? $" (%{p:0})" : ""); }
        // #849: the query and the generation are done, the export not asked for yet -- the result is on screen.
        if (stages[ReportRunStage.Generate].Status == ReportRunStageStatus.Done && stages[ReportRunStage.Export].Status == ReportRunStageStatus.Pending) return "Sonuç hazır; dışa aktarma isteğe bağlı";
        return "Bekliyor";
    }

    static ReportRunStageProgress Reset(ReportRunStageProgress s) => s with { Status = ReportRunStageStatus.Pending, Done = 0, Total = null, StartedUtc = null, Elapsed = TimeSpan.Zero, Note = "" };
    static ReportRunStageProgress WithElapsed(ReportRunStageProgress s, DateTime nowUtc) => s.Status == ReportRunStageStatus.Running && s.StartedUtc is { } started ? s with { Elapsed = nowUtc - started } : s;

    public static string StatusWord(ReportRunStageStatus status) => status switch { ReportRunStageStatus.Running => "sürüyor", ReportRunStageStatus.Done => "tamam", ReportRunStageStatus.Failed => "başarısız", ReportRunStageStatus.Cancelled => "iptal", _ => "bekliyor" };
    public static string Glyph(ReportRunStageStatus status) => status switch { ReportRunStageStatus.Done => "✔", ReportRunStageStatus.Running => "⟳", ReportRunStageStatus.Failed => "✖", ReportRunStageStatus.Cancelled => "⏹", _ => "○" };
}
