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
    public static DateTime NextRunUtc(AutomationJob job, DateTime nowUtc)
    {
        nowUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        if (job.ScheduleMode.Equals("Interval", StringComparison.OrdinalIgnoreCase)) return nowUtc.AddMinutes(Math.Max(1, job.IntervalMinutes));
        if (!TimeSpan.TryParseExact(job.RunAtLocal, @"hh\:mm", CultureInfo.InvariantCulture, out var runAt)) throw new InvalidOperationException("Otomasyon yerel saati HH:mm olmalı.");
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, TimeZoneInfo.Local);
        for (var day = 0; day <= 370; day++)
        {
            var date = localNow.Date.AddDays(day);
            if (job.ScheduleMode.Equals("Weekly", StringComparison.OrdinalIgnoreCase) && !ParseDays(job.DaysOfWeek).Contains(date.DayOfWeek)) continue;
            var candidate = date.Add(runAt);
            if (candidate <= localNow) continue;
            if (!ApplyWindow(job, date, runAt, out var adjusted)) continue;
            candidate = adjusted;
            if (candidate <= localNow) continue;
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(candidate, DateTimeKind.Unspecified), TimeZoneInfo.Local);
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
    public AutomationJob Save(AutomationJob job)
    {
        if (job.IntervalMinutes < 1) throw new InvalidOperationException("Otomasyon aralığı en az 1 dakika olmalı."); if (string.IsNullOrWhiteSpace(job.Channel) || string.IsNullOrWhiteSpace(job.Shop)) throw new InvalidOperationException("Kanal ve mağaza zorunlu."); AutomationSchedule.Validate(job);
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "INSERT INTO AutomationJobs(Id,Kind,IntervalMinutes,NextRunUtc,LastRunUtc,LockedUntilUtc,LastError,Channel,Shop,Enabled,ScheduleMode,RunAtLocal,DaysOfWeek,WindowStartLocal,WindowEndLocal,RetryLimit,RetryBackoffMinutes,FailureCount,TemplateKey) VALUES($id,$kind,$interval,$next,$last,$lock,$error,$channel,$shop,$enabled,$schedule,$time,$days,$windowStart,$windowEnd,$retryLimit,$retryBackoff,$failures,$template) ON CONFLICT(Id) DO UPDATE SET Kind=excluded.Kind,IntervalMinutes=excluded.IntervalMinutes,NextRunUtc=excluded.NextRunUtc,LastRunUtc=excluded.LastRunUtc,LockedUntilUtc=excluded.LockedUntilUtc,LastError=excluded.LastError,Channel=excluded.Channel,Shop=excluded.Shop,Enabled=excluded.Enabled,ScheduleMode=excluded.ScheduleMode,RunAtLocal=excluded.RunAtLocal,DaysOfWeek=excluded.DaysOfWeek,WindowStartLocal=excluded.WindowStartLocal,WindowEndLocal=excluded.WindowEndLocal,RetryLimit=excluded.RetryLimit,RetryBackoffMinutes=excluded.RetryBackoffMinutes,FailureCount=excluded.FailureCount,TemplateKey=excluded.TemplateKey";
        cmd.Parameters.AddWithValue("$id", job.Id); cmd.Parameters.AddWithValue("$kind", (int)job.Kind); cmd.Parameters.AddWithValue("$interval", job.IntervalMinutes); cmd.Parameters.AddWithValue("$next", job.NextRunUtc.ToString("O", CultureInfo.InvariantCulture)); cmd.Parameters.AddWithValue("$last", job.LastRunUtc.HasValue ? job.LastRunUtc.Value.ToString("O", CultureInfo.InvariantCulture) : DBNull.Value); cmd.Parameters.AddWithValue("$lock", job.LockedUntilUtc.HasValue ? job.LockedUntilUtc.Value.ToString("O", CultureInfo.InvariantCulture) : DBNull.Value); cmd.Parameters.AddWithValue("$error", MarketplaceConnectionStore.Redact(job.LastError)); cmd.Parameters.AddWithValue("$channel", job.Channel.Trim().ToLowerInvariant()); cmd.Parameters.AddWithValue("$shop", job.Shop.Trim()); cmd.Parameters.AddWithValue("$enabled", job.Enabled ? 1 : 0); cmd.Parameters.AddWithValue("$schedule", job.ScheduleMode); cmd.Parameters.AddWithValue("$time", job.RunAtLocal); cmd.Parameters.AddWithValue("$days", job.DaysOfWeek); cmd.Parameters.AddWithValue("$windowStart", job.WindowStartLocal); cmd.Parameters.AddWithValue("$windowEnd", job.WindowEndLocal); cmd.Parameters.AddWithValue("$retryLimit", job.RetryLimit); cmd.Parameters.AddWithValue("$retryBackoff", job.RetryBackoffMinutes); cmd.Parameters.AddWithValue("$failures", job.FailureCount); cmd.Parameters.AddWithValue("$template", job.TemplateKey); cmd.ExecuteNonQuery(); return job;
    }
    public AutomationJob Get(string id) { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = Select + " WHERE Id=$id"; cmd.Parameters.AddWithValue("$id", id); using var r = cmd.ExecuteReader(); if (!r.Read()) throw new InvalidOperationException("Otomasyon işi bulunamadı."); return Read(r); }
    public IReadOnlyList<AutomationJob> List() { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = Select + " ORDER BY NextRunUtc"; using var r = cmd.ExecuteReader(); var result = new List<AutomationJob>(); while (r.Read()) result.Add(Read(r)); return result; }
    public bool TryClaim(string id, DateTime nowUtc, TimeSpan lease) { using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "UPDATE AutomationJobs SET LockedUntilUtc=$lock WHERE Id=$id AND Enabled=1 AND NextRunUtc<=$now AND (LockedUntilUtc IS NULL OR LockedUntilUtc<$now)"; cmd.Parameters.AddWithValue("$lock", (nowUtc + lease).ToString("O", CultureInfo.InvariantCulture)); cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$now", nowUtc.ToString("O", CultureInfo.InvariantCulture)); return cmd.ExecuteNonQuery() == 1; }
    public void Complete(string id, DateTime nowUtc) { var job = Get(id); using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "UPDATE AutomationJobs SET NextRunUtc=$next,LastRunUtc=$last,LockedUntilUtc=NULL,LastError='',FailureCount=0 WHERE Id=$id"; cmd.Parameters.AddWithValue("$next", AutomationSchedule.NextRunUtc(job, nowUtc).ToString("O", CultureInfo.InvariantCulture)); cmd.Parameters.AddWithValue("$last", nowUtc.ToString("O", CultureInfo.InvariantCulture)); cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery(); }
    public void Fail(string id, string error) { var job = Get(id); var failures = job.FailureCount + 1; var delay = failures <= job.RetryLimit ? TimeSpan.FromMinutes(Math.Min(1440, job.RetryBackoffMinutes * Math.Pow(2, failures - 1))) : TimeSpan.Zero; var next = delay > TimeSpan.Zero ? DateTime.UtcNow.Add(delay) : AutomationSchedule.NextRunUtc(job, DateTime.UtcNow); using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "UPDATE AutomationJobs SET NextRunUtc=$next,LockedUntilUtc=NULL,LastError=$error,FailureCount=$failures WHERE Id=$id"; cmd.Parameters.AddWithValue("$next", next.ToString("O", CultureInfo.InvariantCulture)); cmd.Parameters.AddWithValue("$error", MarketplaceConnectionStore.Redact(error)); cmd.Parameters.AddWithValue("$failures", failures); cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery(); }
    const string Select = "SELECT Id,Kind,IntervalMinutes,NextRunUtc,LastRunUtc,LockedUntilUtc,LastError,Channel,Shop,Enabled,ScheduleMode,RunAtLocal,DaysOfWeek,WindowStartLocal,WindowEndLocal,RetryLimit,RetryBackoffMinutes,FailureCount,TemplateKey FROM AutomationJobs";
    static AutomationJob Read(SqliteDataReader r) => new() { Id = r.GetString(0), Kind = (AutomationKind)r.GetInt32(1), IntervalMinutes = r.GetInt32(2), NextRunUtc = DateTime.Parse(r.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), LastRunUtc = r.IsDBNull(4) ? null : DateTime.Parse(r.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), LockedUntilUtc = r.IsDBNull(5) ? null : DateTime.Parse(r.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), LastError = r.GetString(6), Channel = r.GetString(7), Shop = r.GetString(8), Enabled = r.GetInt32(9) != 0, ScheduleMode = r.GetString(10), RunAtLocal = r.GetString(11), DaysOfWeek = r.GetString(12), WindowStartLocal = r.GetString(13), WindowEndLocal = r.GetString(14), RetryLimit = r.GetInt32(15), RetryBackoffMinutes = r.GetInt32(16), FailureCount = r.GetInt32(17), TemplateKey = r.GetString(18) };
}
