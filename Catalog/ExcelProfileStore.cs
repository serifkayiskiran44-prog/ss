using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace TrMarketplaceHubDesktop.Catalog;

public sealed class ExcelImportProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Yeni Excel profili";
    public string CultureName { get; set; } = CultureInfo.CurrentCulture.Name;
    public Dictionary<string, string> ColumnMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> HeaderAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Defaults { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> VisibleFields { get; set; } = [];
    /// null/empty means "first worksheet" (previous fixed behavior); set explicitly
    /// once the user picks a sheet other than the first, or a hidden/empty first
    /// sheet would otherwise be silently assumed.
    public string? SheetName { get; set; }
    /// 1-based row number containing headers; previously always assumed to be 1.
    public int HeaderRow { get; set; } = 1;
    /// Header text captured at save time, so a later load can detect the workbook's
    /// header set changed and warn instead of silently mis-mapping columns.
    public List<string> ExpectedHeaders { get; set; } = [];
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    /// Optimistic-concurrency token: must equal the currently-stored value (0 for
    /// "no profile yet") for Save/Delete to accept the write - see
    /// ExcelProfileStore. Never reused after a delete for the same Id (#2666's
    /// ABA guard): a separate persistent floor keeps issuing strictly higher
    /// revisions for that Id even across a delete+recreate cycle.
    public int Revision { get; set; }
}

/// Bounded diagnostics only (id/name + a short reason code + detection time) -
/// never mapping/header/default/PII content - for an ExcelProfiles row that
/// failed to deserialize or whose payload identity doesn't match its own row.
public sealed record CorruptExcelProfileRow(string Id, string Name, string Reason, DateTime DetectedUtc);
/// Raised by Find(...) when the row exists but is corrupt - kept distinct from
/// returning null (which still means "no such profile").
public sealed class ExcelProfileCorruptException : Exception
{
    public string ProfileId { get; }
    public ExcelProfileCorruptException(string profileId, string reason) : base($"Excel profili bozuk (REVIEW_REQUIRED): {reason}") => ProfileId = profileId;
}

public sealed class ExcelProfileStore
{
    readonly string connectionString;
    public ExcelProfileStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "excel-profiles.db") }.ToString();
        using var connection = Open(); using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS ExcelProfiles(Id TEXT PRIMARY KEY,Name TEXT NOT NULL,Json TEXT NOT NULL,UpdatedUtc TEXT NOT NULL,Revision INTEGER NOT NULL DEFAULT 0);" +
            // Never deleted, never decremented: the durable floor a given Id's
            // revision must exceed, so a delete+recreate of the same Id can never
            // reissue a revision number a still-open, older editor might remember
            // from before the delete (see #2666's ABA edge case).
            "CREATE TABLE IF NOT EXISTS ExcelProfileRevisionFloor(Id TEXT PRIMARY KEY,Floor INTEGER NOT NULL)";
        command.ExecuteNonQuery();
        EnsureColumn(connection, "Revision", "INTEGER NOT NULL DEFAULT 0");
    }
    static void EnsureColumn(SqliteConnection c, string name, string definition) { using var check = c.CreateCommand(); check.CommandText = "SELECT 1 FROM pragma_table_info('ExcelProfiles') WHERE name=$name"; check.Parameters.AddWithValue("$name", name); if (check.ExecuteScalar() != null) return; using var add = c.CreateCommand(); add.CommandText = $"ALTER TABLE ExcelProfiles ADD COLUMN {name} {definition}"; add.ExecuteNonQuery(); }
    SqliteConnection Open() { var connection = new SqliteConnection(connectionString); connection.Open(); return connection; }
    /// profile.Revision must equal the currently-stored revision (0 for "no
    /// profile yet") - the same optimistic-concurrency contract used elsewhere
    /// (TaxonomyEntry/PricingProfile/ChannelProductPlan). A stale/deleted/newly-
    /// created profile under the same Id is rejected rather than silently
    /// overwritten, and the durable floor prevents an old handle's revision
    /// number from ever matching a profile recreated after a delete.
    public void Save(ExcelImportProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Trim().Length > 120) throw new InvalidOperationException("Excel profil adı 1-120 karakter olmalı.");
        _ = Culture(profile.CultureName);
        profile.Name = profile.Name.Trim(); profile.UpdatedUtc = DateTime.UtcNow;
        using var connection = Open(); using var tx = connection.BeginTransaction();
        int currentRevision;
        using (var find = connection.CreateCommand())
        {
            find.Transaction = tx; find.CommandText = "SELECT Revision FROM ExcelProfiles WHERE Id=$id"; find.Parameters.AddWithValue("$id", profile.Id);
            var current = find.ExecuteScalar();
            currentRevision = current is null ? 0 : Convert.ToInt32(current, CultureInfo.InvariantCulture);
            if (currentRevision != profile.Revision) throw new InvalidOperationException("Excel profili başka bir işlemde değişti; yenileyip tekrar deneyin.");
        }
        int floor;
        using (var findFloor = connection.CreateCommand())
        {
            findFloor.Transaction = tx; findFloor.CommandText = "SELECT Floor FROM ExcelProfileRevisionFloor WHERE Id=$id"; findFloor.Parameters.AddWithValue("$id", profile.Id);
            var value = findFloor.ExecuteScalar(); floor = value is null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        var nextRevision = Math.Max(currentRevision, floor) + 1;
        profile.Revision = nextRevision;
        using (var upsertFloor = connection.CreateCommand()) { upsertFloor.Transaction = tx; upsertFloor.CommandText = "INSERT INTO ExcelProfileRevisionFloor(Id,Floor) VALUES($id,$floor) ON CONFLICT(Id) DO UPDATE SET Floor=excluded.Floor"; upsertFloor.Parameters.AddWithValue("$id", profile.Id); upsertFloor.Parameters.AddWithValue("$floor", nextRevision); upsertFloor.ExecuteNonQuery(); }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = tx; command.CommandText = "INSERT INTO ExcelProfiles(Id,Name,Json,UpdatedUtc,Revision) VALUES($id,$name,$json,$updated,$revision) ON CONFLICT(Id) DO UPDATE SET Name=excluded.Name,Json=excluded.Json,UpdatedUtc=excluded.UpdatedUtc,Revision=excluded.Revision";
            command.Parameters.AddWithValue("$id", profile.Id); command.Parameters.AddWithValue("$name", profile.Name); command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(profile)); command.Parameters.AddWithValue("$updated", profile.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$revision", nextRevision);
            command.ExecuteNonQuery();
        }
        tx.Commit();
    }
    const string SelectAll = "SELECT Id,Name,Json,Revision FROM ExcelProfiles";
    static bool TryRead(SqliteDataReader r, out ExcelImportProfile? profile, out CorruptExcelProfileRow? corrupt)
    {
        var id = r.GetString(0); var name = r.GetString(1); var json = r.GetString(2); var revision = r.GetInt32(3);
        profile = null;
        ExcelImportProfile? candidate;
        try { candidate = JsonSerializer.Deserialize<ExcelImportProfile>(json); }
        catch (JsonException) { corrupt = new(id, name, "Geçersiz JSON", DateTime.UtcNow); return false; }
        if (candidate is null) { corrupt = new(id, name, "Boş JSON", DateTime.UtcNow); return false; }
        if (candidate.Id != id) { corrupt = new(id, name, "Kimlik uyuşmazlığı", DateTime.UtcNow); return false; }
        candidate.Revision = revision; profile = candidate; corrupt = null; return true;
    }
    public IReadOnlyList<ExcelImportProfile> List()
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = SelectAll + " ORDER BY Name"; using var reader = command.ExecuteReader(); var result = new List<ExcelImportProfile>(); while (reader.Read()) if (TryRead(reader, out var profile, out _)) result.Add(profile!); return result;
    }
    /// Read-only diagnostics: id/name + bounded reason code only, never mapping/
    /// header/default content. Corrupt rows are never auto-deleted/overwritten
    /// by List/Find.
    public IReadOnlyList<CorruptExcelProfileRow> CorruptProfiles()
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = SelectAll; using var reader = command.ExecuteReader(); var result = new List<CorruptExcelProfileRow>(); while (reader.Read()) if (!TryRead(reader, out _, out var corrupt)) result.Add(corrupt!); return result;
    }
    public ExcelImportProfile? Find(string id)
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = SelectAll + " WHERE Id=$id"; command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
        if (!TryRead(reader, out var profile, out var corrupt)) throw new ExcelProfileCorruptException(corrupt!.Id, corrupt.Reason);
        return profile;
    }
    /// expectedRevision must equal the currently-stored revision - a stale caller
    /// (holding an older revision than what's actually persisted) can never
    /// delete a profile it no longer has an up-to-date view of.
    public void Delete(string id, int expectedRevision)
    {
        using var connection = Open(); using var tx = connection.BeginTransaction();
        using var find = connection.CreateCommand(); find.Transaction = tx; find.CommandText = "SELECT Revision FROM ExcelProfiles WHERE Id=$id"; find.Parameters.AddWithValue("$id", id);
        var current = find.ExecuteScalar();
        if (current is null) throw new InvalidOperationException("Excel profili bulunamadı.");
        if (Convert.ToInt32(current, CultureInfo.InvariantCulture) != expectedRevision) throw new InvalidOperationException("Excel profili başka bir işlemde değişti; yenileyip tekrar deneyin.");
        using var del = connection.CreateCommand(); del.Transaction = tx; del.CommandText = "DELETE FROM ExcelProfiles WHERE Id=$id"; del.Parameters.AddWithValue("$id", id); del.ExecuteNonQuery();
        tx.Commit();
    }
    public static CultureInfo Culture(string? name) { try { return string.IsNullOrWhiteSpace(name) ? CultureInfo.CurrentCulture : CultureInfo.GetCultureInfo(name); } catch (CultureNotFoundException) { throw new InvalidOperationException("Excel profilinin sayı/tarih kültürü geçersiz."); } }
}
