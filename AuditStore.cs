using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TrMarketplaceHubDesktop;

public sealed class AuditEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
    public string Module { get; set; } = "system";
    public string Action { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string OrderId { get; set; } = "";
    public string Marketplace { get; set; } = "";
    public string ShopId { get; set; } = "";
    public string Outcome { get; set; } = "Info";
    public string Detail { get; set; } = "";
}

/// Bounded diagnostics only (id, a short reason, detection time) - never Detail,
/// ProductId, OrderId, ShopId, or any other audit field - for a row whose AtUtc
/// failed to parse. See CatalogStore's CorruptProductRow for the identical pattern.
public sealed record CorruptAuditRow(string Id, string Reason, DateTime DetectedUtc);

/// Raised only by LastFailure() when every "Failed" row it scanned (bounded by
/// AuditStore.MaxLastFailureScan) has a corrupt AtUtc - a genuinely unknown "most
/// recent failure" state, distinct from the ordinary null meaning "no failures
/// exist". Never raised by List(), which always degrades to the healthy subset.
public sealed class AuditStoreCorruptionException : Exception
{
    public AuditStoreCorruptionException(string message) : base(message) { }
}

public sealed class AuditStore
{
    public const int RetentionLimit = 5000;
    public const int MaxLastFailureScan = 50;
    readonly string connectionString;
    public AuditStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop"); Directory.CreateDirectory(directory); connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "audit.db") }.ToString();
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "CREATE TABLE IF NOT EXISTS AuditEvents(Id TEXT PRIMARY KEY,AtUtc TEXT NOT NULL,Module TEXT NOT NULL,Action TEXT NOT NULL,ProductId TEXT NOT NULL,OrderId TEXT NOT NULL,Marketplace TEXT NOT NULL,ShopId TEXT NOT NULL,Outcome TEXT NOT NULL,Detail TEXT NOT NULL);CREATE INDEX IF NOT EXISTS IX_AuditEvents_At ON AuditEvents(AtUtc DESC)"; command.ExecuteNonQuery();
    }
    SqliteConnection Open() { var connection = new SqliteConnection(connectionString); connection.Open(); return connection; }
    public void Append(AuditEvent audit)
    {
        audit.Module = Clean(audit.Module, 80); audit.Action = Clean(audit.Action, 120); audit.ProductId = Clean(audit.ProductId, 120); audit.OrderId = Clean(audit.OrderId, 120); audit.Marketplace = Clean(audit.Marketplace, 80); audit.ShopId = Clean(audit.ShopId, 160); audit.Outcome = Clean(audit.Outcome, 40); audit.Detail = Sanitize(audit.Detail); audit.AtUtc = audit.AtUtc == default ? DateTime.UtcNow : audit.AtUtc;
        using var connection = Open(); using var transaction = connection.BeginTransaction(); using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "INSERT INTO AuditEvents VALUES($id,$at,$module,$action,$product,$order,$marketplace,$shop,$outcome,$detail)"; command.Parameters.AddWithValue("$id", audit.Id); command.Parameters.AddWithValue("$at", audit.AtUtc.ToString("O", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$module", audit.Module); command.Parameters.AddWithValue("$action", audit.Action); command.Parameters.AddWithValue("$product", audit.ProductId); command.Parameters.AddWithValue("$order", audit.OrderId); command.Parameters.AddWithValue("$marketplace", audit.Marketplace); command.Parameters.AddWithValue("$shop", audit.ShopId); command.Parameters.AddWithValue("$outcome", audit.Outcome); command.Parameters.AddWithValue("$detail", audit.Detail); command.ExecuteNonQuery(); using var trim = connection.CreateCommand(); trim.Transaction = transaction; trim.CommandText = "DELETE FROM AuditEvents WHERE Id NOT IN (SELECT Id FROM AuditEvents ORDER BY AtUtc DESC LIMIT $limit)"; trim.Parameters.AddWithValue("$limit", RetentionLimit); trim.ExecuteNonQuery(); transaction.Commit();
    }
    /// A malformed AtUtc must never crash the whole read - it is excluded from the
    /// healthy result and reported only via CorruptEvents(); detection re-runs from
    /// the row's own stored text every call, so it stays stable across a restart.
    public IReadOnlyList<AuditEvent> List(int limit = 500, string? query = null)
    {
        if (limit is < 1 or > RetentionLimit) throw new ArgumentOutOfRangeException(nameof(limit)); using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT Id,AtUtc,Module,Action,ProductId,OrderId,Marketplace,ShopId,Outcome,Detail FROM AuditEvents WHERE ($query='' OR Module LIKE $like OR Action LIKE $like OR Outcome LIKE $like OR Detail LIKE $like OR ProductId LIKE $like OR OrderId LIKE $like OR Marketplace LIKE $like OR ShopId LIKE $like) ORDER BY AtUtc DESC LIMIT $limit"; var q = query?.Trim() ?? ""; command.Parameters.AddWithValue("$query", q); command.Parameters.AddWithValue("$like", $"%{q}%"); command.Parameters.AddWithValue("$limit", limit); using var reader = command.ExecuteReader(); var result = new List<AuditEvent>(); while (reader.Read()) if (TryRead(reader, out var evt, out _)) result.Add(evt!); return result;
    }
    /// Bounded diagnostics for every row (within `limit`) whose AtUtc failed to
    /// parse - never the raw Detail/ProductId/OrderId/ShopId values.
    public IReadOnlyList<CorruptAuditRow> CorruptEvents(int limit = RetentionLimit)
    {
        if (limit is < 1 or > RetentionLimit) throw new ArgumentOutOfRangeException(nameof(limit)); using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT Id,AtUtc,Module,Action,ProductId,OrderId,Marketplace,ShopId,Outcome,Detail FROM AuditEvents ORDER BY AtUtc DESC LIMIT $limit"; command.Parameters.AddWithValue("$limit", limit); using var reader = command.ExecuteReader(); var result = new List<CorruptAuditRow>(); while (reader.Read()) if (!TryRead(reader, out _, out var corrupt)) result.Add(corrupt!); return result;
    }
    /// Scans at most MaxLastFailureScan rows so a fully corrupted table can never
    /// spin through hundreds of exceptions; if every scanned "Failed" row is corrupt
    /// this throws a typed AuditStoreCorruptionException instead of returning null -
    /// null is reserved for "no failures at all", a materially different fact.
    public AuditEvent? LastFailure()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,AtUtc,Module,Action,ProductId,OrderId,Marketplace,ShopId,Outcome,Detail FROM AuditEvents WHERE Outcome = $outcome ORDER BY AtUtc DESC LIMIT $limit";
        command.Parameters.AddWithValue("$outcome", "Failed");
        command.Parameters.AddWithValue("$limit", MaxLastFailureScan);
        using var reader = command.ExecuteReader();
        var sawAnyRow = false;
        while (reader.Read())
        {
            sawAnyRow = true;
            if (TryRead(reader, out var evt, out _)) return evt;
        }
        if (sawAnyRow) throw new AuditStoreCorruptionException($"Son {MaxLastFailureScan} 'Failed' kaydının AtUtc alanı bozuk; en son hata belirlenemiyor.");
        return null;
    }
    static bool TryRead(SqliteDataReader reader, out AuditEvent? evt, out CorruptAuditRow? corrupt)
    {
        evt = null; corrupt = null; var id = reader.GetString(0);
        if (!TryParseUtc(reader.GetString(1), out var at)) { corrupt = new(id, "Malformed AtUtc timestamp", DateTime.UtcNow); return false; }
        evt = new() { Id = id, AtUtc = at, Module = reader.GetString(2), Action = reader.GetString(3), ProductId = reader.GetString(4), OrderId = reader.GetString(5), Marketplace = reader.GetString(6), ShopId = reader.GetString(7), Outcome = reader.GetString(8), Detail = reader.GetString(9) };
        return true;
    }
    /// Only ever a format/parse failure - never conflated with a DB-busy/locked
    /// SqliteException, which is raised by the surrounding command, not this parse.
    static bool TryParseUtc(string value, out DateTime result) => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out result);
    static string Clean(string value, int max) { var clean = Sanitize(value); return clean.Length > max ? clean[..max] : clean; }
    public static string Sanitize(string? value)
    {
        var safe = value ?? "";
        // Normalize transport credentials before the generic key/value pass so
        // support packages and audit rows cannot retain bearer/query secrets.
        safe = Regex.Replace(safe, "(?i)\\bAuthorization\\s*:\\s*(?:Bearer|Basic)\\s+[^\\s,;&]+", "Authorization: [redacted]");
        safe = Regex.Replace(safe, "(?i)([?&](?:access[_-]?token|refresh[_-]?token|api[_-]?key|client[_-]?secret|password|passwd|secret|token)=)[^&#\\s]+", "$1[redacted]");
        safe = Regex.Replace(safe, "(?i)(password|passwd|token|secret|api[_-]?key|client[_-]?secret)\\s*[:=]\\s*[\\\"']?[^\\\"'\\s,;&}]+", "$1=[redacted]");
        safe = MarketplaceConnectionStore.Redact(safe);
        safe = Regex.Replace(safe, "(?i)\\b[\\w.%+-]+@[\\w.-]+\\.[a-z]{2,}\\b", "[pii-email]");
        safe = Regex.Replace(safe, "(?<!\\d)(?:\\+?90[ .-]?)?0?5\\d{2}[ .-]?\\d{3}[ .-]?\\d{2}[ .-]?\\d{2}(?!\\d)", "[pii-phone]");
        safe = Regex.Replace(safe, "(?i)\\bAuthorization\\s*:\\s*(?:Bearer|Basic)\\s+[^\\s,;&]+", "Authorization: [redacted]");
        return safe.Length > 2000 ? safe[..2000] : safe;
    }
}

public sealed record DiagnosticCheck(string Name, string Status, string Detail);
public sealed record DiagnosticsSnapshot(DateTime AtUtc, string DataDirectory, string ApplicationVersion, IReadOnlyList<DiagnosticCheck> Checks, int PendingSync, int FailedSync, string LastError);

public sealed class DiagnosticsService
{
    readonly string directory;
    public DiagnosticsService(string? dataDirectory = null) => directory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
    public DiagnosticsSnapshot Build()
    {
        Directory.CreateDirectory(directory); var checks = new List<DiagnosticCheck>(); foreach (var file in new[] { "catalog.db", "orders.db", "media.db", "audit.db", "excel-profiles.db" }) { var path = Path.Combine(directory, file); checks.Add(new(file, File.Exists(path) ? "OK" : "EMPTY", File.Exists(path) ? $"{new FileInfo(path).Length:N0} byte" : "Henüz oluşturulmadı")); }
        IReadOnlyList<Catalog.SyncJob> sync = []; try { sync = new Catalog.SyncStore(directory).List(); checks.Add(new("Sync kuyruğu", "OK", $"{sync.Count:N0} iş")); } catch (Exception error) { checks.Add(new("Sync kuyruğu", "ERROR", AuditStore.Sanitize(error.Message))); }
        try { _ = new Catalog.CatalogStore(directory).Products(); checks.Add(new("Katalog DB", "OK", "Okunabildi")); } catch (Exception error) { checks.Add(new("Katalog DB", "ERROR", AuditStore.Sanitize(error.Message))); }
        var failed = sync.Count(x => x.Status == Catalog.SyncStatus.Failed); var pending = sync.Count(x => x.Status is Catalog.SyncStatus.Pending or Catalog.SyncStatus.Running);
        string lastErrorDetail = "";
        try { lastErrorDetail = new AuditStore(directory).LastFailure()?.Detail ?? ""; }
        catch (AuditStoreCorruptionException error) { checks.Add(new("Audit geçmişi", "ERROR", AuditStore.Sanitize(error.Message))); }
        return new(DateTime.UtcNow, directory, AppVersion.Display, checks, pending, failed, lastErrorDetail);
    }
}

public static class SupportPackageService
{
    public static string Export(string outputPath, string? dataDirectory = null)
    {
        dataDirectory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop"); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!); var audit = new AuditStore(dataDirectory); var diagnostics = new DiagnosticsService(dataDirectory).Build(); var connectionMetadata = new MarketplaceConnectionStore(dataDirectory).List().Select(x => new { x.Id, x.Channel, x.ShopId, x.DisplayName, x.Enabled, x.Status, x.LastTestUtc, LastError = AuditStore.Sanitize(x.LastError) }).ToList(); var sourceMetadata = new Catalog.CatalogStore(dataDirectory).Sources().Select(x => new { x.Id, x.Name, Location = AuditStore.Sanitize(x.Location), x.Enabled, x.LastStatus, x.LastRunUtc }).ToList(); var temp = outputPath + ".tmp-" + Guid.NewGuid().ToString("N"); try { using (var archive = ZipFile.Open(temp, ZipArchiveMode.Create)) { WriteJson(archive, "diagnostics.json", diagnostics); WriteJson(archive, "connections-metadata.json", connectionMetadata); WriteJson(archive, "xml-sources-metadata.json", sourceMetadata); WriteJson(archive, "audit.json", audit.List(AuditStore.RetentionLimit)); var logPath = Path.Combine(dataDirectory, "operations.log"); var entry = archive.CreateEntry("operations.log"); using (var writer = new StreamWriter(entry.Open())) { if (File.Exists(logPath)) foreach (var line in File.ReadLines(logPath).TakeLast(1000)) writer.WriteLine(AuditStore.Sanitize(line)); } var readme = archive.CreateEntry("README.txt"); using (var readmeWriter = new StreamWriter(readme.Open())) { readmeWriter.WriteLine("MonoBridge güvenli destek paketi"); readmeWriter.WriteLine("Credential/token/password değerleri pakete dahil edilmez; yerel DB dosyaları ve şifreli credential byte'ları paylaşılmaz."); } } File.Move(temp, outputPath, true); return outputPath; } finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    static void WriteJson<T>(ZipArchive archive, string name, T value) { var entry = archive.CreateEntry(name); using var writer = new StreamWriter(entry.Open()); writer.Write(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true })); }
}
