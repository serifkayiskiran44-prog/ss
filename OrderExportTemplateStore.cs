using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace TrMarketplaceHubDesktop;

public sealed class OrderExportTemplate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<string> FieldIds { get; set; } = [];
    public int Version { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// Bounded diagnostics only (id/name + a short reason code + detection time) -
/// never the raw FieldIdsJson - for a persisted row that failed to
/// deserialize/parse or whose FieldIds no longer validate against
/// OrderExportFieldRegistry. See CatalogStore's CorruptProductRow for the same
/// pattern.
public sealed record CorruptOrderExportTemplate(string Id, string Name, string Reason, DateTime DetectedUtc);
/// Raised by Find(...) when the row exists but is corrupt - kept distinct from
/// returning null (which still means "no such template"), so a caller can
/// never mistake "needs repair" for "not found", and export can never silently
/// start from an empty/partial field selection.
public sealed class OrderExportTemplateCorruptException : Exception
{
    public string TemplateId { get; }
    public OrderExportTemplateCorruptException(string templateId, string reason) : base($"Export şablonu bozuk (REVIEW_REQUIRED): {reason}") => TemplateId = templateId;
}
/// Stale means zero rows were deleted because the persisted Version no longer
/// matches the caller's snapshot; AlreadyDeleted is the distinct idempotent
/// "no such row" outcome.
public enum OrderExportTemplateDeleteResult { Deleted, AlreadyDeleted, Stale }

/// Named, reusable field-selection templates for order Excel export (#1980).
/// A template stores only the chosen field IDs from OrderExportFieldRegistry
/// - never the current filter result, never actual order/PII values - so
/// saving a template can never leak data and re-running it always reflects
/// whatever orders currently match the export scope at run time.
public sealed class OrderExportTemplateStore
{
    readonly string connectionString;

    public OrderExportTemplateStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "catalog.db"), DefaultTimeout = 15, Pooling = true }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand();
        // The floor table is never deleted from: it remembers the highest Version
        // ever issued per Id, so a template deleted and recreated under the same
        // Id can never be handed a Version an older, still-open snapshot holds
        // (ABA). See #2667.
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS OrderExportTemplates(Id TEXT PRIMARY KEY, Name TEXT NOT NULL, FieldIdsJson TEXT NOT NULL, Version INTEGER NOT NULL, UpdatedUtc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS OrderExportTemplateRevisionFloor(Id TEXT PRIMARY KEY, Floor INTEGER NOT NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); return c; }

    public OrderExportTemplate Save(OrderExportTemplate template)
    {
        var name = template.Name.Trim();
        if (name.Length == 0 || name.Length > 120) throw new ArgumentException("Şablon adı 1-120 karakter olmalı.");
        var fieldIds = (template.FieldIds ?? []).Select(id => id.Trim()).Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (fieldIds.Count == 0) throw new ArgumentException("En az bir alan seçilmeli.");
        var unknown = fieldIds.Where(id => !OrderExportFieldRegistry.IsValidFieldId(id)).ToList();
        if (unknown.Count > 0) throw new ArgumentException($"Kayıtlı olmayan alan(lar): {string.Join(", ", unknown)}");

        using var c = Open(); using var tx = c.BeginTransaction();
        using (var dup = c.CreateCommand())
        {
            dup.Transaction = tx; dup.CommandText = "SELECT Id FROM OrderExportTemplates WHERE Id<>$id AND lower(Name)=lower($name)";
            dup.Parameters.AddWithValue("$id", template.Id); dup.Parameters.AddWithValue("$name", name);
            if (dup.ExecuteScalar() is not null) throw new InvalidOperationException("Bu isimde bir export şablonu zaten var.");
        }
        long currentVersion = 0;
        using (var find = c.CreateCommand())
        {
            find.Transaction = tx; find.CommandText = "SELECT Version FROM OrderExportTemplates WHERE Id=$id"; find.Parameters.AddWithValue("$id", template.Id);
            var current = find.ExecuteScalar();
            if (current is not null)
            {
                currentVersion = Convert.ToInt64(current, CultureInfo.InvariantCulture);
                if (currentVersion != template.Version) throw new InvalidOperationException("Şablon başka bir işlemde değişti; yenileyip tekrar deneyin.");
            }
        }
        var nextVersion = Math.Max(currentVersion, ReadFloor(c, tx, template.Id)) + 1;
        if (nextVersion > int.MaxValue) throw new InvalidOperationException("Şablon sürüm sınırına ulaştı; yeni bir şablon oluşturun.");
        RaiseFloor(c, tx, template.Id, nextVersion);
        template.Name = name; template.FieldIds = fieldIds; template.Version = (int)nextVersion; template.UpdatedUtc = DateTime.UtcNow;
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO OrderExportTemplates(Id,Name,FieldIdsJson,Version,UpdatedUtc) VALUES($id,$name,$fields,$version,$updated) ON CONFLICT(Id) DO UPDATE SET Name=excluded.Name,FieldIdsJson=excluded.FieldIdsJson,Version=excluded.Version,UpdatedUtc=excluded.UpdatedUtc";
        cmd.Parameters.AddWithValue("$id", template.Id); cmd.Parameters.AddWithValue("$name", template.Name);
        cmd.Parameters.AddWithValue("$fields", JsonSerializer.Serialize(template.FieldIds));
        cmd.Parameters.AddWithValue("$version", template.Version); cmd.Parameters.AddWithValue("$updated", template.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery(); tx.Commit();
        return template;
    }

    static long ReadFloor(SqliteConnection c, SqliteTransaction tx, string id)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT Floor FROM OrderExportTemplateRevisionFloor WHERE Id=$id"; cmd.Parameters.AddWithValue("$id", id);
        var value = cmd.ExecuteScalar();
        return value is null ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    static void RaiseFloor(SqliteConnection c, SqliteTransaction tx, string id, long floor)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO OrderExportTemplateRevisionFloor(Id,Floor) VALUES($id,$floor) ON CONFLICT(Id) DO UPDATE SET Floor=MAX(Floor,excluded.Floor)";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$floor", floor);
        cmd.ExecuteNonQuery();
    }

    const int MaxFieldIdsJsonBytes = 65_536;
    /// A malformed row must never crash the whole template list - it's
    /// excluded from the healthy result and reported only via
    /// CorruptTemplates(); detection re-derives from the row's own stored data
    /// every call. Field IDs are re-validated against the current
    /// OrderExportFieldRegistry on every read (not just at Save time), so a
    /// field retired from the registry after a template was saved is caught
    /// here too - export can never silently start from a stale/partial field
    /// selection. A Version Save could never have written (below 1, or past
    /// int range) is corrupt too, so it can't be materialized as a normal
    /// delete/edit snapshot.
    static bool TryRead(SqliteDataReader r, out OrderExportTemplate? template, out CorruptOrderExportTemplate? corrupt)
    {
        template = null; var id = r.GetString(0); var name = r.GetString(1); var json = r.GetString(2);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxFieldIdsJsonBytes) { corrupt = new(id, name, "Kayıt boyutu sınırı aşıyor", DateTime.UtcNow); return false; }
        List<string>? fieldIds;
        try { fieldIds = JsonSerializer.Deserialize<List<string>>(json); }
        catch (JsonException) { corrupt = new(id, name, "Geçersiz JSON", DateTime.UtcNow); return false; }
        if (fieldIds is null) { corrupt = new(id, name, "Boş JSON", DateTime.UtcNow); return false; }
        if (fieldIds.Count == 0) { corrupt = new(id, name, "Boş alan listesi", DateTime.UtcNow); return false; }
        var unknown = fieldIds.Where(f => !OrderExportFieldRegistry.IsValidFieldId(f)).ToList();
        if (unknown.Count > 0) { corrupt = new(id, name, "Kayıtlı olmayan alan(lar)", DateTime.UtcNow); return false; }
        if (!DateTime.TryParse(r.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var updated)) { corrupt = new(id, name, "Geçersiz güncelleme zamanı", DateTime.UtcNow); return false; }
        long version;
        try { version = r.GetInt64(3); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { corrupt = new(id, name, "Geçersiz sürüm", DateTime.UtcNow); return false; }
        if (version is < 1 or > int.MaxValue) { corrupt = new(id, name, "Geçersiz sürüm", DateTime.UtcNow); return false; }
        template = new() { Id = id, Name = name, FieldIds = fieldIds, Version = (int)version, UpdatedUtc = updated };
        corrupt = null; return true;
    }

    public IReadOnlyList<OrderExportTemplate> List()
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id,Name,FieldIdsJson,Version,UpdatedUtc FROM OrderExportTemplates ORDER BY Name";
        using var r = cmd.ExecuteReader(); var result = new List<OrderExportTemplate>(); while (r.Read()) if (TryRead(r, out var template, out _)) result.Add(template!); return result;
    }

    /// Read-only diagnostics: id/name + bounded reason code only, never the raw
    /// FieldIdsJson. Corrupt rows are never auto-deleted/overwritten by List/
    /// Find/Save; this is the only way to discover them for operator repair.
    public IReadOnlyList<CorruptOrderExportTemplate> CorruptTemplates()
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id,Name,FieldIdsJson,Version,UpdatedUtc FROM OrderExportTemplates ORDER BY Name";
        using var r = cmd.ExecuteReader(); var result = new List<CorruptOrderExportTemplate>(); while (r.Read()) if (!TryRead(r, out _, out var corrupt)) result.Add(corrupt!); return result;
    }

    public OrderExportTemplate? Find(string id)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id,Name,FieldIdsJson,Version,UpdatedUtc FROM OrderExportTemplates WHERE Id=$id"; cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader(); if (!r.Read()) return null;
        if (!TryRead(r, out var template, out var corrupt)) throw new OrderExportTemplateCorruptException(corrupt!.Id, corrupt.Reason);
        return template;
    }

    /// Compare-and-delete: removes the row only while its persisted Version
    /// still equals the caller's snapshot, inside one immediate transaction, so
    /// a stale screen can never delete a newer version someone else saved, and
    /// of two racing deletes exactly one gets Deleted. The deleted Version is
    /// folded into the durable floor first, so a same-Id recreate always gets a
    /// strictly higher Version. A corrupt row is refused here (never
    /// materialized as a deletable snapshot) - use DeleteCorruptTemplate. See #2667.
    public OrderExportTemplateDeleteResult Delete(string id, int expectedVersion)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        OrderExportTemplate? current;
        using (var find = c.CreateCommand())
        {
            find.Transaction = tx; find.CommandText = "SELECT Id,Name,FieldIdsJson,Version,UpdatedUtc FROM OrderExportTemplates WHERE Id=$id"; find.Parameters.AddWithValue("$id", id);
            using var r = find.ExecuteReader();
            if (!r.Read()) { current = null; }
            else if (!TryRead(r, out current, out var corrupt)) throw new OrderExportTemplateCorruptException(corrupt!.Id, corrupt.Reason);
        }
        if (current is null) { tx.Commit(); return OrderExportTemplateDeleteResult.AlreadyDeleted; }
        if (current.Version != expectedVersion) { tx.Commit(); return OrderExportTemplateDeleteResult.Stale; }
        RaiseFloor(c, tx, id, current.Version);
        using var delete = c.CreateCommand(); delete.Transaction = tx;
        delete.CommandText = "DELETE FROM OrderExportTemplates WHERE Id=$id AND Version=$version";
        delete.Parameters.AddWithValue("$id", id); delete.Parameters.AddWithValue("$version", expectedVersion);
        if (delete.ExecuteNonQuery() != 1) throw new InvalidOperationException("Şablon silinemedi; yenileyip tekrar deneyin.");
        tx.Commit();
        return OrderExportTemplateDeleteResult.Deleted;
    }

    /// Explicit recovery action for a row CorruptTemplates() reported. It
    /// re-validates inside the delete transaction that the row is still corrupt,
    /// so a row repaired or replaced since diagnostics were read is never
    /// discarded through this path. Whatever integer Version the corrupt row
    /// held still raises the floor, so a recreate can't reuse it.
    public void DeleteCorruptTemplate(string id)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        using (var find = c.CreateCommand())
        {
            find.Transaction = tx; find.CommandText = "SELECT Id,Name,FieldIdsJson,Version,UpdatedUtc FROM OrderExportTemplates WHERE Id=$id"; find.Parameters.AddWithValue("$id", id);
            using var r = find.ExecuteReader();
            if (!r.Read()) throw new InvalidOperationException("Kayıt bulunamadı.");
            if (TryRead(r, out _, out _)) throw new InvalidOperationException("Bu şablon bozuk değil; normal silme akışını kullanın.");
        }
        using (var floor = c.CreateCommand())
        {
            floor.Transaction = tx;
            floor.CommandText = "INSERT INTO OrderExportTemplateRevisionFloor(Id,Floor) SELECT Id, CASE WHEN typeof(Version)='integer' AND Version>0 THEN Version ELSE 0 END FROM OrderExportTemplates WHERE Id=$id ON CONFLICT(Id) DO UPDATE SET Floor=MAX(Floor,excluded.Floor)";
            floor.Parameters.AddWithValue("$id", id); floor.ExecuteNonQuery();
        }
        using var delete = c.CreateCommand(); delete.Transaction = tx;
        delete.CommandText = "DELETE FROM OrderExportTemplates WHERE Id=$id"; delete.Parameters.AddWithValue("$id", id);
        delete.ExecuteNonQuery(); tx.Commit();
    }
}
