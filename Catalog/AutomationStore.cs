using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;

namespace TrMarketplaceHubDesktop.Catalog;

public enum AutomationKind { Stock = 0, Price = 1, Xml = 2, Health = 3, Sync = 4 }

public sealed class AutomationJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public AutomationKind Kind { get; set; }
    public int IntervalMinutes { get; set; } = 30;
    public DateTime NextRunUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastRunUtc { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    public string LastError { get; set; } = "";
    public string Channel { get; set; } = "etsy";
    public string Shop { get; set; } = "default";
    public bool Enabled { get; set; } = true;
    public string ScheduleMode { get; set; } = "Interval";
    public string RunAtLocal { get; set; } = "09:00";
    public string DaysOfWeek { get; set; } = "Monday";
    public string WindowStartLocal { get; set; } = "";
    public string WindowEndLocal { get; set; } = "";
    public int RetryLimit { get; set; } = 3;
    public int RetryBackoffMinutes { get; set; } = 5;
    public int FailureCount { get; set; }
    public string TemplateKey { get; set; } = "";
}

public sealed record AutomationTemplate(string Key, string Name, AutomationKind Kind, int IntervalMinutes, string ScheduleMode = "Interval");

/// Bounded diagnostics only (id, a short reason, detection time) - never LastError,
/// Channel, Shop, or TemplateKey - for an AutomationJobs row with an unparsable
/// persisted timestamp. See CatalogStore's CorruptProductRow for the same pattern.
public sealed record CorruptAutomationJob(string Id, string Reason, DateTime DetectedUtc);

/// Raised by Get(id) when the row exists but has a corrupt timestamp - kept
/// distinct from the "not found" InvalidOperationException so a caller (and a
/// human reading a stack trace) never confuses "no such job" with "job exists but
/// its schedule state is unreadable and needs explicit repair".
public sealed class AutomationJobCorruptException : Exception
{
    public string JobId { get; }
    public AutomationJobCorruptException(string jobId, string reason) : base($"Otomasyon işi bozuk (REVIEW_REQUIRED): {reason}") => JobId = jobId;
}

public static class AutomationTemplateCatalog
{
    public static IReadOnlyList<AutomationTemplate> All { get; } = Array.AsReadOnly(new[]
    {
        new AutomationTemplate("xml-refresh", "XML yenileme", AutomationKind.Xml, 30),
        new AutomationTemplate("stock-sync", "Stok senkronizasyonu", AutomationKind.Stock, 15),
        new AutomationTemplate("price-sync", "Fiyat senkronizasyonu", AutomationKind.Price, 30),
        new AutomationTemplate("health-check", "Bağlantı sağlık kontrolü", AutomationKind.Health, 15),
        new AutomationTemplate("normal-sync", "Normal sync", AutomationKind.Sync, 15)
    });
}

public static class AutomationSchedule
{
    public static bool WindowContains(string startLocal, string endLocal, TimeSpan time)
    {
        if (string.IsNullOrWhiteSpace(startLocal) && string.IsNullOrWhiteSpace(endLocal)) return true;
        if (!TimeSpan.TryParseExact(startLocal, @"hh\:mm", CultureInfo.InvariantCulture, out var start) ||
            !TimeSpan.TryParseExact(endLocal, @"hh\:mm", CultureInfo.InvariantCulture, out var end)) return false;
        return start <= end ? time >= start && time <= end : time >= start || time <= end;
    }

    /// Technical upper bound only - large enough for any real interval, small
    /// enough that nowUtc.AddMinutes(...) can never overflow DateTime's range
    /// even starting from a nowUtc close to DateTime.MaxValue. See #2531.
    public const int MaxIntervalMinutes = 129_600; // 90 days
    /// timeZone defaults to TimeZoneInfo.Local for every real caller; the
    /// parameter exists so DST spring-forward/fall-back behavior can be tested
    /// deterministically against a zone that actually observes DST, regardless
    /// of the host machine's own local zone (e.g. Turkey Standard Time, which
    /// has observed no DST since 2016). See #2637.
    public static DateTime NextRunUtc(AutomationJob job, DateTime nowUtc, TimeZoneInfo? timeZone = null)
    {
        var zone = timeZone ?? TimeZoneInfo.Local;
        nowUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        if (job.ScheduleMode.Equals("Interval", StringComparison.OrdinalIgnoreCase))
        {
            try { return nowUtc.AddMinutes(Math.Max(1, job.IntervalMinutes)); }
            catch (ArgumentOutOfRangeException) { throw new InvalidOperationException("Bir sonraki çalışma zamanı hesaplanamadı; sistem saati veya aralık desteklenen tarih sınırına çok yakın."); }
        }
        if (!TimeSpan.TryParseExact(job.RunAtLocal, @"hh\:mm", CultureInfo.InvariantCulture, out var runAt)) throw new InvalidOperationException("Otomasyon yerel saati HH:mm olmalı.");
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone);
        for (var day = 0; day <= 370; day++)
        {
            var date = localNow.Date.AddDays(day);
            if (job.ScheduleMode.Equals("Weekly", StringComparison.OrdinalIgnoreCase) && !ParseDays(job.DaysOfWeek).Contains(date.DayOfWeek)) continue;
            var candidate = date.Add(runAt);
            if (candidate <= localNow) continue;
            if (!ApplyWindow(job, date, runAt, out var adjusted)) continue;
            candidate = adjusted;
            if (candidate <= localNow) continue;
            // A DST spring-forward gap makes this local instant not exist at all -
            // ConvertTimeToUtc would throw. Policy: skip this occurrence entirely
            // and let the day-scan try the next day, rather than crashing the
            // scheduler poll or guessing an adjacent instant. See #2637.
            var candidateUnspecified = DateTime.SpecifyKind(candidate, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(candidateUnspecified)) continue;
            // A DST fall-back local instant is ambiguous (it occurs twice, under
            // two different UTC offsets). Policy: deterministically resolve to the
            // earlier of the two UTC instants (the larger/daylight offset) every
            // time - restart-stable and never produces two runs for the one local
            // wall-clock moment.
            if (zone.IsAmbiguousTime(candidateUnspecified))
            {
                var offsets = zone.GetAmbiguousTimeOffsets(candidateUnspecified);
                var earliestOffset = offsets.Max();
                return new DateTimeOffset(candidateUnspecified, earliestOffset).UtcDateTime;
            }
            return TimeZoneInfo.ConvertTimeToUtc(candidateUnspecified, zone);
        }
        throw new InvalidOperationException("Otomasyon takviminde uygun bir sonraki çalışma bulunamadı.");
    }

    public static void Validate(AutomationJob job)
    {
        if (job.ScheduleMode is not ("Interval" or "Daily" or "Weekly")) throw new InvalidOperationException("Zamanlama Interval, Daily veya Weekly olmalı.");
        if (!TimeSpan.TryParseExact(job.RunAtLocal, @"hh\:mm", CultureInfo.InvariantCulture, out _)) throw new InvalidOperationException("Yerel çalışma saati HH:mm olmalı.");
        if (job.ScheduleMode == "Weekly" && ParseDays(job.DaysOfWeek).Count == 0) throw new InvalidOperationException("Haftalık otomasyon için en az bir gün seçin.");
        if (job.WindowStartLocal.Length > 0 && !TimeSpan.TryParseExact(job.WindowStartLocal, @"hh\:mm", CultureInfo.InvariantCulture, out _)) throw new InvalidOperationException("Pencere başlangıcı HH:mm olmalı.");
        if (job.WindowEndLocal.Length > 0 && !TimeSpan.TryParseExact(job.WindowEndLocal, @"hh\:mm", CultureInfo.InvariantCulture, out _)) throw new InvalidOperationException("Pencere bitişi HH:mm olmalı.");
        if (job.RetryLimit is < 0 or > 10 || job.RetryBackoffMinutes is < 1 or > 1440) throw new InvalidOperationException("Retry limiti 0–10, backoff 1–1440 dakika olmalı.");
    }

    static bool ApplyWindow(AutomationJob job, DateTime date, TimeSpan runAt, out DateTime adjusted)
    {
        adjusted = date.Add(runAt);
        if (job.WindowStartLocal.Length == 0 && job.WindowEndLocal.Length == 0) return true;
        if (!TimeSpan.TryParseExact(job.WindowStartLocal, @"hh\:mm", CultureInfo.InvariantCulture, out var start) || !TimeSpan.TryParseExact(job.WindowEndLocal, @"hh\:mm", CultureInfo.InvariantCulture, out var end)) return false;
        if (start <= end)
        {
            if (runAt < start) adjusted = date.Add(start);
            else if (runAt > end) return false;
            return true;
        }
        return runAt >= start || runAt <= end;
    }

    static HashSet<DayOfWeek> ParseDays(string value)
    {
        var result = new HashSet<DayOfWeek>();
        foreach (var item in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) if (Enum.TryParse<DayOfWeek>(item, true, out var day)) result.Add(day);
        return result;
    }
}

public sealed class AutomationStore
{
    readonly string connectionString;
    public AutomationStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop"); Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "catalog.db") }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "CREATE TABLE IF NOT EXISTS AutomationJobs(Id TEXT PRIMARY KEY, Kind INTEGER NOT NULL, IntervalMinutes INTEGER NOT NULL, NextRunUtc TEXT NOT NULL, LastRunUtc TEXT NULL, LockedUntilUtc TEXT NULL, LastError TEXT NOT NULL, Channel TEXT NOT NULL DEFAULT 'etsy', Shop TEXT NOT NULL DEFAULT 'default', Enabled INTEGER NOT NULL DEFAULT 1)"; cmd.ExecuteNonQuery();
        EnsureColumn(c, "LastRunUtc", "TEXT NULL"); EnsureColumn(c, "Channel", "TEXT NOT NULL DEFAULT 'etsy'"); EnsureColumn(c, "Shop", "TEXT NOT NULL DEFAULT 'default'"); EnsureColumn(c, "Enabled", "INTEGER NOT NULL DEFAULT 1"); EnsureColumn(c, "ScheduleMode", "TEXT NOT NULL DEFAULT 'Interval'"); EnsureColumn(c, "RunAtLocal", "TEXT NOT NULL DEFAULT '09:00'"); EnsureColumn(c, "DaysOfWeek", "TEXT NOT NULL DEFAULT 'Monday'"); EnsureColumn(c, "WindowStartLocal", "TEXT NOT NULL DEFAULT ''"); EnsureColumn(c, "WindowEndLocal", "TEXT NOT NULL DEFAULT ''"); EnsureColumn(c, "RetryLimit", "INTEGER NOT NULL DEFAULT 3"); EnsureColumn(c, "RetryBackoffMinutes", "INTEGER NOT NULL DEFAULT 5"); EnsureColumn(c, "FailureCount", "INTEGER NOT NULL DEFAULT 0"); EnsureColumn(c, "TemplateKey", "TEXT NOT NULL DEFAULT ''");
    }
    static void EnsureColumn(SqliteConnection c, string name, string definition) { using var check = c.CreateCommand(); check.CommandText = "SELECT 1 FROM pragma_table_info('AutomationJobs') WHERE name=$name"; check.Parameters.AddWithValue("$name", name); if (check.ExecuteScalar() is not null) return; using var add = c.CreateCommand(); add.CommandText = $"ALTER TABLE AutomationJobs ADD COLUMN {name} {definition}"; add.ExecuteNonQuery(); }
    SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); return c; }
    /// A closed set, not a raw cast target - an unrecognized/future/corrupt
    /// persisted value must never be silently treated as any of these. See
    /// #2530.
    static bool IsValidKind(AutomationKind kind) => kind is AutomationKind.Stock or AutomationKind.Price or AutomationKind.Xml or AutomationKind.Health or AutomationKind.Sync;
    public AutomationJob Save(AutomationJob job)
    {
        if (!IsValidKind(job.Kind)) throw new InvalidOperationException($"Tanımsız otomasyon türü: {(int)job.Kind}.");
        if (job.IntervalMinutes is < 1 or > AutomationSchedule.MaxIntervalMinutes) throw new InvalidOperationException($"Otomasyon aralığı 1–{AutomationSchedule.MaxIntervalMinutes} dakika arasında olmalı."); if (string.IsNullOrWhiteSpace(job.Channel) || string.IsNullOrWhiteSpace(job.Shop)) throw new InvalidOperationException("Kanal ve mağaza zorunlu."); AutomationSchedule.Validate(job);
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "INSERT INTO AutomationJobs(Id,Kind,IntervalMinutes,NextRunUtc,LastRunUtc,LockedUntilUtc,LastError,Channel,Shop,Enabled,ScheduleMode,RunAtLocal,DaysOfWeek,WindowStartLocal,WindowEndLocal,RetryLimit,RetryBackoffMinutes,FailureCount,TemplateKey) VALUES($id,$kind,$interval,$next,$last,$lock,$error,$channel,$shop,$enabled,$schedule,$time,$days,$windowStart,$windowEnd,$retryLimit,$retryBackoff,$failures,$template) ON CONFLICT(Id) DO UPDATE SET Kind=excluded.Kind,IntervalMinutes=excluded.IntervalMinutes,NextRunUtc=excluded.NextRunUtc,LastRunUtc=excluded.LastRunUtc,LockedUntilUtc=excluded.LockedUntilUtc,LastError=excluded.LastError,Channel=excluded.Channel,Shop=excluded.Shop,Enabled=excluded.Enabled,ScheduleMode=excluded.ScheduleMode,RunAtLocal=excluded.RunAtLocal,DaysOfWeek=excluded.DaysOfWeek,WindowStartLocal=excluded.WindowStartLocal,WindowEndLocal=excluded.WindowEndLocal,RetryLimit=excluded.RetryLimit,RetryBackoffMinutes=excluded.RetryBackoffMinutes,FailureCount=excluded.FailureCount,TemplateKey=excluded.TemplateKey";
        cmd.Parameters.AddWithValue("$id", job.Id); cmd.Parameters.AddWithValue("$kind", (int)job.Kind); cmd.Parameters.AddWithValue("$interval", job.IntervalMinutes); cmd.Parameters.AddWithValue("$next", job.NextRunUtc.ToString("O", CultureInfo.InvariantCulture)); cmd.Parameters.AddWithValue("$last", job.LastRunUtc.HasValue ? job.LastRunUtc.Value.ToString("O", CultureInfo.InvariantCulture) : DBNull.Value); cmd.Parameters.AddWithValue("$lock", job.LockedUntilUtc.HasValue ? job.LockedUntilUtc.Value.ToString("O", CultureInfo.InvariantCulture) : DBNull.Value); cmd.Parameters.AddWithValue("$error", MarketplaceConnectionStore.Redact(job.LastError)); cmd.Parameters.AddWithValue("$channel", job.Channel.Trim().ToLowerInvariant()); cmd.Parameters.AddWithValue("$shop", job.Shop.Trim()); cmd.Parameters.AddWithValue("$enabled", job.Enabled ? 1 : 0); cmd.Parameters.AddWithValue("$schedule", job.ScheduleMode); cmd.Parameters.AddWithValue("$time", job.RunAtLocal); cmd.Parameters.AddWithValue("$days", job.DaysOfWeek); cmd.Parameters.AddWithValue("$windowStart", job.WindowStartLocal); cmd.Parameters.AddWithValue("$windowEnd", job.WindowEndLocal); cmd.Parameters.AddWithValue("$retryLimit", job.RetryLimit); cmd.Parameters.AddWithValue("$retryBackoff", job.RetryBackoffMinutes); cmd.Parameters.AddWithValue("$failures", job.FailureCount); cmd.Parameters.AddWithValue("$template", job.TemplateKey); cmd.ExecuteNonQuery(); return job;
    }
    public AutomationJob Get(string id)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = Select + " WHERE Id=$id"; cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new InvalidOperationException("Otomasyon işi bulunamadı.");
        if (!TryRead(r, out var job, out var corrupt)) throw new AutomationJobCorruptException(id, corrupt!.Reason);
        return job!;
    }
    /// A malformed NextRunUtc/LastRunUtc/LockedUntilUtc must never crash the whole
    /// read - the row is excluded from the healthy result and reported only via
    /// CorruptJobs(); detection re-runs from the row's own stored text every call,
    /// so it stays stable across a restart without a separate tracking table.
    public IReadOnlyList<AutomationJob> List() { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = Select + " ORDER BY NextRunUtc"; using var r = cmd.ExecuteReader(); var result = new List<AutomationJob>(); while (r.Read()) if (TryRead(r, out var job, out _)) result.Add(job!); return result; }
    /// Bounded diagnostics for every row with an unparsable timestamp - never the
    /// raw LastError/Channel/Shop/TemplateKey.
    public IReadOnlyList<CorruptAutomationJob> CorruptJobs() { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = Select; using var r = cmd.ExecuteReader(); var result = new List<CorruptAutomationJob>(); while (r.Read()) if (!TryRead(r, out _, out var corrupt)) result.Add(corrupt!); return result; }
    /// A corrupt job is fail-closed here, not fail-open: it is silently excluded
    /// from the due set rather than running with a fabricated/default schedule.
    public IReadOnlyList<AutomationJob> Due(DateTime nowUtc)
    {
        nowUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = Select + " WHERE Enabled=1 AND NextRunUtc<=$now AND (LockedUntilUtc IS NULL OR LockedUntilUtc<$now) ORDER BY NextRunUtc";
        cmd.Parameters.AddWithValue("$now", nowUtc.ToString("O", CultureInfo.InvariantCulture));
        using var r = cmd.ExecuteReader(); var result = new List<AutomationJob>(); while (r.Read()) if (TryRead(r, out var job, out _)) result.Add(job!); return result;
    }
    /// Re-checked before the claim UPDATE (not just relied on via Due()) so a
    /// caller that claims by id directly - bypassing Due()'s own filtering - can
    /// never lock/lease a row whose schedule state SQLite's plain TEXT comparison
    /// might otherwise happen to match despite it being unparsable/corrupt.
    bool RowIsHealthy(string id) { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = Select + " WHERE Id=$id"; cmd.Parameters.AddWithValue("$id", id); using var r = cmd.ExecuteReader(); return r.Read() && TryRead(r, out _, out _); }
    public bool TryClaim(string id, DateTime nowUtc, TimeSpan lease) { if (!RowIsHealthy(id)) return false; using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "UPDATE AutomationJobs SET LockedUntilUtc=$lock WHERE Id=$id AND Enabled=1 AND NextRunUtc<=$now AND (LockedUntilUtc IS NULL OR LockedUntilUtc<$now)"; cmd.Parameters.AddWithValue("$lock", (nowUtc + lease).ToString("O", CultureInfo.InvariantCulture)); cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$now", nowUtc.ToString("O", CultureInfo.InvariantCulture)); return cmd.ExecuteNonQuery() == 1; }
    public bool TryClaimLease(string id, DateTime nowUtc, TimeSpan lease, out string leaseToken)
    {
        if (lease <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lease));
        leaseToken = ""; if (!RowIsHealthy(id)) return false;
        leaseToken = Guid.NewGuid().ToString("N"); using var c = Open(); EnsureColumn(c, "LeaseToken", "TEXT NULL"); using var cmd = c.CreateCommand(); cmd.CommandText = "UPDATE AutomationJobs SET LockedUntilUtc=$lock,LeaseToken=$token WHERE Id=$id AND Enabled=1 AND NextRunUtc<=$now AND (LockedUntilUtc IS NULL OR LockedUntilUtc<$now)"; cmd.Parameters.AddWithValue("$lock", DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc).Add(lease).ToString("O", CultureInfo.InvariantCulture)); cmd.Parameters.AddWithValue("$token", leaseToken); cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$now", DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture)); if (cmd.ExecuteNonQuery() == 1) return true; leaseToken = ""; return false;
    }
    public void Complete(string id, DateTime nowUtc) { var job = Get(id); using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "UPDATE AutomationJobs SET NextRunUtc=$next,LastRunUtc=$last,LockedUntilUtc=NULL,LastError='',FailureCount=0 WHERE Id=$id"; cmd.Parameters.AddWithValue("$next", AutomationSchedule.NextRunUtc(job, nowUtc).ToString("O", CultureInfo.InvariantCulture)); cmd.Parameters.AddWithValue("$last", nowUtc.ToString("O", CultureInfo.InvariantCulture)); cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery(); }
    public void Complete(string id, DateTime nowUtc, string leaseToken) { var job = Get(id); using var c = Open(); EnsureColumn(c, "LeaseToken", "TEXT NULL"); using var cmd = c.CreateCommand(); cmd.CommandText = "UPDATE AutomationJobs SET NextRunUtc=$next,LastRunUtc=$last,LockedUntilUtc=NULL,LeaseToken=NULL,LastError='',FailureCount=0 WHERE Id=$id AND LeaseToken=$token"; cmd.Parameters.AddWithValue("$next", AutomationSchedule.NextRunUtc(job, nowUtc).ToString("O", CultureInfo.InvariantCulture)); cmd.Parameters.AddWithValue("$last", nowUtc.ToString("O", CultureInfo.InvariantCulture)); cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$token", leaseToken); if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("Otomasyon lease'i artık bu çalışmaya ait değil."); }
    public void Fail(string id, string error) { var job = Get(id); var failures = job.FailureCount + 1; var delay = failures <= job.RetryLimit ? TimeSpan.FromMinutes(Math.Min(1440, job.RetryBackoffMinutes * Math.Pow(2, failures - 1))) : TimeSpan.Zero; var next = delay > TimeSpan.Zero ? DateTime.UtcNow.Add(delay) : AutomationSchedule.NextRunUtc(job, DateTime.UtcNow); using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "UPDATE AutomationJobs SET NextRunUtc=$next,LockedUntilUtc=NULL,LastError=$error,FailureCount=$failures WHERE Id=$id"; cmd.Parameters.AddWithValue("$next", next.ToString("O", CultureInfo.InvariantCulture)); cmd.Parameters.AddWithValue("$error", MarketplaceConnectionStore.Redact(error)); cmd.Parameters.AddWithValue("$failures", failures); cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery(); }
    public void Fail(string id, string error, string leaseToken) { var job = Get(id); var failures = job.FailureCount + 1; var delay = failures <= job.RetryLimit ? TimeSpan.FromMinutes(Math.Min(1440, job.RetryBackoffMinutes * Math.Pow(2, failures - 1))) : TimeSpan.Zero; var next = delay > TimeSpan.Zero ? DateTime.UtcNow.Add(delay) : AutomationSchedule.NextRunUtc(job, DateTime.UtcNow); using var c = Open(); EnsureColumn(c, "LeaseToken", "TEXT NULL"); using var cmd = c.CreateCommand(); cmd.CommandText = "UPDATE AutomationJobs SET NextRunUtc=$next,LockedUntilUtc=NULL,LeaseToken=NULL,LastError=$error,FailureCount=$failures WHERE Id=$id AND LeaseToken=$token"; cmd.Parameters.AddWithValue("$next", next.ToString("O", CultureInfo.InvariantCulture)); cmd.Parameters.AddWithValue("$error", MarketplaceConnectionStore.Redact(error)); cmd.Parameters.AddWithValue("$failures", failures); cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$token", leaseToken); if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("Otomasyon lease'i artık bu çalışmaya ait değil."); }
    const string Select = "SELECT Id,Kind,IntervalMinutes,NextRunUtc,LastRunUtc,LockedUntilUtc,LastError,Channel,Shop,Enabled,ScheduleMode,RunAtLocal,DaysOfWeek,WindowStartLocal,WindowEndLocal,RetryLimit,RetryBackoffMinutes,FailureCount,TemplateKey FROM AutomationJobs";
    static bool TryRead(SqliteDataReader r, out AutomationJob? job, out CorruptAutomationJob? corrupt)
    {
        job = null; corrupt = null; var id = r.GetString(0);
        if (!TryParseUtc(r.GetString(3), out var nextRun)) { corrupt = new(id, "Malformed NextRunUtc timestamp", DateTime.UtcNow); return false; }
        DateTime? lastRun = null;
        if (!r.IsDBNull(4)) { if (!TryParseUtc(r.GetString(4), out var value)) { corrupt = new(id, "Malformed LastRunUtc timestamp", DateTime.UtcNow); return false; } lastRun = value; }
        DateTime? lockedUntil = null;
        if (!r.IsDBNull(5)) { if (!TryParseUtc(r.GetString(5), out var value)) { corrupt = new(id, "Malformed LockedUntilUtc timestamp", DateTime.UtcNow); return false; } lockedUntil = value; }
        var rawKind = r.GetInt32(1); var kind = (AutomationKind)rawKind;
        if (!IsValidKind(kind)) { corrupt = new(id, $"Unrecognized Kind value: {rawKind}", DateTime.UtcNow); return false; }
        var interval = r.GetInt32(2);
        if (interval is < 1 or > AutomationSchedule.MaxIntervalMinutes) { corrupt = new(id, $"Out-of-bounds IntervalMinutes: {interval}", DateTime.UtcNow); return false; }
        job = new() { Id = id, Kind = kind, IntervalMinutes = interval, NextRunUtc = nextRun, LastRunUtc = lastRun, LockedUntilUtc = lockedUntil, LastError = r.GetString(6), Channel = r.GetString(7), Shop = r.GetString(8), Enabled = r.GetInt32(9) != 0, ScheduleMode = r.GetString(10), RunAtLocal = r.GetString(11), DaysOfWeek = r.GetString(12), WindowStartLocal = r.GetString(13), WindowEndLocal = r.GetString(14), RetryLimit = r.GetInt32(15), RetryBackoffMinutes = r.GetInt32(16), FailureCount = r.GetInt32(17), TemplateKey = r.GetString(18) };
        return true;
    }
    /// Only ever a format/parse failure - never conflated with a DB-busy/locked
    /// SqliteException, which is raised by the surrounding command, not this parse.
    static bool TryParseUtc(string value, out DateTime result) => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out result);
}
