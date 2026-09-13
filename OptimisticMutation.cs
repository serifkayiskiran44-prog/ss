namespace TrMarketplaceHubDesktop;

/// <summary>What a mutation touches: the local store, or a live marketplace (never optimistic).</summary>
public enum MutationScope { Local, LiveMarketplace }

/// <summary>How a mutation ended: committed to the store; reverted to the prior state after a failure; refused before anything changed.</summary>
public enum MutationOutcome { Committed, Reverted, Refused }

public sealed record MutationReport(MutationOutcome Outcome, string? Error);

/// <summary>
/// The optimistic UI rollback pattern (#874). The screen takes the change at once (<c>apply</c>), the store is asked
/// to keep it (<c>commit</c>), and if the store refuses — a validation, a database failure, a cancellation — the
/// prior state comes back (<c>revert</c>) and a visible, redacted error says so. A second click on the same key
/// while the first is in flight is refused before it touches anything, and so is a live marketplace write: those
/// keep their explicit approval and idempotency and are never applied ahead of the marketplace.
/// </summary>
public static class OptimisticMutation
{
    public const string LiveWriteRefused = "Canlı pazaryeri yazımı iyimser uygulanmaz; açık onay ve idempotency korunur.";
    public const string DuplicateRefused = "İşlem zaten sürüyor; ikinci tıklama yok sayıldı.";
    public const string Cancelled = "İşlem iptal edildi; önceki durum geri alındı.";
    public const string RevertedPrefix = "Kaydedilemedi; önceki durum geri alındı.";
    public const string RevertFailedPrefix = "Önceki durum da geri yüklenemedi; ekranı yenileyin.";

    static readonly HashSet<string> inFlight = new(StringComparer.Ordinal);
    static readonly object gate = new();

    public static bool IsInFlight(string key) { lock (gate) return inFlight.Contains(key); }

    public static MutationReport Run(string key, Action apply, Action commit, Action revert, Action<string> reportError, MutationScope scope = MutationScope.Local)
    {
        ArgumentNullException.ThrowIfNull(apply); ArgumentNullException.ThrowIfNull(commit); ArgumentNullException.ThrowIfNull(revert); ArgumentNullException.ThrowIfNull(reportError);
        if (Refuse(key, scope, reportError) is { } refused) return refused;
        apply();
        try { commit(); return new MutationReport(MutationOutcome.Committed, null); }
        catch (Exception ex) { var error = Revert(revert, ex); reportError(error); return new MutationReport(MutationOutcome.Reverted, error); }
        finally { lock (gate) inFlight.Remove(key); }
    }

    /// <summary>Brings the prior state back and describes the failure; a revert that fails itself is reported in the same words, never thrown out of a click.</summary>
    static string Revert(Action revert, Exception failure)
    {
        var error = Describe(failure);
        try { revert(); }
        catch (Exception revertFailure) { error += $" {RevertFailedPrefix} {AuditStore.Redact((revertFailure.Message ?? "").Trim())}".TrimEnd(); }
        return error;
    }

    public static async Task<MutationReport> RunAsync(string key, Action apply, Func<CancellationToken, Task> commit, Action revert, Action<string> reportError, CancellationToken cancellation = default, MutationScope scope = MutationScope.Local)
    {
        ArgumentNullException.ThrowIfNull(apply); ArgumentNullException.ThrowIfNull(commit); ArgumentNullException.ThrowIfNull(revert); ArgumentNullException.ThrowIfNull(reportError);
        if (Refuse(key, scope, reportError) is { } refused) return refused;
        apply();
        try { cancellation.ThrowIfCancellationRequested(); await commit(cancellation); return new MutationReport(MutationOutcome.Committed, null); }
        catch (Exception ex) { var error = Revert(revert, ex); reportError(error); return new MutationReport(MutationOutcome.Reverted, error); }
        finally { lock (gate) inFlight.Remove(key); }
    }

    static MutationReport? Refuse(string key, MutationScope scope, Action<string> reportError)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (scope == MutationScope.LiveMarketplace) { reportError(LiveWriteRefused); return new MutationReport(MutationOutcome.Refused, LiveWriteRefused); }
        lock (gate) { if (!inFlight.Add(key)) { reportError(DuplicateRefused); return new MutationReport(MutationOutcome.Refused, DuplicateRefused); } }
        return null;
    }

    /// <summary>The words shown for a failure: cancellation by name, everything else through the central redaction.</summary>
    public static string Describe(Exception ex) => ex is OperationCanceledException ? Cancelled : $"{RevertedPrefix} {AuditStore.Redact((ex?.Message ?? "").Trim())}".Trim();
}
