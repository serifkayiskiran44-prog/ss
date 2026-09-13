using System.Globalization;
using Microsoft.Data.Sqlite;
using System.IO;

namespace TrMarketplaceHubDesktop;

/// <summary>A snooze on one alert: its identity and scope, the severity it had when silenced, and when the silence ends. Never the alert's words.</summary>
public sealed record AlertSnooze(string Fingerprint, string Source, string StoreKey, string SeverityAtSnooze, DateTime UntilUtc, DateTime CreatedUtc)
{
    public bool IsExpired(DateTime nowUtc) => UntilUtc <= nowUtc;
}

public sealed record AlertSnoozeOption(string Key, string Label, TimeSpan Duration);

/// <summary>
/// Snooze rules (#852): a snooze is bounded (five minutes to fourteen days, offered as a short list), it ends by
/// itself, it silences an alert only while the alert is no more severe than it was when silenced -- a new, more
/// severe event bypasses it -- and it survives a resolve/reopen cycle until it expires: the operator asked not to
/// be bothered until a time, not until the next sighting.
/// </summary>
public static class AlertSnoozeRules
{
    public static readonly TimeSpan Min = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan Max = TimeSpan.FromDays(14);
    public static readonly IReadOnlyList<AlertSnoozeOption> Options = new AlertSnoozeOption[]
    {
        new("1h", "1 saat", TimeSpan.FromHours(1)), new("4h", "4 saat", TimeSpan.FromHours(4)), new("1d", "1 gün", TimeSpan.FromDays(1)), new("3d", "3 gün", TimeSpan.FromDays(3)), new("1w", "1 hafta", TimeSpan.FromDays(7)),
    };

    public static TimeSpan Clamp(TimeSpan duration) => duration < Min ? Min : duration > Max ? Max : duration;
    public static AlertSnoozeOption? Option(string? key) => Options.FirstOrDefault(o => string.Equals(o.Key, (key ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>True while the snooze has not ended and the alert has not escalated past the severity it was snoozed at.</summary>
    public static bool Silences(AlertSnooze snooze, LocalNotification alert, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(snooze); ArgumentNullException.ThrowIfNull(alert);
        if (!string.Equals(snooze.Fingerprint, alert.Fingerprint, StringComparison.Ordinal)) return false;
        if (snooze.IsExpired(nowUtc)) return false;
        return NotificationCenter.Rank(alert.Severity) >= NotificationCenter.Rank(snooze.SeverityAtSnooze);
    }

    public static string Describe(AlertSnooze snooze, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(snooze);
        var left = snooze.UntilUtc - nowUtc;
        if (left <= TimeSpan.Zero) return "ertelemesi doldu";
        var span = left < TimeSpan.FromHours(1) ? $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} dk" : left < TimeSpan.FromDays(1) ? $"{(int)Math.Ceiling(left.TotalHours)} sa" : $"{(int)Math.Ceiling(left.TotalDays)} gün";
        return $"{span} daha ertelendi (bitiş {snooze.UntilUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)})";
    }
}

/// <summary>Where snoozes live (#852): beside the alert ledger, keyed by fingerprint, holding only identity, scope, severity and times -- restart-safe, and never a title or a detail.</summary>
public sealed class AlertSnoozeStore
{
    readonly string connectionString;
    public AlertSnoozeStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory); connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "notifications.db") }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "CREATE TABLE IF NOT EXISTS AlertSnoozes(Fingerprint TEXT PRIMARY KEY,Source TEXT NOT NULL,StoreKey TEXT NOT NULL,Severity TEXT NOT NULL,UntilUtc TEXT NOT NULL,CreatedUtc TEXT NOT NULL)"; cmd.ExecuteNonQuery();
    }
    SqliteConnection Open() => SqliteConnectionPolicy.Open(connectionString);

    /// <summary>Snoozes an alert for a bounded duration; a second snooze replaces the first.</summary>
    public AlertSnooze Snooze(string fingerprint, string source, string storeKey, string severity, TimeSpan duration, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) throw new ArgumentException("Erteleme için uyarı kimliği gerekli.", nameof(fingerprint));
        var snooze = new AlertSnooze(fingerprint.Trim(), (source ?? "").Trim(), (storeKey ?? "").Trim(), NotificationCenter.Normalize(severity), nowUtc + AlertSnoozeRules.Clamp(duration), nowUtc);
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO AlertSnoozes(Fingerprint,Source,StoreKey,Severity,UntilUtc,CreatedUtc) VALUES($fp,$src,$store,$sev,$until,$created) ON CONFLICT(Fingerprint) DO UPDATE SET Source=excluded.Source,StoreKey=excluded.StoreKey,Severity=excluded.Severity,UntilUtc=excluded.UntilUtc,CreatedUtc=excluded.CreatedUtc";
        cmd.Parameters.AddWithValue("$fp", snooze.Fingerprint); cmd.Parameters.AddWithValue("$src", snooze.Source); cmd.Parameters.AddWithValue("$store", snooze.StoreKey); cmd.Parameters.AddWithValue("$sev", snooze.SeverityAtSnooze); cmd.Parameters.AddWithValue("$until", snooze.UntilUtc.ToString("O", CultureInfo.InvariantCulture)); cmd.Parameters.AddWithValue("$created", snooze.CreatedUtc.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
        return snooze;
    }

    public void Clear(string fingerprint) { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "DELETE FROM AlertSnoozes WHERE Fingerprint=$fp"; cmd.Parameters.AddWithValue("$fp", (fingerprint ?? "").Trim()); cmd.ExecuteNonQuery(); }

    /// <summary>Every snooze still in force at <paramref name="nowUtc"/>; expired ones are dropped from the table on the way.</summary>
    public IReadOnlyList<AlertSnooze> Active(DateTime nowUtc)
    {
        using var c = Open();
        using (var purge = c.CreateCommand()) { purge.CommandText = "DELETE FROM AlertSnoozes WHERE UntilUtc<=$now"; purge.Parameters.AddWithValue("$now", nowUtc.ToString("O", CultureInfo.InvariantCulture)); purge.ExecuteNonQuery(); }
        using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT Fingerprint,Source,StoreKey,Severity,UntilUtc,CreatedUtc FROM AlertSnoozes ORDER BY UntilUtc";
        using var r = cmd.ExecuteReader(); var rows = new List<AlertSnooze>();
        while (r.Read()) rows.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), Parse(r.GetString(4)), Parse(r.GetString(5))));
        return rows;
    }

    static DateTime Parse(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
