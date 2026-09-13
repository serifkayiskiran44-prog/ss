using System.Globalization;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public enum SourceSlaState { OnTime, Overdue, NeverDelivered, Disabled }

/// <summary>One source's standing against its refresh SLA: the state, its word, the detail, the deadline and how late it is.</summary>
public sealed record SourceSlaVerdict(SourceSlaState State, string Word, string Detail, DateTime? DeadlineUtc, TimeSpan OverdueBy, SeverityLevel Level)
{
    public string Line => Detail.Length > 0 ? $"{Word} · {Detail}" : Word;
}

/// <summary>
/// The per-source refresh SLA (#898): how often a source is expected to deliver a successful feed
/// (<c>SlaRefreshMinutes</c>, 0 = its check interval) and how much later than that is still acceptable
/// (<c>SlaGraceMinutes</c>, 0 = half the expected refresh, at least fifteen minutes). A source is on time while its
/// last successful feed is younger than refresh + grace, overdue after that, "never delivered" when it has no
/// successful feed at all, and out of scope while disabled. Every moment is UTC — a wall-clock change (DST) or a
/// restart cannot move the verdict, because the facts it reads are the recorded UTC times. The scheduler asks
/// <see cref="DueForRetry"/> to try an overdue source at the grace cadence instead of waiting a whole check
/// interval, and <see cref="Transition"/> records the state on the source so a crossing is reported once, not
/// every tick. Names only — never a feed address.
/// </summary>
public static class SourceSla
{
    public static readonly TimeSpan MinimumGrace = TimeSpan.FromMinutes(15);
    public const string AuditAction = "source-sla";

    /// <summary>The expected refresh: the profile's own value, or the check interval when none is set.</summary>
    public static TimeSpan Refresh(XmlSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return TimeSpan.FromMinutes(Math.Max(1, source.SlaRefreshMinutes > 0 ? source.SlaRefreshMinutes : source.IntervalMinutes));
    }

    /// <summary>The tolerance after the expected refresh: the profile's own value, or half the refresh, at least fifteen minutes.</summary>
    public static TimeSpan Grace(XmlSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.SlaGraceMinutes > 0) return TimeSpan.FromMinutes(source.SlaGraceMinutes);
        var half = TimeSpan.FromTicks(Refresh(source).Ticks / 2);
        return half < MinimumGrace ? MinimumGrace : half;
    }

    public static SourceSlaVerdict Evaluate(XmlSource source, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(source);
        var nowUtc = Utc(now);
        var refresh = Refresh(source); var grace = Grace(source);
        var profile = $"beklenen her {Minutes(refresh)}, tolerans {Minutes(grace)}";
        if (!source.Enabled) return new(SourceSlaState.Disabled, "pasif", "kaynak devre dışı; SLA uygulanmıyor", null, TimeSpan.Zero, SeverityLevel.Info);
        if (source.LastSuccessfulFeedUtc is not { } fedAt) return new(SourceSlaState.NeverDelivered, "hiç teslim etmedi", profile, null, TimeSpan.Zero, SeverityLevel.Warning);
        var fed = Utc(fedAt);
        var deadline = fed + refresh + grace;
        if (nowUtc <= deadline) return new(SourceSlaState.OnTime, "zamanında", $"son teslim {Span(nowUtc - fed)} önce · son tarihe {Span(deadline - nowUtc)} · {profile}", deadline, TimeSpan.Zero, SeverityLevel.Success);
        var late = nowUtc - deadline;
        return new(SourceSlaState.Overdue, "gecikmiş", $"{Span(late)} gecikti · son teslim {Span(nowUtc - fed)} önce · {profile}", deadline, late, SeverityLevel.Warning);
    }

    /// <summary>The scheduler's retry rule: an overdue auto-import source is worth a new attempt once a grace period has passed since its last attempt, instead of a whole check interval.</summary>
    public static bool DueForRetry(XmlSource source, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.Enabled || !source.AutoImport) return false;
        if (Evaluate(source, now).State != SourceSlaState.Overdue) return false;
        var lastAttempt = source.LastRunUtc is { } run ? Utc(run) : DateTime.MinValue;
        return Utc(now) - lastAttempt >= Grace(source);
    }

    /// <summary>Records the verdict's state on the source. True when it is a crossing worth reporting once: any change after the first record, or a first record that is already overdue.</summary>
    public static bool Transition(XmlSource source, SourceSlaVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(verdict);
        var word = verdict.State.ToString();
        if (string.Equals(source.LastSlaState, word, StringComparison.Ordinal)) return false;
        var first = string.IsNullOrEmpty(source.LastSlaState);
        source.LastSlaState = word;
        return !first || verdict.State == SourceSlaState.Overdue;
    }

    /// <summary>The audit row for a crossing: module import, the source's name and the verdict line, redacted.</summary>
    public static AuditEvent ToAudit(XmlSource source, SourceSlaVerdict verdict)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(verdict);
        var name = string.IsNullOrWhiteSpace(source.Name) ? "adsız kaynak" : source.Name.Trim();
        return new AuditEvent { Module = "import", Action = AuditAction, Outcome = verdict.State == SourceSlaState.Overdue ? "Warning" : "Info", Detail = AuditStore.Redact($"{name}: {verdict.Line}") };
    }

    static DateTime Utc(DateTime t) => t.Kind switch { DateTimeKind.Local => t.ToUniversalTime(), DateTimeKind.Unspecified => DateTime.SpecifyKind(t, DateTimeKind.Utc), _ => t };
    static string Minutes(TimeSpan s) => $"{((int)Math.Round(s.TotalMinutes)).ToString(CultureInfo.CurrentCulture)} dk";

    static string Span(TimeSpan s)
    {
        if (s < TimeSpan.Zero) s = TimeSpan.Zero;
        if (s.TotalMinutes < 1) return "1 dk'dan az";
        if (s.TotalHours < 1) return $"{(int)s.TotalMinutes} dk";
        if (s.TotalDays < 1) return s.Minutes > 0 ? $"{(int)s.TotalHours} sa {s.Minutes} dk" : $"{(int)s.TotalHours} sa";
        return $"{(int)s.TotalDays} gün";
    }
}
