using System.Text.RegularExpressions;

namespace TrMarketplaceHubDesktop;

public enum NotificationSeverity { Success, Info, Warning, Error }

public sealed record NotificationRequest(NotificationSeverity Severity, string Text, string ActionLabel = "", string Route = "");

public sealed record Toast(long Id, NotificationSeverity Severity, string Text, string ActionLabel, string Route, int Count, DateTime ShownUtc, DateTime? ExpiresUtc);

/// <summary>
/// The app's toast policy (#814), decided in one place and driven by an injected clock so it is testable. Four
/// rules: a severity owns its lifetime (success shortest, error none -- an error waits for a person); the same
/// text at the same severity inside the dedupe window is one toast with a count, refreshed rather than
/// re-stacked; at most <see cref="MaxVisible"/> are on screen and the rest wait in arrival order, promoted as
/// slots free up, with a ceiling on the waiting line that drops the oldest non-error and counts the loss; and
/// every text goes through the central sanitizer first, then loses its newlines and its length -- a toast is a
/// sentence, and it never carries a bearer token, a query secret, an e-mail address or a profile path.
/// </summary>
public sealed class NotificationQueue
{
    public const int MaxVisible = 3;
    public const int MaxPending = 50;
    public const int MaxTextLength = 240;
    public static readonly TimeSpan DedupeWindow = TimeSpan.FromSeconds(30);

    readonly List<Toast> visible = new();
    readonly List<Toast> pending = new();
    long nextId;

    public IReadOnlyList<Toast> Visible => visible;
    public int PendingCount => pending.Count;
    public int DroppedCount { get; private set; }

    public static TimeSpan? DurationFor(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Success => TimeSpan.FromSeconds(4),
        NotificationSeverity.Info => TimeSpan.FromSeconds(6),
        NotificationSeverity.Warning => TimeSpan.FromSeconds(10),
        _ => null,
    };

    public Toast Publish(NotificationRequest request, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        var text = Clean(request.Text);
        var duration = DurationFor(request.Severity);

        // Dedupe against what is showing and what is waiting: the same news is one toast with a count.
        var twin = visible.Concat(pending).FirstOrDefault(t => t.Severity == request.Severity && t.Text == text && nowUtc - t.ShownUtc <= DedupeWindow);
        if (twin is not null)
        {
            var refreshed = twin with { Count = twin.Count + 1, ShownUtc = nowUtc, ExpiresUtc = duration is null ? null : nowUtc + duration };
            Replace(twin, refreshed);
            return refreshed;
        }

        var toast = new Toast(++nextId, request.Severity, text, request.ActionLabel ?? "", request.Route ?? "", 1, nowUtc, duration is null ? null : nowUtc + duration);
        if (visible.Count < MaxVisible) { visible.Add(toast); return toast; }

        // The waiting line's clock starts when a toast is shown, so a queued one is stored without an expiry.
        pending.Add(toast with { ExpiresUtc = null });
        while (pending.Count > MaxPending)
        {
            var victim = pending.FirstOrDefault(t => t.Severity != NotificationSeverity.Error);
            if (victim is null) break; // Only errors are waiting: the ceiling yields, an error is never dropped.
            pending.Remove(victim);
            DroppedCount++;
        }
        return toast;
    }

    /// <summary>Removes every visible toast whose clock has run out and moves the queue up; returns what was removed.</summary>
    public IReadOnlyList<Toast> Expire(DateTime nowUtc)
    {
        var removed = visible.Where(t => t.ExpiresUtc is { } at && at <= nowUtc).ToList();
        foreach (var toast in removed) visible.Remove(toast);
        Promote(nowUtc);
        return removed;
    }

    public bool Dismiss(long id)
    {
        var shown = visible.FirstOrDefault(t => t.Id == id);
        if (shown is not null) { visible.Remove(shown); Promote(shown.ShownUtc > DateTime.MinValue ? DateTime.UtcNow : DateTime.UtcNow); return true; }
        var waiting = pending.FirstOrDefault(t => t.Id == id);
        if (waiting is null) return false;
        pending.Remove(waiting);
        return true;
    }

    /// <summary>Escape closes what appeared last.</summary>
    public Toast? DismissTop()
    {
        if (visible.Count == 0) return null;
        var top = visible[^1];
        visible.RemoveAt(visible.Count - 1);
        Promote(DateTime.UtcNow);
        return top;
    }

    public void DismissAll() { visible.Clear(); pending.Clear(); }

    void Promote(DateTime nowUtc)
    {
        while (visible.Count < MaxVisible && pending.Count > 0)
        {
            var next = pending[0];
            pending.RemoveAt(0);
            var duration = DurationFor(next.Severity);
            visible.Add(next with { ShownUtc = nowUtc, ExpiresUtc = duration is null ? null : nowUtc + duration });
        }
    }

    void Replace(Toast old, Toast fresh)
    {
        var index = visible.IndexOf(old);
        if (index >= 0) { visible[index] = fresh; return; }
        index = pending.IndexOf(old);
        if (index >= 0) pending[index] = fresh with { ExpiresUtc = null };
    }

    static string Clean(string? raw)
    {
        var safe = AuditStore.Sanitize(raw ?? "");
        safe = Regex.Replace(safe, @"\s+", " ").Trim();
        if (safe.Length == 0) return "Bir olay gerçekleşti; ayrıntı için işlem geçmişine bakın.";
        return safe.Length <= MaxTextLength ? safe : safe[..(MaxTextLength - 1)] + "…";
    }
}
