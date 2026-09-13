using System.Globalization;

namespace TrMarketplaceHubDesktop;

public enum ImportProgressStage { Download, Read, Parse, Validate, Preview, Apply }

public enum ImportProgressStatus { Pending, Running, Done, Failed, Cancelled }

/// <summary>One real event from the pipeline: a stage started, advanced (<paramref name="Done"/> of an optional <paramref name="Total"/>), finished, failed or was cancelled.</summary>
public sealed record ImportProgressEvent(ImportProgressStage Stage, ImportProgressStatus Status, long Done = 0, long? Total = null, DateTime AtUtc = default, string Note = "");

public sealed record ImportStageProgress(ImportProgressStage Stage, string Label, ImportProgressStatus Status, long Done, long? Total, DateTime? StartedUtc, TimeSpan Elapsed, string Note)
{
    /// <summary>A percentage exists only when the total is known and positive; otherwise the bar is indeterminate.</summary>
    public double? Percent => Total is { } t && t > 0 ? Math.Min(100d, 100d * Done / t) : null;
    public bool IsIndeterminate => Status == ImportProgressStatus.Running && Percent is null;
    public string Counter => Total is { } t ? $"{Done.ToString("N0", CultureInfo.CurrentCulture)} / {t.ToString("N0", CultureInfo.CurrentCulture)}" : Done > 0 ? Done.ToString("N0", CultureInfo.CurrentCulture) : "";
}

/// <summary>
/// The import's progress as stages (#827), fed only by real events from the pipeline -- a stage begins, counts
/// what it has processed against a total when one is known, and ends done, failed or cancelled. Nothing here
/// estimates: a stage with an unknown total shows a count and an indeterminate bar, never a percentage, and a
/// stage that took a second is recorded like one that took a minute. Cancellation marks the running stage and
/// leaves later stages pending; retry resets the failed or cancelled stage and everything after it, keeping what
/// finished before. Notes are redacted, so a stage can say "12 ürün" but never quote the source address.
/// </summary>
public sealed class ImportProgressState
{
    public static readonly IReadOnlyList<(ImportProgressStage Stage, string Label)> Stages = new[]
    {
        (ImportProgressStage.Download, "İndir"), (ImportProgressStage.Read, "Oku"), (ImportProgressStage.Parse, "Ayrıştır"),
        (ImportProgressStage.Validate, "Doğrula"), (ImportProgressStage.Preview, "Önizle"), (ImportProgressStage.Apply, "Havuza aktar (yerel)"),
    };

    readonly Dictionary<ImportProgressStage, ImportStageProgress> stages = Stages.ToDictionary(s => s.Stage, s => new ImportStageProgress(s.Stage, s.Label, ImportProgressStatus.Pending, 0, null, null, TimeSpan.Zero, ""));

    public IReadOnlyList<ImportStageProgress> Snapshot(DateTime nowUtc) => Stages.Select(s => WithElapsed(stages[s.Stage], nowUtc)).ToList();

    public ImportStageProgress this[ImportProgressStage stage] => stages[stage];

    public ImportProgressStage? Running => stages.Values.FirstOrDefault(s => s.Status == ImportProgressStatus.Running)?.Stage;
    public ImportProgressStage? Failed => stages.Values.FirstOrDefault(s => s.Status is ImportProgressStatus.Failed or ImportProgressStatus.Cancelled)?.Stage;
    public bool IsComplete => stages.Values.All(s => s.Status == ImportProgressStatus.Done);

    public void Apply(ImportProgressEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var current = stages[e.Stage];
        var at = e.AtUtc == default ? DateTime.UtcNow : e.AtUtc;
        var note = AuditStore.Redact(e.Note ?? "").Trim();
        stages[e.Stage] = e.Status switch
        {
            ImportProgressStatus.Running => current with { Status = ImportProgressStatus.Running, Done = e.Done, Total = e.Total ?? current.Total, StartedUtc = current.Status == ImportProgressStatus.Running ? current.StartedUtc ?? at : at, Note = note.Length > 0 ? note : current.Note },
            ImportProgressStatus.Done => current with { Status = ImportProgressStatus.Done, Done = e.Total ?? (e.Done > 0 ? e.Done : current.Done), Total = e.Total ?? current.Total, StartedUtc = current.StartedUtc ?? at, Elapsed = current.StartedUtc is { } s ? at - s : TimeSpan.Zero, Note = note.Length > 0 ? note : current.Note },
            ImportProgressStatus.Failed => current with { Status = ImportProgressStatus.Failed, Elapsed = current.StartedUtc is { } s ? at - s : TimeSpan.Zero, Note = note },
            ImportProgressStatus.Cancelled => current with { Status = ImportProgressStatus.Cancelled, Elapsed = current.StartedUtc is { } s ? at - s : TimeSpan.Zero, Note = note.Length > 0 ? note : "İptal edildi" },
            _ => current with { Status = ImportProgressStatus.Pending, Done = 0, Total = null, StartedUtc = null, Elapsed = TimeSpan.Zero, Note = "" },
        };
        // Starting a stage means every later stage is pending again (a re-read invalidates the preview and the apply).
        if (e.Status == ImportProgressStatus.Running) foreach (var later in Stages.Where(s => s.Stage > e.Stage)) stages[later.Stage] = Reset(stages[later.Stage]);
    }

    /// <summary>A cancellation lands on whatever is running; nothing else changes.</summary>
    public void Cancel(DateTime nowUtc)
    {
        if (Running is { } running) Apply(new(running, ImportProgressStatus.Cancelled, AtUtc: nowUtc));
    }

    /// <summary>Retry keeps what finished before the failure and resets the failed or cancelled stage and everything after it.</summary>
    public ImportProgressStage? Retry()
    {
        var failed = Failed;
        if (failed is null) return null;
        foreach (var s in Stages.Where(s => s.Stage >= failed.Value)) stages[s.Stage] = Reset(stages[s.Stage]);
        return failed;
    }

    static ImportStageProgress Reset(ImportStageProgress s) => s with { Status = ImportProgressStatus.Pending, Done = 0, Total = null, StartedUtc = null, Elapsed = TimeSpan.Zero, Note = "" };

    static ImportStageProgress WithElapsed(ImportStageProgress s, DateTime nowUtc) => s.Status == ImportProgressStatus.Running && s.StartedUtc is { } started ? s with { Elapsed = nowUtc - started } : s;

    public static string StatusWord(ImportProgressStatus status) => status switch
    {
        ImportProgressStatus.Running => "sürüyor", ImportProgressStatus.Done => "tamam", ImportProgressStatus.Failed => "başarısız", ImportProgressStatus.Cancelled => "iptal", _ => "bekliyor",
    };
}
