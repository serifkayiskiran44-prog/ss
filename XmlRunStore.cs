using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;

namespace TrMarketplaceHubDesktop;

/// <summary>XML kaynaklarının manuel ve zamanlanmış çalıştırma geçmişini tutar.</summary>
public sealed record XmlRunRecord(
    string Id,
    string SourceId,
    string Status,
    DateTime StartedUtc,
    DateTime? FinishedUtc,
    int Added,
    int Updated,
    int Unchanged,
    string Error,
    DateTime? LeaseUntilUtc = null,
    string FeedHash = "",
    int SourceRevision = 0);

public sealed class XmlRunStore
{
    readonly string connectionString;

    public XmlRunStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, "catalog.db")
        }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS XmlRuns(
                Id TEXT PRIMARY KEY,
                SourceId TEXT NOT NULL,
                Status TEXT NOT NULL,
                StartedUtc TEXT NOT NULL,
                FinishedUtc TEXT NULL,
                Added INTEGER NOT NULL DEFAULT 0,
                Updated INTEGER NOT NULL DEFAULT 0,
                Unchanged INTEGER NOT NULL DEFAULT 0,
                Error TEXT NOT NULL DEFAULT '',
                LeaseUntilUtc TEXT NULL,
                FeedHash TEXT NOT NULL DEFAULT '')
            """;
        command.ExecuteNonQuery();
        AddColumnIfMissing(connection, "LeaseUntilUtc", "TEXT NULL");
        AddColumnIfMissing(connection, "SourceRevision", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "FeedHash", "TEXT NOT NULL DEFAULT ''");
        using var index = connection.CreateCommand();
        index.CommandText = "CREATE INDEX IF NOT EXISTS IX_XmlRuns_SourceStarted ON XmlRuns(SourceId,StartedUtc DESC);CREATE UNIQUE INDEX IF NOT EXISTS UX_XmlRuns_OneRunningSource ON XmlRuns(SourceId) WHERE Status='Running'";
        index.ExecuteNonQuery();
    }

    SqliteConnection Open()
    {
        return SqliteConnectionPolicy.Open(connectionString);
    }

    public string Start(string sourceId) => Start(sourceId, "", null);

    public string Start(string sourceId, string feedHash, TimeSpan? lease, int sourceRevision = 0)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("XML kaynak kimliği zorunludur.", nameof(sourceId));
        var effectiveLease = lease ?? TimeSpan.FromMinutes(10);
        if (effectiveLease <= TimeSpan.Zero || effectiveLease > TimeSpan.FromHours(24)) throw new ArgumentOutOfRangeException(nameof(lease), "Lease süresi 24 saatten kısa ve pozitif olmalı.");
        RecoverAbandonedRunning(effectiveLease);
        var id = Guid.NewGuid().ToString("N");
        using var connection = Open();
        using (var running = connection.CreateCommand())
        {
            running.CommandText = "SELECT 1 FROM XmlRuns WHERE SourceId=$source AND Status='Running' LIMIT 1";
            running.Parameters.AddWithValue("$source", sourceId.Trim());
            if (running.ExecuteScalar() is not null) throw new InvalidOperationException("Bu XML kaynağı zaten çalışıyor; paralel ikinci çalışma engellendi.");
        }
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO XmlRuns(Id,SourceId,Status,StartedUtc,LeaseUntilUtc,FeedHash,SourceRevision) VALUES($id,$source,'Running',$started,$lease,$hash,$rev)"; command.Parameters.AddWithValue("$rev", Math.Max(0, sourceRevision));
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$source", sourceId.Trim());
        var started = DateTime.UtcNow; command.Parameters.AddWithValue("$started", started.ToString("O", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$lease", started.Add(effectiveLease).ToString("O", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$hash", feedHash ?? "");
        try { command.ExecuteNonQuery(); }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        { throw new InvalidOperationException("Bu XML kaynağı zaten çalışıyor; paralel ikinci çalışma engellendi."); }
        return id;
    }

    public bool Heartbeat(string id, TimeSpan lease)
    {
        if (lease <= TimeSpan.Zero || lease > TimeSpan.FromHours(24)) throw new ArgumentOutOfRangeException(nameof(lease));
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "UPDATE XmlRuns SET LeaseUntilUtc=$lease WHERE Id=$id AND Status='Running'"; command.Parameters.AddWithValue("$lease", DateTime.UtcNow.Add(lease).ToString("O", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$id", id); return command.ExecuteNonQuery() == 1;
    }

    public int RecoverAbandonedRunning(TimeSpan lease, DateTime? nowUtc = null)
    {
        if (lease <= TimeSpan.Zero || lease > TimeSpan.FromHours(24)) throw new ArgumentOutOfRangeException(nameof(lease));
        var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime(); using var connection = Open(); using var command = connection.CreateCommand();
        // When a clock is supplied (tests/recovery tooling), use the recorded start +
        // lease so a deterministic clock jump cannot be hidden by a future local clock.
        command.CommandText = nowUtc.HasValue
            ? "UPDATE XmlRuns SET Status='Abandoned',FinishedUtc=$now,Error='Lease süresi doldu; çalışma yeniden alınabilir.',LeaseUntilUtc=NULL WHERE Status='Running' AND datetime(StartedUtc) <= datetime($deadline)"
            : "UPDATE XmlRuns SET Status='Abandoned',FinishedUtc=$now,Error='Lease süresi doldu; çalışma yeniden alınabilir.',LeaseUntilUtc=NULL WHERE Status='Running' AND datetime(COALESCE(LeaseUntilUtc, datetime(StartedUtc, '+' || $lease || ' seconds'))) <= datetime($now)";
        command.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture)); if (nowUtc.HasValue) command.Parameters.AddWithValue("$deadline", now.Subtract(lease).ToString("O", CultureInfo.InvariantCulture)); else command.Parameters.AddWithValue("$lease", lease.TotalSeconds);
        return command.ExecuteNonQuery();
    }

    public void Complete(string id, Catalog.ImportSummary summary)
    {
        Update(id, "Succeeded", summary.Added, summary.Updated, summary.Unchanged, "");
    }

    public void Fail(string id, string error)
    {
        Update(id, "Failed", 0, 0, 0, string.IsNullOrWhiteSpace(error) ? "Bilinmeyen XML hatası." : MarketplaceConnectionStore.Redact(error));
    }

    void Update(string id, string status, int added, int updated, int unchanged, string error)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Çalıştırma kimliği zorunludur.", nameof(id));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE XmlRuns SET Status=$status, FinishedUtc=$finished,
                Added=$added, Updated=$updated, Unchanged=$unchanged, Error=$error, LeaseUntilUtc=NULL
            WHERE Id=$id AND Status='Running'
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$finished", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$added", added);
        command.Parameters.AddWithValue("$updated", updated);
        command.Parameters.AddWithValue("$unchanged", unchanged);
        command.Parameters.AddWithValue("$error", error);
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("XML çalıştırması bulunamadı veya zaten tamamlandı.");
    }

    public IReadOnlyList<XmlRunRecord> List(string? sourceId = null, int limit = 100)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit), "Geçmiş limiti 1–1000 arasında olmalı.");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sourceId is null
            ? "SELECT Id,SourceId,Status,StartedUtc,FinishedUtc,Added,Updated,Unchanged,Error,LeaseUntilUtc,FeedHash,SourceRevision FROM XmlRuns ORDER BY StartedUtc DESC LIMIT $limit"
            : "SELECT Id,SourceId,Status,StartedUtc,FinishedUtc,Added,Updated,Unchanged,Error,LeaseUntilUtc,FeedHash,SourceRevision FROM XmlRuns WHERE SourceId=$source ORDER BY StartedUtc DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        if (sourceId is not null) command.Parameters.AddWithValue("$source", sourceId);
        using var reader = command.ExecuteReader();
        var rows = new List<XmlRunRecord>();
        while (reader.Read())
        {
            rows.Add(new XmlRunRecord(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                ParseUtc(reader.GetString(3)), reader.IsDBNull(4) ? null : ParseUtc(reader.GetString(4)),
                reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7), reader.GetString(8), reader.IsDBNull(9) ? null : ParseUtc(reader.GetString(9)), reader.GetString(10), reader.GetInt32(11)));
        }
        return rows;
    }

    static DateTime ParseUtc(string value) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    static void AddColumnIfMissing(SqliteConnection connection, string name, string definition)
    {
        using var command = connection.CreateCommand(); command.CommandText = $"ALTER TABLE XmlRuns ADD COLUMN {name} {definition}";
        try { command.ExecuteNonQuery(); } catch (SqliteException error) when (error.SqliteErrorCode == 1 && error.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)) { }
    }
}
