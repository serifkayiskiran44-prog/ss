using Microsoft.Data.Sqlite;
using System.IO;

namespace TrMarketplaceHubDesktop;

public sealed record DatabaseHealthResult(string Status, string QuickCheck, int SchemaVersion, bool WalEnabled, bool ForeignKeysEnabled, string Detail, long WalSizeBytes = 0, bool CheckpointBusy = false);

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
            var (walSize, busy) = InspectWal(path);
            return new(ok ? "HEALTHY" : "BLOCKED", quick, version, wal, fk, ok ? "Salt okunur SQLite sağlık kontrolü geçti." : "Bütünlük veya şema sürümü kontrolü başarısız.", walSize, busy);
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
        { return new("ERROR", "unavailable", 0, false, false, AuditStore.Sanitize(error.Message)); }
    }

    // PRAGMA quick_check et al. above work on a read-only connection, but a checkpoint attempt needs write
    // access; opened as its own short-lived connection so a checkpoint failure never masks the (more
    // important) integrity result above. PASSIVE is SQLite's own non-disruptive mode -- it never blocks or
    // forces out a concurrent reader/writer, unlike RESTART/TRUNCATE/FULL, so this is safe to run against a
    // live, in-use database as part of a routine health check. No size/duration threshold is imposed here;
    // the raw byte count and busy flag are reported as-is for the caller to interpret.
    static (long WalSizeBytes, bool Busy) InspectWal(string path)
    {
        var walPath = path + "-wal";
        var walSize = File.Exists(walPath) ? new FileInfo(walPath).Length : 0;
        try
        {
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 2 }.ToString());
            c.Open();
            using var cmd = c.CreateCommand(); cmd.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
            using var r = cmd.ExecuteReader();
            // Columns: busy, log, checkpointed. "busy" alone only covers the checkpoint being unable to
            // start at all (e.g. a conflicting checkpoint already running); a long-open reader instead
            // leaves it running to completion but only partially -- checkpointed < log -- without ever
            // setting busy. Both are "blocked/needs attention" from a diagnostic's point of view.
            if (r.Read()) { var busy = r.GetInt32(0) != 0; var log = r.GetInt32(1); var checkpointed = r.GetInt32(2); return (walSize, busy || checkpointed < log); }
        }
        catch (SqliteException) { /* a checkpoint attempt failing does not invalidate the integrity check above */ }
        return (walSize, false);
    }
}
