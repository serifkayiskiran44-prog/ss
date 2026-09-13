using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace TrMarketplaceHubDesktop;

public enum LatencyPhase { Load, Refresh }

/// <summary>Completed: the view showed its data. Cancelled: the operator or a shutdown stopped it. Failed: it threw. Superseded: a newer request of the same view replaced it before it could show.</summary>
public enum LatencyOutcome { Completed, Cancelled, Failed, Superseded }

/// <summary>One measured view load or refresh: when, which view, which phase, how it ended, how long, how many items, which scope id, whether the view had loaded before in this session, and a correlation id. Never entity content.</summary>
public sealed record LatencyMetric(DateTime AtUtc, string View, LatencyPhase Phase, LatencyOutcome Outcome, long DurationMs, int Count, string Scope, bool Warm, string Correlation);

public sealed record LatencySummary(string View, LatencyPhase Phase, int Samples, long MedianMs, long P95Ms, long MaxMs, int Failed, int Cancelled);

/// <summary>
/// UI render latency instrumentation (#878). Every product, order, dashboard and import screen load or refresh is
/// measured at its boundary and recorded as a structured, PII-safe metric: durations, counts, scope ids (a store key,
/// a source id) and outcomes only — a scope that is not an id is recorded as <see cref="InvalidScope"/>, never as
/// its text, and a view name that is not a fixed identifier is a programming error. A cold load is the first of its
/// view in the session, the next ones are warm. Recording is best effort: a store that cannot take the row never
/// throws into the screen. The diagnostics page shows a per-view summary and the support package carries the
/// summary and the recent rows.
/// </summary>
public static class UiLatency
{
    public const string ProductsView = "products";
    public const string OrdersView = "orders";
    public const string DashboardView = "dashboard";
    public const string ImportView = "import";
    public const string Unscoped = "all";
    public const string InvalidScope = "invalid-scope";
    public const string DiagnosticName = "Ekran gecikmesi";
    public const long SlowThresholdMs = 2000;
    static readonly Regex ViewShape = new("^[a-z][a-z0-9-]{0,31}$", RegexOptions.Compiled);
    static readonly Regex ScopeShape = new("^[A-Za-z0-9:_.-]{1,64}$", RegexOptions.Compiled);

    /// <summary>Starts a measurement; the scope's verdict (Complete / Cancel / Fail / Supersede) records the metric.</summary>
    public static LatencyScope Begin(LatencyStore store, string view, LatencyPhase phase, string? scope)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (view is null || !ViewShape.IsMatch(view)) throw new ArgumentException("Ekran adı sabit bir tanımlayıcı olmalı.", nameof(view));
        return new LatencyScope(store, view, phase, SafeScope(scope));
    }

    /// <summary>A scope is an id (store key, source id); anything else is recorded as <see cref="InvalidScope"/>, never as its text.</summary>
    public static string SafeScope(string? scope) => string.IsNullOrWhiteSpace(scope) ? Unscoped : ScopeShape.IsMatch(scope) ? scope : InvalidScope;

    public static string PhaseLabel(LatencyPhase phase) => phase == LatencyPhase.Load ? "açılış" : "yenileme";

    /// <summary>The diagnostics line: one entry per view and phase, WARN when any p95 crosses the slow threshold.</summary>
    public static DiagnosticCheck Check(IReadOnlyList<LatencySummary> summaries)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        if (summaries.Count == 0) return new DiagnosticCheck(DiagnosticName, "OK", "Henüz ölçüm yok");
        var slow = summaries.Any(s => s.P95Ms > SlowThresholdMs);
        var detail = string.Join(" | ", summaries.Select(s => $"{s.View} {PhaseLabel(s.Phase)} p50 {s.MedianMs} ms · p95 {s.P95Ms} ms · en uzun {s.MaxMs} ms ({s.Samples} ölçüm{(s.Failed > 0 ? $", {s.Failed} hata" : "")}{(s.Cancelled > 0 ? $", {s.Cancelled} iptal" : "")})"));
        return new DiagnosticCheck(DiagnosticName, slow ? "WARN" : "OK", detail);
    }
}

/// <summary>One measurement in flight: its own clock and correlation id, so views measured in parallel never share a verdict; the first verdict wins.</summary>
public sealed class LatencyScope
{
    readonly LatencyStore store; readonly string view; readonly LatencyPhase phase; readonly string scope; readonly Stopwatch clock = Stopwatch.StartNew(); readonly DateTime startedUtc = DateTime.UtcNow;
    LatencyMetric? verdict;

    internal LatencyScope(LatencyStore store, string view, LatencyPhase phase, string scope) { this.store = store; this.view = view; this.phase = phase; this.scope = scope; Correlation = Guid.NewGuid().ToString("N")[..12]; }

    public string Correlation { get; }

    public LatencyMetric Complete(int count) => Settle(LatencyOutcome.Completed, count);
    public LatencyMetric Cancel() => Settle(LatencyOutcome.Cancelled, 0);
    public LatencyMetric Fail() => Settle(LatencyOutcome.Failed, 0);
    public LatencyMetric Supersede() => Settle(LatencyOutcome.Superseded, 0);

    LatencyMetric Settle(LatencyOutcome outcome, int count)
    {
        if (verdict is not null) return verdict;
        clock.Stop();
        var warm = store.MarkSeen(view);
        verdict = new LatencyMetric(startedUtc, view, phase, outcome, clock.ElapsedMilliseconds, Math.Max(0, count), scope, warm, Correlation);
        try { store.Record(verdict); }
        catch (Exception error) { Debug.WriteLine(error.Message); } // best effort: a metric never breaks a screen
        return verdict;
    }
}

/// <summary>The metrics store (ui-latency.db): rows are appended and pruned to a retention limit; the summary groups by view and phase.</summary>
public sealed class LatencyStore
{
    public const string FileName = "ui-latency.db";
    public const int DefaultRetention = 5000;
    readonly string connectionString; readonly int retention; readonly HashSet<string> seen = new(StringComparer.Ordinal); readonly object gate = new();

    public LatencyStore(string? directory = null, int retention = DefaultRetention)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        this.retention = Math.Max(1, retention);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, FileName) }.ToString();
        using var c = Open(); using var command = c.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS UiLatency(Id INTEGER PRIMARY KEY AUTOINCREMENT,AtUtc TEXT NOT NULL,View TEXT NOT NULL,Phase TEXT NOT NULL,Outcome TEXT NOT NULL,DurationMs INTEGER NOT NULL,Count INTEGER NOT NULL,Scope TEXT NOT NULL,Warm INTEGER NOT NULL,Correlation TEXT NOT NULL)";
        command.ExecuteNonQuery();
    }

    SqliteConnection Open() => SqliteConnectionPolicy.Open(connectionString);

    /// <summary>Whether this view had a verdict before in this session (warm); marks it seen either way.</summary>
    public bool MarkSeen(string view) { lock (gate) return !seen.Add(view); }

    /// <summary>Whether this view has had a verdict in this session — the boundary's way to call its next run a refresh rather than a load.</summary>
    public bool HasSeen(string view) { lock (gate) return seen.Contains(view); }

    /// <summary>The phase for a view's next run: its first in the session is the load, every later one a refresh.</summary>
    public LatencyPhase PhaseFor(string view) => HasSeen(view) ? LatencyPhase.Refresh : LatencyPhase.Load;

    public void Record(LatencyMetric metric)
    {
        ArgumentNullException.ThrowIfNull(metric);
        using var c = Open(); using var command = c.CreateCommand();
        command.CommandText = "INSERT INTO UiLatency(AtUtc,View,Phase,Outcome,DurationMs,Count,Scope,Warm,Correlation) VALUES($at,$view,$phase,$outcome,$ms,$count,$scope,$warm,$correlation);DELETE FROM UiLatency WHERE Id NOT IN (SELECT Id FROM UiLatency ORDER BY Id DESC LIMIT $keep)";
        command.Parameters.AddWithValue("$at", metric.AtUtc.ToString("O", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$view", metric.View); command.Parameters.AddWithValue("$phase", metric.Phase.ToString()); command.Parameters.AddWithValue("$outcome", metric.Outcome.ToString());
        command.Parameters.AddWithValue("$ms", metric.DurationMs); command.Parameters.AddWithValue("$count", metric.Count); command.Parameters.AddWithValue("$scope", UiLatency.SafeScope(metric.Scope)); command.Parameters.AddWithValue("$warm", metric.Warm ? 1 : 0); command.Parameters.AddWithValue("$correlation", metric.Correlation); command.Parameters.AddWithValue("$keep", retention);
        command.ExecuteNonQuery();
    }

    /// <summary>The newest rows first, optionally for one view.</summary>
    public IReadOnlyList<LatencyMetric> List(int limit = 200, string? view = null)
    {
        using var c = Open(); using var command = c.CreateCommand();
        command.CommandText = "SELECT AtUtc,View,Phase,Outcome,DurationMs,Count,Scope,Warm,Correlation FROM UiLatency WHERE ($view IS NULL OR View=$view) ORDER BY Id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$view", (object?)view ?? DBNull.Value); command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, DefaultRetention));
        using var reader = command.ExecuteReader(); var rows = new List<LatencyMetric>();
        while (reader.Read())
            rows.Add(new LatencyMetric(DateTime.Parse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), reader.GetString(1), Enum.Parse<LatencyPhase>(reader.GetString(2)), Enum.Parse<LatencyOutcome>(reader.GetString(3)), reader.GetInt64(4), reader.GetInt32(5), reader.GetString(6), reader.GetInt32(7) == 1, reader.GetString(8)));
        return rows;
    }

    /// <summary>Per view and phase over the retained rows: sample count, median, p95, maximum, failures and cancellations (durations of completed and superseded rows; every row counts toward failures and cancellations).</summary>
    public IReadOnlyList<LatencySummary> Summary()
    {
        var rows = List(DefaultRetention);
        return rows.GroupBy(r => (r.View, r.Phase)).OrderBy(g => g.Key.View).ThenBy(g => g.Key.Phase).Select(g =>
        {
            var durations = g.Where(r => r.Outcome is LatencyOutcome.Completed or LatencyOutcome.Superseded).Select(r => r.DurationMs).OrderBy(d => d).ToList();
            return new LatencySummary(g.Key.View, g.Key.Phase, durations.Count, Percentile(durations, 0.5), Percentile(durations, 0.95), durations.Count == 0 ? 0 : durations[^1], g.Count(r => r.Outcome == LatencyOutcome.Failed), g.Count(r => r.Outcome == LatencyOutcome.Cancelled));
        }).ToList();
    }

    /// <summary>The nearest-rank percentile of sorted durations: the value at ceil(q·n), 0 for no samples.</summary>
    public static long Percentile(IReadOnlyList<long> sorted, double quantile)
    {
        ArgumentNullException.ThrowIfNull(sorted);
        if (sorted.Count == 0) return 0;
        var rank = (int)Math.Ceiling(Math.Clamp(quantile, 0, 1) * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }
}
