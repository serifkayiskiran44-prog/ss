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
    string Error);

/// Bounded diagnostics only (id/source identity, a short reason, detection time) -
/// never Error - for an XmlRuns row with an unparsable StartedUtc/FinishedUtc.
/// See CatalogStore's CorruptProductRow for the same pattern.
public sealed record CorruptXmlRunRow(string Id, string SourceId, string Reason, DateTime DetectedUtc);

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
                Error TEXT NOT NULL DEFAULT '')
            """;
        command.ExecuteNonQuery();
        using var index = connection.CreateCommand();
        index.CommandText = "CREATE INDEX IF NOT EXISTS IX_XmlRuns_SourceStarted ON XmlRuns(SourceId,StartedUtc DESC);CREATE UNIQUE INDEX IF NOT EXISTS UX_XmlRuns_OneRunningSource ON XmlRuns(SourceId) WHERE Status='Running'";
        index.ExecuteNonQuery();
    }

    SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    public string Start(string sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("XML kaynak kimliği zorunludur.", nameof(sourceId));
        var id = Guid.NewGuid().ToString("N");
        using var connection = Open();
        using (var running = connection.CreateCommand())
        {
            running.CommandText = "SELECT 1 FROM XmlRuns WHERE SourceId=$source AND Status='Running' LIMIT 1";
            running.Parameters.AddWithValue("$source", sourceId.Trim());
            if (running.ExecuteScalar() is not null) throw new InvalidOperationException("Bu XML kaynağı zaten çalışıyor; paralel ikinci çalışma engellendi.");
        }
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO XmlRuns(Id,SourceId,Status,StartedUtc) VALUES($id,$source,'Running',$started)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$source", sourceId.Trim());
        command.Parameters.AddWithValue("$started", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        try { command.ExecuteNonQuery(); }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        { throw new InvalidOperationException("Bu XML kaynağı zaten çalışıyor; paralel ikinci çalışma engellendi."); }
        return id;
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
                Added=$added, Updated=$updated, Unchanged=$unchanged, Error=$error
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

    /// A malformed StartedUtc/FinishedUtc must never crash the whole read - the row
    /// is excluded from the healthy result and reported only via CorruptRuns();
    /// detection re-runs from the row's own stored text every call, so it stays
    /// stable across a restart without a separate tracking table. A NULL
    /// FinishedUtc (still Running) is never treated as corruption - only a stored,
    /// unparsable value is.
    public IReadOnlyList<XmlRunRecord> List(string? sourceId = null, int limit = 100)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit), "Geçmiş limiti 1–1000 arasında olmalı.");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sourceId is null
            ? "SELECT Id,SourceId,Status,StartedUtc,FinishedUtc,Added,Updated,Unchanged,Error FROM XmlRuns ORDER BY StartedUtc DESC LIMIT $limit"
            : "SELECT Id,SourceId,Status,StartedUtc,FinishedUtc,Added,Updated,Unchanged,Error FROM XmlRuns WHERE SourceId=$source ORDER BY StartedUtc DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        if (sourceId is not null) command.Parameters.AddWithValue("$source", sourceId);
        using var reader = command.ExecuteReader();
        var rows = new List<XmlRunRecord>();
        while (reader.Read()) if (TryRead(reader, out var row, out _)) rows.Add(row!);
        return rows;
    }

    /// Bounded diagnostics for every row (within `limit`) whose StartedUtc/
    /// FinishedUtc failed to parse - never the raw Error text.
    public IReadOnlyList<CorruptXmlRunRow> CorruptRuns(string? sourceId = null, int limit = 1000)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit), "Geçmiş limiti 1–1000 arasında olmalı.");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sourceId is null
            ? "SELECT Id,SourceId,Status,StartedUtc,FinishedUtc,Added,Updated,Unchanged,Error FROM XmlRuns ORDER BY StartedUtc DESC LIMIT $limit"
            : "SELECT Id,SourceId,Status,StartedUtc,FinishedUtc,Added,Updated,Unchanged,Error FROM XmlRuns WHERE SourceId=$source ORDER BY StartedUtc DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        if (sourceId is not null) command.Parameters.AddWithValue("$source", sourceId);
        using var reader = command.ExecuteReader();
        var rows = new List<CorruptXmlRunRow>();
        while (reader.Read()) if (!TryRead(reader, out _, out var corrupt)) rows.Add(corrupt!);
        return rows;
    }

    static bool TryRead(SqliteDataReader reader, out XmlRunRecord? row, out CorruptXmlRunRow? corrupt)
    {
        row = null; corrupt = null; var id = reader.GetString(0); var sourceId = reader.GetString(1);
        if (!TryParseUtc(reader.GetString(3), out var started)) { corrupt = new(id, sourceId, "Malformed StartedUtc timestamp", DateTime.UtcNow); return false; }
        DateTime? finished = null;
        if (!reader.IsDBNull(4)) { if (!TryParseUtc(reader.GetString(4), out var value)) { corrupt = new(id, sourceId, "Malformed FinishedUtc timestamp", DateTime.UtcNow); return false; } finished = value; }
        row = new XmlRunRecord(id, sourceId, reader.GetString(2), started, finished, reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7), reader.GetString(8));
        return true;
    }

    /// Only ever a format/parse failure - never conflated with a DB-busy/locked
    /// SqliteException, which is raised by the surrounding command, not this parse.
    static bool TryParseUtc(string value, out DateTime result) => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out result);
}
