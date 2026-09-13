namespace TrMarketplaceHubDesktop;

/// <summary>Where a cancellation stands, read from the job's real state — never from the click alone.</summary>
public enum CancellationPhase { NotRequested, Requested, StoppingAtSafePoint, Cancelled, CompletedBeforeCancel }

/// <summary>The words a surface shows for a cancellation: the phase, a headline and one sentence of detail; terminal when the job will not move again.</summary>
public sealed record CancellationVerdict(CancellationPhase Phase, string Headline, string Detail)
{
    public bool IsTerminal => Phase is CancellationPhase.Cancelled or CancellationPhase.CompletedBeforeCancel;
    public SeverityLevel Level => Phase switch { CancellationPhase.Cancelled => SeverityLevel.Warning, CancellationPhase.CompletedBeforeCancel => SeverityLevel.Success, CancellationPhase.NotRequested => SeverityLevel.Info, _ => SeverityLevel.Info };
    public string Line => Detail.Length > 0 ? $"{Headline} · {Detail}" : Headline;
    public static readonly CancellationVerdict None = new(CancellationPhase.NotRequested, "", "");
}

/// <summary>
/// What a surface knows for certain: when the user asked (null when never), whether the job acknowledged the
/// request (it stopped and said so), whether the job reached its end (finished or failed) and when, and — while
/// it still runs — whether the stage under way can be interrupted or must finish its unit first, with that stage's
/// label. Nothing here is a guess: a stage's cancellability is the pipeline's own policy, and the times are the
/// job's own timestamps.
/// </summary>
public sealed record CancellationFacts(DateTime? RequestedUtc, bool Acknowledged, bool Ended, DateTime? EndedUtc = null, bool StageCancellable = true, string StageLabel = "");

/// <summary>
/// Cancellation outcome messaging (#890). A cancel click is a request, not an outcome: the job answers it at its
/// next check, and until then the surface may only say that the request is in. Four terminal or near-terminal
/// states follow from the job's real state — "İptal istendi" while the request waits for a check, "Güvenli noktada
/// duruyor" while a stage that cannot be interrupted finishes its unit, "İptal edildi" once the job acknowledged
/// and stopped (what stayed safe is the surface's own sentence), and "Tamamlanmıştı" when the job had already
/// reached its end, before the request or in spite of it. No partial state is quoted: the detail names stages
/// and guarantees, never rows, values or payloads.
/// </summary>
public static class CancellationOutcome
{
    public const string RequestedWord = "İptal istendi";
    public const string SafePointWord = "Güvenli noktada duruyor";
    public const string CancelledWord = "İptal edildi";
    public const string CompletedWord = "Tamamlanmıştı";
    public const string Tag = "cancel-outcome";

    /// <param name="stayedSafe">The surface's own guarantee for a stopped job — "Havuz değişmedi.", "Dosya yazılmadı." — shown with "İptal edildi".</param>
    public static CancellationVerdict Describe(CancellationFacts facts, string stayedSafe)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.RequestedUtc is null) return CancellationVerdict.None;
        if (facts.Acknowledged) return new(CancellationPhase.Cancelled, CancelledWord, Clean(stayedSafe));
        if (facts.Ended)
        {
            var afterRequest = facts.EndedUtc is { } ended && ended >= facts.RequestedUtc.Value;
            return new(CancellationPhase.CompletedBeforeCancel, CompletedWord, afterRequest
                ? "İptal isteği son güvenli noktadan sonra geldi; iş tamamlandı, sonucu geçerli."
                : "İstek geldiğinde iş bitmişti; iptal uygulanmadı, sonucu geçerli.");
        }
        if (!facts.StageCancellable)
        {
            var stage = (facts.StageLabel ?? "").Trim();
            return new(CancellationPhase.StoppingAtSafePoint, SafePointWord, stage.Length > 0 ? $"{stage} aşaması bölünemez; bittiğinde iş durur." : "Sürmekte olan aşama bölünemez; bittiğinde iş durur.");
        }
        return new(CancellationPhase.Requested, RequestedWord, "Sürmekte olan aşama bir sonraki kontrolde duracak.");
    }

    /// <summary>A queued or running sync job, cancelled through its store: pending stops before any request leaves, running finishes the request already on the wire and writes its own result, ended stays what it was.</summary>
    public static CancellationVerdict ForSyncJob(Catalog.SyncStatus before, bool cancelled)
    {
        if (cancelled && before == Catalog.SyncStatus.Pending) return new(CancellationPhase.Cancelled, CancelledWord, "Mağazaya istek gitmedi; iş kuyruktan çıktı.");
        if (cancelled && before == Catalog.SyncStatus.Running) return new(CancellationPhase.StoppingAtSafePoint, SafePointWord, "Mağazaya giden istek geri alınamaz; bittiğinde sonucu yazılır, iş yeniden kuyruğa alınmaz.");
        return before switch
        {
            Catalog.SyncStatus.Cancelled => new(CancellationPhase.Cancelled, CancelledWord, "İş zaten iptal edilmişti."),
            Catalog.SyncStatus.Succeeded or Catalog.SyncStatus.Failed => new(CancellationPhase.CompletedBeforeCancel, CompletedWord, "İş bitmişti; iptal uygulanmadı, sonucu geçerli."),
            _ => new(CancellationPhase.Requested, RequestedWord, "İş şu anda değiştirilemedi; durumu yenileyin."),
        };
    }

    static string Clean(string? text) => AuditStore.Redact(text ?? "").Trim();
}
