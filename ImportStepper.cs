namespace TrMarketplaceHubDesktop;

public enum ImportStage { Source, Mapping, Validation, Preview, Apply, Result }

public enum ImportStageStatus { Pending, Current, Done, Blocked, Stale, Running }

/// <summary>The XML flow's real state, as the window holds it; the stepper derives everything from this and adds nothing.</summary>
public sealed record ImportFlowState
{
    public bool HasSource { get; init; }
    public bool SourceSaved { get; init; }
    public bool XmlLoaded { get; init; }
    public bool XmlMatchesSource { get; init; } = true;
    public bool MappingReady { get; init; }
    public string MappingProblem { get; init; } = "";
    public bool PreviewExists { get; init; }
    public bool PreviewFresh { get; init; }
    public bool ApplyRunning { get; init; }
    public bool ApplyCancelled { get; init; }
    public string ResultStatus { get; init; } = "";
    public bool ResultFailed { get; init; }
}

public sealed record ImportStep(ImportStage Stage, string Label, ImportStageStatus Status, bool CanJump, string Reason);

/// <summary>
/// The import workflow stepper (#822): six stages the operator can see, each computed from the flow's real state
/// -- a source that is chosen and saved, an XML that was actually read for that address, a mapping the catalogue
/// would accept, a preview whose fingerprint still matches the XML and the mapping, an apply that is running or
/// done, and a result. Nothing is a fake percentage and nothing is skipped by decoration: a stage can be jumped to
/// only when the state behind it is real (you may go back to mapping any time; you may go forward to preview only
/// after a read and a ready mapping; you may apply only from a fresh preview). A preview whose fingerprint no
/// longer matches is shown as stale and blocks apply, which is exactly what the import command already refuses.
/// The apply stage writes the local pool and says so; this stepper has no stage that is a live marketplace write.
/// </summary>
public static class ImportStepper
{
    public static readonly IReadOnlyList<(ImportStage Stage, string Label)> Stages = new[]
    {
        (ImportStage.Source, "Kaynak"), (ImportStage.Mapping, "Eşleme"), (ImportStage.Validation, "Doğrulama"),
        (ImportStage.Preview, "Önizleme"), (ImportStage.Apply, "Havuza aktar (yerel)"), (ImportStage.Result, "Sonuç"),
    };

    public static IReadOnlyList<ImportStep> Compute(ImportFlowState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var steps = new List<ImportStep>();
        ImportStage current = Current(s);

        foreach (var (stage, label) in Stages)
        {
            var (status, canJump, reason) = stage switch
            {
                ImportStage.Source => s.HasSource
                    ? (Done(stage, current, s), true, "")
                    : (ImportStageStatus.Current, true, "Önce bir XML kaynağı seçin veya ekleyin."),
                ImportStage.Mapping => !s.HasSource
                    ? (ImportStageStatus.Pending, false, "Kaynak seçilmeden eşleme yapılamaz.")
                    : !s.XmlLoaded ? (ImportStageStatus.Blocked, true, "XML henüz okunmadı; alanlar bulunmadan eşleme doğrulanamaz.")
                    : (Done(stage, current, s), true, ""),
                ImportStage.Validation => !s.XmlLoaded
                    ? (ImportStageStatus.Pending, false, "XML okunmadan doğrulama yapılamaz.")
                    : !s.MappingReady ? (ImportStageStatus.Blocked, false, s.MappingProblem.Length > 0 ? s.MappingProblem : "Eşleme eksik: zorunlu alanlar bağlanmadı.")
                    : (Done(stage, current, s), false, ""),
                ImportStage.Preview => !s.XmlLoaded || !s.MappingReady
                    ? (ImportStageStatus.Pending, false, "Önizleme için okunmuş XML ve hazır eşleme gerekir.")
                    : !s.PreviewExists ? (ImportStageStatus.Current, true, "")
                    : !s.PreviewFresh ? (ImportStageStatus.Stale, true, "XML veya eşleme değişti; önizleme geçersiz, yeniden önizleyin.")
                    : (Done(stage, current, s), true, ""),
                ImportStage.Apply => s.ApplyRunning
                    ? (ImportStageStatus.Running, false, "Aktarım sürüyor; iptal edilebilir.")
                    : !(s.PreviewExists && s.PreviewFresh) ? (ImportStageStatus.Pending, false, "Yalnızca güncel bir önizlemeden aktarılır.")
                    : s.ResultStatus.Length > 0 && !s.ResultFailed && !s.ApplyCancelled ? (ImportStageStatus.Done, true, "")
                    : (ImportStageStatus.Current, true, ""),
                _ => s.ApplyCancelled
                    ? (ImportStageStatus.Blocked, false, "Aktarım iptal edildi; yeniden önizleyip tekrar başlatın.")
                    : s.ResultFailed ? (ImportStageStatus.Blocked, false, "Aktarım başarısız: " + s.ResultStatus)
                    : s.ResultStatus.Length > 0 && !s.ApplyRunning ? (ImportStageStatus.Done, true, s.ResultStatus)
                    : (ImportStageStatus.Pending, false, ""),
            };
            steps.Add(new ImportStep(stage, label, status, canJump, reason));
        }
        return steps;
    }

    /// <summary>The stage the operator is at: the first one that is not yet done, in order.</summary>
    public static ImportStage Current(ImportFlowState s)
    {
        if (!s.HasSource) return ImportStage.Source;
        if (!s.XmlLoaded) return ImportStage.Mapping;
        if (!s.MappingReady) return ImportStage.Validation;
        if (!s.PreviewExists || !s.PreviewFresh) return ImportStage.Preview;
        if (s.ApplyRunning || s.ResultStatus.Length == 0) return ImportStage.Apply;
        return ImportStage.Result;
    }

    /// <summary>Restart is the read stage again: everything after the source is forgotten, the source stays.</summary>
    public static ImportFlowState Restart(ImportFlowState s) => new() { HasSource = s.HasSource, SourceSaved = s.SourceSaved };

    /// <summary>Every stage this stepper knows writes locally; a live marketplace write is not a stage here.</summary>
    public static bool IsLiveWrite(ImportStage stage) => false;

    static ImportStageStatus Done(ImportStage stage, ImportStage current, ImportFlowState s) => stage < current ? ImportStageStatus.Done : stage == current ? ImportStageStatus.Current : ImportStageStatus.Pending;
}
