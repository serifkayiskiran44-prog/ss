using Microsoft.Data.Sqlite;
using System.IO;

namespace TrMarketplaceHubDesktop;

public sealed record DatabaseHealthResult(string Status, string QuickCheck, int SchemaVersion, bool WalEnabled, bool ForeignKeysEnabled, string Detail);

public static class DatabaseHealth
{
    public static DatabaseHealthResult Inspect(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var path = Path.Combine(dataDirectory, "catalog.db");
        if (!File.Exists(path)) return new("NOT_CONFIGURED", "missing", 0, false, false, "catalog.db henüz oluşturulmadı.");
        try
        {
            var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, DefaultTimeout = 2 }.ToString();
            using var c = new SqliteConnection(cs); c.Open();
            using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA quick_check(1); PRAGMA user_version; PRAGMA journal_mode; PRAGMA foreign_keys;";
            using var r = cmd.ExecuteReader();
            r.Read(); var quick = r.GetString(0); r.NextResult(); r.Read(); var version = r.GetInt32(0); r.NextResult(); r.Read(); var wal = string.Equals(r.GetString(0), "wal", StringComparison.OrdinalIgnoreCase); r.NextResult(); r.Read(); var fk = r.GetInt32(0) == 1;
            var ok = string.Equals(quick, "ok", StringComparison.OrdinalIgnoreCase) && version <= SchemaVersion.Current;
            return new(ok ? "HEALTHY" : "BLOCKED", quick, version, wal, fk, ok ? "Salt okunur SQLite sağlık kontrolü geçti." : "Bütünlük veya şema sürümü kontrolü başarısız.");
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
        { return new("ERROR", "unavailable", 0, false, false, AuditStore.Sanitize(error.Message)); }
    }
}
