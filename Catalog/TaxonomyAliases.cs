using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;

namespace TrMarketplaceHubDesktop.Catalog;

/// <summary>One alias as the dictionary shows it: the text as given, its key, the category it names, whether it is approved (and so applied), where it came from, and its state.</summary>
public sealed record TaxonomyAliasView(string Alias, string Key, string LocalId, string CategoryName, bool Approved, string Source, DateTime UpdatedUtc, string Status)
{
    public const string ApprovedStatus = "APPROVED", Pending = "PENDING", Orphan = "ORPHAN";
}

public sealed record TaxonomyAliasMatch(string LocalId, string CategoryName);

/// <summary>
/// The category alias dictionary (#912). A supplier writes "ELEKTRONİK ürünleri", "Elektronik Ürünler" or
/// "elektronık urunler" for the one local category "Elektronik"; each spelling is an alias, kept as given and keyed
/// by the display fold (#868: Turkish lower-casing, dotted and dotless i met, whitespace collapsed) so the casing
/// variants are one alias. An alias names exactly one active local category: the same alias saved again for the
/// same category is the same alias (its approval may change), for another category it is a conflict and refused;
/// an alias that is another category's own name is refused. Only an approved alias is applied — by the import, on
/// an exact key match, and nowhere else; a pending alias is a suggestion the operator has not confirmed. The
/// fuzzy suggestions of <see cref="TaxonomyStore.SuggestBulk"/> never write through this dictionary.
/// </summary>
public sealed class TaxonomyAliasStore
{
    public const int AliasLimit = 200;
    readonly string connectionString;

    public TaxonomyAliasStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop"); Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "catalog.db") }.ToString();
        _ = new TaxonomyStore(directory); // the entries table is the taxonomy owner's schema; ensure it exists before this dictionary reads it (a fresh database has no taxonomy yet)
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS TaxonomyAliases(Key TEXT PRIMARY KEY, Alias TEXT NOT NULL, LocalId TEXT NOT NULL, Approved INTEGER NOT NULL, Source TEXT NOT NULL DEFAULT 'manual', UpdatedUtc TEXT NOT NULL);CREATE INDEX IF NOT EXISTS IX_TaxonomyAliases_Local ON TaxonomyAliases(LocalId)";
        cmd.ExecuteNonQuery();
    }

    SqliteConnection Open() => SqliteConnectionPolicy.Open(connectionString);

    /// <summary>The key two spellings of one alias share: the display fold, whitespace collapsed.</summary>
    public static string Key(string? alias) => string.Join(' ', UiSearch.Fold(alias).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Saves an alias for a category: refused when the category is unknown, inactive or not a category, when the alias is the category's own name or another category's name, or when the same alias already names another category; the same alias for the same category is updated in place.</summary>
    public TaxonomyAliasView Save(string alias, string localId, bool approved, string source = "manual")
    {
        var text = (alias ?? "").Trim();
        if (text.Length == 0 || text.Length > AliasLimit || text.Any(char.IsControl)) throw new InvalidOperationException("Takma ad 1-200 karakter olmalı ve kontrol karakteri içermemeli.");
        var key = Key(text); if (key.Length == 0) throw new InvalidOperationException("Takma ad harf veya rakam içermeli.");
        using var c = Open(); using var tx = c.BeginTransaction();
        var target = Entry(c, tx, localId) ?? throw new InvalidOperationException("Takma adın bağlanacağı yerel kategori bulunamadı.");
        if (target.Kind != TaxonomyKind.Category) throw new InvalidOperationException("Takma adlar yalnız kategorilere bağlanır.");
        if (!target.Active) throw new InvalidOperationException("Pasif kategoriye takma ad bağlanamaz.");
        if (Key(target.Name) == key) throw new InvalidOperationException("Takma ad kategorinin kendi adıyla aynı; gerek yok.");
        foreach (var other in Categories(c, tx)) if (other.Id != localId && Key(other.Name) == key) throw new InvalidOperationException($"'{text}' başka bir kategorinin adı ({other.Name}); takma ad olamaz.");
        using var find = c.CreateCommand(); find.Transaction = tx; find.CommandText = "SELECT LocalId FROM TaxonomyAliases WHERE Key=$key"; find.Parameters.AddWithValue("$key", key);
        if (find.ExecuteScalar() is string existing && existing != localId)
        {
            var holder = Entry(c, tx, existing);
            throw new InvalidOperationException($"'{text}' takma adı zaten başka bir kategoriye bağlı ({holder?.Name ?? "silinmiş kategori"}); önce oradan kaldırın.");
        }
        var now = DateTime.UtcNow;
        using var upsert = c.CreateCommand(); upsert.Transaction = tx;
        upsert.CommandText = "INSERT INTO TaxonomyAliases(Key,Alias,LocalId,Approved,Source,UpdatedUtc) VALUES($key,$alias,$local,$approved,$source,$updated) ON CONFLICT(Key) DO UPDATE SET Alias=excluded.Alias,Approved=excluded.Approved,Source=excluded.Source,UpdatedUtc=excluded.UpdatedUtc";
        upsert.Parameters.AddWithValue("$key", key); upsert.Parameters.AddWithValue("$alias", text); upsert.Parameters.AddWithValue("$local", localId); upsert.Parameters.AddWithValue("$approved", approved ? 1 : 0); upsert.Parameters.AddWithValue("$source", (source ?? "manual").Trim()); upsert.Parameters.AddWithValue("$updated", now.ToString("O", CultureInfo.InvariantCulture));
        upsert.ExecuteNonQuery(); tx.Commit();
        return new(text, key, localId, target.Name, approved, (source ?? "manual").Trim(), now, approved ? TaxonomyAliasView.ApprovedStatus : TaxonomyAliasView.Pending);
    }

    public IReadOnlyList<TaxonomyAliasView> List()
    {
        using var c = Open(); var categories = Categories(c, null).ToDictionary(e => e.Id, StringComparer.Ordinal);
        using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT Key,Alias,LocalId,Approved,Source,UpdatedUtc FROM TaxonomyAliases ORDER BY Alias";
        using var r = cmd.ExecuteReader(); var rows = new List<TaxonomyAliasView>();
        while (r.Read())
        {
            var localId = r.GetString(2); var approved = r.GetInt32(3) != 0; var category = categories.GetValueOrDefault(localId);
            var status = category is null || !category.Active ? TaxonomyAliasView.Orphan : approved ? TaxonomyAliasView.ApprovedStatus : TaxonomyAliasView.Pending;
            rows.Add(new(r.GetString(1), r.GetString(0), localId, category?.Name ?? "", approved, r.GetString(4), ParseUtc(r.GetString(5)), status));
        }
        return rows;
    }

    /// <summary>The category an approved alias names for a text — exact key match only; null for a pending alias, an orphan or no alias.</summary>
    public TaxonomyAliasMatch? Resolve(string? text)
    {
        var key = Key(text); if (key.Length == 0) return null;
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT LocalId FROM TaxonomyAliases WHERE Key=$key AND Approved=1"; cmd.Parameters.AddWithValue("$key", key);
        if (cmd.ExecuteScalar() is not string localId) return null;
        var entry = Entry(c, null, localId);
        return entry is { Active: true, Kind: TaxonomyKind.Category } ? new(entry.Id, entry.Name) : null;
    }

    public bool Remove(string alias)
    {
        var key = Key(alias); if (key.Length == 0) return false;
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "DELETE FROM TaxonomyAliases WHERE Key=$key"; cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Every approved alias with a live category, keyed for the import: key → the category's name.</summary>
    public IReadOnlyDictionary<string, string> ApprovedMap()
        => List().Where(a => a.Status == TaxonomyAliasView.ApprovedStatus).ToDictionary(a => a.Key, a => a.CategoryName, StringComparer.Ordinal);

    /// <summary>The canonical category name for a text under the approved map, or null when there is no exact alias — the only auto-apply there is.</summary>
    public static string? Match(IReadOnlyDictionary<string, string> approved, string? text)
    {
        ArgumentNullException.ThrowIfNull(approved);
        if (approved.Count == 0) return null;
        var key = Key(text);
        return key.Length > 0 && approved.TryGetValue(key, out var name) ? name : null;
    }

    static TaxonomyEntry? Entry(SqliteConnection c, SqliteTransaction? tx, string? id)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT Id,Kind,Name,Active FROM TaxonomyEntries WHERE Id=$id"; cmd.Parameters.AddWithValue("$id", id ?? "");
        using var r = cmd.ExecuteReader(); return r.Read() ? new TaxonomyEntry { Id = r.GetString(0), Kind = (TaxonomyKind)r.GetInt32(1), Name = r.GetString(2), Active = r.GetInt32(3) != 0 } : null;
    }

    static List<TaxonomyEntry> Categories(SqliteConnection c, SqliteTransaction? tx)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT Id,Kind,Name,Active FROM TaxonomyEntries WHERE Kind=$kind"; cmd.Parameters.AddWithValue("$kind", (int)TaxonomyKind.Category);
        using var r = cmd.ExecuteReader(); var rows = new List<TaxonomyEntry>(); while (r.Read()) rows.Add(new TaxonomyEntry { Id = r.GetString(0), Kind = (TaxonomyKind)r.GetInt32(1), Name = r.GetString(2), Active = r.GetInt32(3) != 0 }); return rows;
    }

    static DateTime ParseUtc(string value) => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) ? date : DateTime.MinValue;
}
