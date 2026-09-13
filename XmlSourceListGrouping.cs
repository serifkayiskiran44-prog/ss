using System.Globalization;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

/// <summary>Where a source sits in the list: bands in the order the operator needs them, problems before quiet.</summary>
public enum SourceHealthBand { Running, Problem, Healthy, NeverRun, Disabled, Unsupported }

/// <summary>The latest run record of a source, as the run store keeps it.</summary>
public sealed record SourceRunSnapshot(string Status, DateTime StartedUtc, DateTime? FinishedUtc, DateTime? LeaseUntilUtc, string Error);

public sealed record SourceListEntry(ImportSourceRow Row, SourceHealthBand Band, string BandLabel, string LastSuccess, string NextSchedule, string ActiveRun, string Problem)
{
    /// <summary>The second line of the row: last success, next schedule, the active run and the problem -- whichever apply.</summary>
    public string Detail => string.Join(" · ", new[] { "son başarı " + LastSuccess, "sonraki: " + NextSchedule, ActiveRun, Problem }.Where(x => x.Length > 0));
}

public sealed record SourceListGroup(SourceHealthBand Band, string Label, IReadOnlyList<SourceListEntry> Entries);

public sealed record SourceListFilter(SourceHealthBand? Band = null, string Text = "");

/// <summary>
/// The XML source list grouped by health (#829). A source lands in exactly one band from what the page already
/// records: an active run (a live lease) is Running; a reachability failure, a slow check, a failed or abandoned
/// last run or an incomplete feed is a Problem; a healthy check with a clean last run is Healthy; a source that was
/// never checked and never ran is NeverRun; a disabled source is Disabled and an unsupported location is
/// Unsupported. Each entry says when it last succeeded, when the schedule will next try it and what is running now.
/// Filtering is by band and by text over the title and the masked location, so a secret in an address can never be
/// found by searching for it. Errors shown come redacted; a payload or a stack trace is never a problem text.
/// </summary>
public static class XmlSourceListGrouping
{
    public const long DegradedLatencyMs = 5000;

    public static readonly IReadOnlyList<(SourceHealthBand Band, string Label)> Bands = new[]
    {
        (SourceHealthBand.Running, "Sürüyor"), (SourceHealthBand.Problem, "Sorunlu"), (SourceHealthBand.Healthy, "Sağlıklı"),
        (SourceHealthBand.NeverRun, "Hiç çalışmadı"), (SourceHealthBand.Disabled, "Pasif"), (SourceHealthBand.Unsupported, "Desteklenmiyor"),
    };

    public static string Label(SourceHealthBand band) => Bands.First(b => b.Band == band).Label;

    public static SourceListEntry Describe(XmlSource source, ImportSourceRow row, SourceRunSnapshot? run, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(row);
        var (band, problem) = Classify(source, row, run, nowUtc);
        var lastSuccess = source.LastSuccessfulFeedUtc is { } ok ? StatusTooltip.Relative(ok, nowUtc) : "hiç";
        return new(row, band, Label(band), lastSuccess, NextSchedule(source, nowUtc), ActiveRun(run, nowUtc), problem);
    }

    public static (SourceHealthBand Band, string Problem) Classify(XmlSource source, ImportSourceRow row, SourceRunSnapshot? run, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(row);
        if (!row.Supported) return (SourceHealthBand.Unsupported, "");
        if (!source.Enabled) return (SourceHealthBand.Disabled, "");
        if (run is { Status: "Running" })
        {
            if (run.LeaseUntilUtc is { } lease && lease < nowUtc) return (SourceHealthBand.Problem, "askıda çalıştırma · kilit süresi doldu");
            return (SourceHealthBand.Running, "");
        }
        var health = (source.LastHealthState ?? "").Trim().ToUpperInvariant();
        var checkedOnce = health.Length > 0 && health != "NEVER_CHECKED";
        if (SourceCredentialHealth.ShouldFailFast(source.LastCredentialState)) return (SourceHealthBand.Problem, "kimlik bilgisi: " + SourceCredentialHealth.Describe(source.LastCredentialState).Word);
        if (checkedOnce && health != "HEALTHY") return (SourceHealthBand.Problem, "erişim: " + HealthWord(health) + SafeError(source.LastHealthError));
        if (checkedOnce && source.LastHealthLatencyMs is { } ms && ms >= DegradedLatencyMs) return (SourceHealthBand.Problem, $"yavaş · {ms.ToString("N0", CultureInfo.CurrentCulture)} ms");
        if (run is { Status: "Failed" or "Abandoned" }) return (SourceHealthBand.Problem, "son çalıştırma başarısız" + SafeError(run.Error));
        if (string.Equals(source.LastFeedState, "INCOMPLETE", StringComparison.OrdinalIgnoreCase)) return (SourceHealthBand.Problem, "eksik akış · son aktarım tamamlanmadı");
        // #898: a source past its refresh SLA is a problem even when it is reachable.
        if (SourceSla.Evaluate(source, nowUtc) is { State: SourceSlaState.Overdue } sla) return (SourceHealthBand.Problem, "güncellik SLA: " + sla.Word);
        var everRan = source.LastRunUtc is not null || source.LastSuccessfulFeedUtc is not null || run is not null;
        if (!checkedOnce && !everRan) return (SourceHealthBand.NeverRun, "");
        return (SourceHealthBand.Healthy, "");
    }

    public static string HealthWord(string state) => state switch
    {
        "TIMEOUT" => "zaman aşımı", "AUTH_ERROR" => "yetki hatası", "RATE_LIMITED" => "istek sınırı", "SERVER_ERROR" => "sunucu hatası",
        "CLIENT_ERROR" => "istek reddedildi", "NETWORK_ERROR" => "ağ hatası", "NOT_CONFIGURED" => "adres yok", "LIVE_API_BLOCKED" => "canlı erişim kapalı", _ => state.ToLowerInvariant(),
    };

    /// <summary>A redacted reason, never a body or a trace; empty when there is nothing readable to add.</summary>
    static string SafeError(string? error)
    {
        var raw = (error ?? "").Trim();
        if (raw.Length == 0 || StatusTooltip.LooksLikeRawPayload(raw)) return "";
        var text = MarketplaceConnectionStore.RedactSecrets(AuditStore.Redact(raw)).Trim();
        if (text.Length > 120) text = text[..120] + "…";
        return " · " + text;
    }

    /// <summary>When the scheduler will next try the source, from its own interval and last run.</summary>
    public static string NextSchedule(XmlSource source, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.Enabled) return "pasif";
        if (!source.AutoImport) return "otomatik değil";
        var due = (source.LastRunUtc ?? DateTime.MinValue).AddMinutes(Math.Max(1, source.IntervalMinutes));
        if (due <= nowUtc) return "sıradaki kontrolde";
        var span = due - nowUtc;
        if (span < TimeSpan.FromMinutes(1)) return "1 dk içinde";
        if (span < TimeSpan.FromHours(1)) return $"{(int)Math.Ceiling(span.TotalMinutes)} dk sonra";
        if (span < TimeSpan.FromDays(1)) return $"{(int)Math.Ceiling(span.TotalHours)} sa sonra";
        return $"{(int)Math.Ceiling(span.TotalDays)} gün sonra";
    }

    public static string ActiveRun(SourceRunSnapshot? run, DateTime nowUtc)
    {
        if (run is not { Status: "Running" }) return "";
        var elapsed = nowUtc - run.StartedUtc; if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        var length = elapsed < TimeSpan.FromMinutes(1) ? $"{(int)elapsed.TotalSeconds} sn" : elapsed < TimeSpan.FromHours(1) ? $"{(int)elapsed.TotalMinutes} dk" : $"{(int)elapsed.TotalHours} sa";
        return run.LeaseUntilUtc is { } lease && lease < nowUtc ? $"askıda · {length}" : $"sürüyor · {length}";
    }

    public static bool Matches(SourceListEntry entry, SourceListFilter filter)
    {
        ArgumentNullException.ThrowIfNull(entry); ArgumentNullException.ThrowIfNull(filter);
        if (filter.Band is { } band && entry.Band != band) return false;
        var text = (filter.Text ?? "").Trim();
        if (text.Length == 0) return true;
        return entry.Row.Title.ContainsFolded(text) || entry.Row.MaskedLocation.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Bands in order, empty bands dropped; within a band the last-used first, then by title.</summary>
    public static IReadOnlyList<SourceListGroup> Group(IEnumerable<SourceListEntry> entries, SourceListFilter filter)
    {
        ArgumentNullException.ThrowIfNull(entries); ArgumentNullException.ThrowIfNull(filter);
        var kept = entries.Where(e => Matches(e, filter)).ToList();
        return Bands.Select(b => new SourceListGroup(b.Band, b.Label, kept.Where(e => e.Band == b.Band).OrderByDescending(e => e.Row.Recent).ThenBy(e => e.Row.Title, StringComparer.CurrentCultureIgnoreCase).ToList()))
            .Where(g => g.Entries.Count > 0).ToList();
    }

    /// <summary>Counts over everything, so the filter chips say what exists even while a filter hides it.</summary>
    public static IReadOnlyDictionary<SourceHealthBand, int> Counts(IEnumerable<SourceListEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var counts = Bands.ToDictionary(b => b.Band, _ => 0);
        foreach (var e in entries) counts[e.Band]++;
        return counts;
    }

    /// <summary>The latest record per source, as the run store's newest-first list yields it.</summary>
    public static IReadOnlyDictionary<string, SourceRunSnapshot> LatestRuns(IEnumerable<XmlRunRecord> newestFirst)
    {
        ArgumentNullException.ThrowIfNull(newestFirst);
        var latest = new Dictionary<string, SourceRunSnapshot>(StringComparer.Ordinal);
        foreach (var r in newestFirst) if (!latest.ContainsKey(r.SourceId)) latest[r.SourceId] = new(r.Status, r.StartedUtc, r.FinishedUtc, r.LeaseUntilUtc, r.Error);
        return latest;
    }
}
