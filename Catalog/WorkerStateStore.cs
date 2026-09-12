using Microsoft.Data.Sqlite;
using System.IO;

namespace TrMarketplaceHubDesktop.Catalog;

public sealed record WorkerState(string Cursor, string? LeaseToken, DateTimeOffset? LeaseUntilUtc, string Status, string LastOutcome);

public sealed class WorkerStateStore
{
    readonly string connectionString;
    public WorkerStateStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "catalog.db"), DefaultTimeout = 15, Pooling = true }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL;PRAGMA busy_timeout=15000;CREATE TABLE IF NOT EXISTS WorkerState(Id INTEGER PRIMARY KEY CHECK(Id=1),Cursor TEXT NOT NULL,LeaseToken TEXT NULL,LeaseUntilUtc TEXT NULL,Status TEXT NOT NULL,LastOutcome TEXT NOT NULL);INSERT OR IGNORE INTO WorkerState(Id,Cursor,Status,LastOutcome) VALUES(1,'281','IDLE','');";
        cmd.ExecuteNonQuery();
    }
    SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); using var p = c.CreateCommand(); p.CommandText = "PRAGMA busy_timeout=15000"; p.ExecuteNonQuery(); return c; }
    public WorkerState Get()
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT Cursor,LeaseToken,LeaseUntilUtc,Status,LastOutcome FROM WorkerState WHERE Id=1"; using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new InvalidOperationException("Worker state bulunamadı.");
        return new(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : DateTimeOffset.Parse(r.GetString(2)), r.GetString(3), r.GetString(4));
    }
    public bool TryClaim(string owner, DateTimeOffset nowUtc, TimeSpan lease)
    {
        if (string.IsNullOrWhiteSpace(owner) || lease <= TimeSpan.Zero) throw new ArgumentException("Worker sahibi ve lease süresi zorunludur.");
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "UPDATE WorkerState SET LeaseToken=$owner,LeaseUntilUtc=$until,Status='RUNNING' WHERE Id=1 AND (LeaseUntilUtc IS NULL OR LeaseUntilUtc<$now OR LeaseToken=$owner)";
        cmd.Parameters.AddWithValue("$owner", owner); cmd.Parameters.AddWithValue("$until", nowUtc.Add(lease).ToString("O")); cmd.Parameters.AddWithValue("$now", nowUtc.ToString("O")); return cmd.ExecuteNonQuery() == 1;
    }
    public void Advance(string owner, string nextCursor, string outcome)
    {
        if (string.IsNullOrWhiteSpace(nextCursor) || string.IsNullOrWhiteSpace(outcome)) throw new ArgumentException("Cursor ve kanıt zorunludur.");
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "UPDATE WorkerState SET Cursor=$cursor,LeaseToken=NULL,LeaseUntilUtc=NULL,Status='IDLE',LastOutcome=$outcome WHERE Id=1 AND LeaseToken=$owner";
        cmd.Parameters.AddWithValue("$cursor", nextCursor); cmd.Parameters.AddWithValue("$outcome", outcome); cmd.Parameters.AddWithValue("$owner", owner); if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("Worker lease sahibi değil; cursor ilerletilemedi.");
    }
}
