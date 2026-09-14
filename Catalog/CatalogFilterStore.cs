using Microsoft.Data.Sqlite;
using System.IO;
using System.Text.Json;

namespace TrMarketplaceHubDesktop.Catalog;

public sealed record SavedCatalogFilter(string Name, CatalogFilter Filter);

/// Bounded diagnostics only (row name, a short reason, byte length, detection time) -
/// never the raw filter JSON/values - for a CatalogFilterViews row that failed
/// decode/validation. See CatalogStore's CorruptProductRow for the identical pattern
/// applied to products (#2585); this covers saved filters (#2586).
public sealed record CorruptCatalogFilterRow(string Name, string Reason, int PayloadLength, DateTime DetectedUtc);

public sealed class CatalogFilterStore
{
    public const int MaxFilterJsonBytes = 200_000;
    const int MaxArrayItems = 100;
    const int MaxArrayCharsSum = 6000;

    readonly string connectionString;
    public CatalogFilterStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "catalog.db") }.ToString();
        using var c = Open(); using var command = c.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS CatalogFilterViews(Name TEXT PRIMARY KEY, Json TEXT NOT NULL)";
        command.ExecuteNonQuery();
    }
    SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); return c; }

    /// Only ever wraps JsonSerializer/validation failures - a SqliteException from the
    /// surrounding command (DB busy/locked) is never caught here, so a transient DB
    /// problem can never be misreported as row corruption.
    static bool TryDeserializeFilter(string name, string json, out CatalogFilter? filter, out CorruptCatalogFilterRow? corrupt)
    {
        filter = null; corrupt = null;
        if (json.Length > MaxFilterJsonBytes) { corrupt = new(name, "Oversized payload", json.Length, DateTime.UtcNow); return false; }
        CatalogFilter? parsed;
        try { parsed = JsonSerializer.Deserialize<CatalogFilter>(json); }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException) { corrupt = new(name, "Malformed JSON: " + ex.GetType().Name, json.Length, DateTime.UtcNow); return false; }
        if (parsed is null) { corrupt = new(name, "Empty or null document", json.Length, DateTime.UtcNow); return false; }
        foreach (var values in new[] { parsed.Brands, parsed.Categories, parsed.Skus, parsed.SourceIds })
        {
            // A field absent from the JSON keeps its []  C# default (legacy rows saved
            // before a field existed) - only an explicit JSON null, or an oversized/
            // null-item array, counts as corruption.
            if (values is null) { corrupt = new(name, "Null array member", json.Length, DateTime.UtcNow); return false; }
            if (values.Any(v => v is null)) { corrupt = new(name, "Null array item", json.Length, DateTime.UtcNow); return false; }
            if (values.Length > MaxArrayItems || values.Sum(v => v.Length) > MaxArrayCharsSum) { corrupt = new(name, "Array payload exceeds bounds", json.Length, DateTime.UtcNow); return false; }
        }
        filter = parsed;
        return true;
    }

    static void ValidateBounds(CatalogFilter filter)
    {
        foreach (var values in new[] { filter.Brands, filter.Categories, filter.Skus, filter.SourceIds })
        {
            if (values is null) throw new ArgumentException("Filtre alanları null olamaz.");
            if (values.Any(v => v is null)) throw new ArgumentException("Filtre alanları null öğe içeremez.");
            if (values.Length > MaxArrayItems || values.Sum(v => v.Length) > MaxArrayCharsSum) throw new ArgumentException($"Filtre alanı en fazla {MaxArrayItems} değer ve {MaxArrayCharsSum} karakter olabilir.");
        }
    }

    public IReadOnlyList<SavedCatalogFilter> List()
    {
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "SELECT Name,Json FROM CatalogFilterViews ORDER BY Name";
        using var reader = command.ExecuteReader(); var rows = new List<SavedCatalogFilter>();
        while (reader.Read())
        {
            var name = reader.GetString(0); var json = reader.GetString(1);
            if (TryDeserializeFilter(name, json, out var filter, out _)) rows.Add(new(name, filter!));
        }
        return rows;
    }

    /// Bounded diagnostics for every row that failed decode/validation - the review-
    /// required state re-derives from the row's own stored bytes on each call, so it
    /// survives a restart without a separate tracking table.
    public IReadOnlyList<CorruptCatalogFilterRow> CorruptFilters()
    {
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "SELECT Name,Json FROM CatalogFilterViews ORDER BY Name";
        using var reader = command.ExecuteReader(); var rows = new List<CorruptCatalogFilterRow>();
        while (reader.Read())
        {
            var name = reader.GetString(0); var json = reader.GetString(1);
            if (!TryDeserializeFilter(name, json, out _, out var corrupt)) rows.Add(corrupt!);
        }
        return rows;
    }

    public void Save(string name, CatalogFilter filter)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 100 || name.Any(char.IsControl)) throw new ArgumentException("Filtre adı 1–100 karakter olmalı.", nameof(name));
        ValidateBounds(filter);
        using var c = Open(); using var tx = c.BeginTransaction();
        using (var find = c.CreateCommand())
        {
            find.Transaction = tx; find.CommandText = "SELECT Json FROM CatalogFilterViews WHERE Name=$name"; find.Parameters.AddWithValue("$name", name);
            // Existing name already corrupt: refuse the plain upsert so a normal Save
            // can never silently replace a REVIEW_REQUIRED record with a fresh one.
            if (find.ExecuteScalar() is string existingJson && !TryDeserializeFilter(name, existingJson, out _, out _))
                throw new InvalidOperationException("Bu adla kayıtlı filtre bozuk (REVIEW_REQUIRED); önce kurtarma veya silme yapılmalı.");
        }
        using var command = c.CreateCommand(); command.Transaction = tx;
        command.CommandText = "INSERT INTO CatalogFilterViews(Name,Json) VALUES($name,$json) ON CONFLICT(Name) DO UPDATE SET Json=excluded.Json";
        command.Parameters.AddWithValue("$name", name); command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(filter));
        command.ExecuteNonQuery(); tx.Commit();
    }

    public void Delete(string name)
    {
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "DELETE FROM CatalogFilterViews WHERE Name=$name"; command.Parameters.AddWithValue("$name", name); command.ExecuteNonQuery();
    }

    /// Explicit, transactional recovery action: re-validates the row is still corrupt
    /// at delete time, so a row fixed/replaced since the caller last listed it is never
    /// silently discarded.
    public void DeleteCorruptFilter(string name)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        using var find = c.CreateCommand(); find.Transaction = tx; find.CommandText = "SELECT Json FROM CatalogFilterViews WHERE Name=$name"; find.Parameters.AddWithValue("$name", name);
        var json = find.ExecuteScalar() as string ?? throw new InvalidOperationException("Kayıt bulunamadı.");
        if (TryDeserializeFilter(name, json, out _, out _)) throw new InvalidOperationException("Bu kayıt bozuk değil; normal silme akışını kullanın.");
        using var del = c.CreateCommand(); del.Transaction = tx; del.CommandText = "DELETE FROM CatalogFilterViews WHERE Name=$name"; del.Parameters.AddWithValue("$name", name);
        del.ExecuteNonQuery(); tx.Commit();
    }
}
