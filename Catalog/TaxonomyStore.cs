using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;
using System.Text;

namespace TrMarketplaceHubDesktop.Catalog;

public enum TaxonomyKind { Category, Brand, Attribute }
public sealed class TaxonomyEntry { public string Id { get; set; } = Guid.NewGuid().ToString("N"); public TaxonomyKind Kind { get; set; } public string Name { get; set; } = ""; public string Value { get; set; } = ""; public bool Active { get; set; } = true; public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow; }
public sealed record TaxonomyMapping(TaxonomyKind Kind, string Marketplace, string ShopId, string ExternalKey, string LocalId, int Version = 0);
/// Raised by Map(...,expectedVersion) when the mapping row for this exact key has
/// moved on (edited/created/removed by another editor) since the caller's
/// snapshot was taken - the write is rejected rather than silently overwriting
/// whatever the other editor just wrote.
public sealed class TaxonomyMappingConflictException : Exception
{
    public TaxonomyMappingConflictException(string message) : base(message) { }
}
public sealed record TaxonomyMappingView(TaxonomyKind Kind, string Marketplace, string ShopId, string ExternalKey, string LocalId, string LocalName, string Status, DateTime UpdatedUtc);
public sealed record TaxonomySuggestion(string ExternalKey, string? LocalId, string? LocalName, string Status);
public sealed record TaxonomyMappingHistoryRecord(DateTime ChangedUtc, TaxonomyKind Kind, string Marketplace, string ShopId, string ExternalKey, string LocalId, string Action);
public sealed record TaxonomyUsage(int Products, int Mappings) { public int Total => Products + Mappings; }
public enum TaxonomyResolution { NotMapped, Ready, TargetInactive, TargetMissing }
public sealed record TaxonomyResolutionResult(TaxonomyResolution Status, string? LocalId, string? LocalName);
/// Bounded diagnostics only (id/kind, a short reason, detection time) - never
/// Name/Value/ExternalKey/LocalId - for a persisted timestamp that failed to parse.
/// See CatalogStore's CorruptProductRow for the identical pattern applied to products.
public sealed record CorruptTaxonomyEntry(string Id, TaxonomyKind Kind, string Reason, DateTime DetectedUtc);
public sealed record CorruptTaxonomyHistoryRow(string Id, TaxonomyKind Kind, string Reason, DateTime DetectedUtc);

public sealed class TaxonomyStore
{
    readonly string connectionString;
    public TaxonomyStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop"); Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "catalog.db") }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS TaxonomyEntries(Id TEXT PRIMARY KEY, Kind INTEGER NOT NULL, Name TEXT NOT NULL, Value TEXT NOT NULL, Active INTEGER NOT NULL, UpdatedUtc TEXT NOT NULL DEFAULT '');CREATE UNIQUE INDEX IF NOT EXISTS UX_TaxonomyEntries_KindNameValue ON TaxonomyEntries(Kind,Name,Value);CREATE TABLE IF NOT EXISTS TaxonomyMappings(Kind INTEGER NOT NULL, Marketplace TEXT NOT NULL DEFAULT 'local', ShopId TEXT NOT NULL DEFAULT 'default', ExternalKey TEXT NOT NULL, LocalId TEXT NOT NULL, UpdatedUtc TEXT NOT NULL DEFAULT '', PRIMARY KEY(Kind,Marketplace,ShopId,ExternalKey));CREATE TABLE IF NOT EXISTS TaxonomyMappingHistory(Id TEXT PRIMARY KEY, Kind INTEGER NOT NULL, Marketplace TEXT NOT NULL, ShopId TEXT NOT NULL, ExternalKey TEXT NOT NULL, LocalId TEXT NOT NULL, Action TEXT NOT NULL, ChangedUtc TEXT NOT NULL)";
        cmd.ExecuteNonQuery(); EnsureColumn(c, "TaxonomyEntries", "UpdatedUtc", "TEXT NOT NULL DEFAULT ''"); EnsureColumn(c, "TaxonomyMappings", "UpdatedUtc", "TEXT NOT NULL DEFAULT ''"); EnsureColumn(c, "TaxonomyMappings", "Version", "INTEGER NOT NULL DEFAULT 0");
    }
    static void EnsureColumn(SqliteConnection c, string table, string name, string definition) { using var check = c.CreateCommand(); check.CommandText = $"SELECT 1 FROM pragma_table_info('{table}') WHERE name=$name"; check.Parameters.AddWithValue("$name", name); if (check.ExecuteScalar() is not null) return; using var add = c.CreateCommand(); add.CommandText = $"ALTER TABLE {table} ADD COLUMN {name} {definition}"; add.ExecuteNonQuery(); }
    SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); return c; }
    /// The only defensible boundary against a future/corrupt persisted Kind value:
    /// TaxonomyKind has no [Flags] and no reserved gaps, so any value outside the
    /// three declared members is definitionally unsupported, not just "unexpected".
    static bool IsValidKind(TaxonomyKind kind) => kind is TaxonomyKind.Category or TaxonomyKind.Brand or TaxonomyKind.Attribute;
    static void ValidateKind(TaxonomyKind kind) { if (!IsValidKind(kind)) throw new ArgumentException($"Tanımsız taxonomy türü: {(int)kind}."); }
    static void Validate(TaxonomyEntry entry) { ValidateKind(entry.Kind); entry.Name = entry.Name.Trim(); entry.Value = entry.Value.Trim(); if (entry.Name.Length == 0 || entry.Name.Length > 200) throw new InvalidOperationException("Kategori, marka veya özellik adı 1-200 karakter olmalı."); if (entry.Value.Length > 200) throw new InvalidOperationException("Özellik değeri en fazla 200 karakter olabilir."); }
    public TaxonomyEntry Save(TaxonomyEntry entry)
    {
        Validate(entry); entry.UpdatedUtc = DateTime.UtcNow; using var c = Open();
        // SQLite's built-in lower()/NOCASE only fold ASCII A-Z, so a duplicate check
        // done in SQL misses non-ASCII case pairs (e.g. Turkish "Ş" vs "ş"). Compare
        // in .NET with OrdinalIgnoreCase (simple Unicode case folding) instead.
        using (var check = c.CreateCommand())
        {
            check.CommandText = "SELECT Id,Name,Value FROM TaxonomyEntries WHERE Kind=$kind AND Id<>$id"; check.Parameters.AddWithValue("$kind", (int)entry.Kind); check.Parameters.AddWithValue("$id", entry.Id);
            using var r = check.ExecuteReader();
            while (r.Read())
                if (string.Equals(r.GetString(1), entry.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(r.GetString(2), entry.Value, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Aynı türde aynı ad/değer zaten var.");
        }
        using var cmd = c.CreateCommand(); cmd.CommandText = "INSERT INTO TaxonomyEntries(Id,Kind,Name,Value,Active,UpdatedUtc) VALUES($id,$kind,$name,$value,$active,$updated) ON CONFLICT(Id) DO UPDATE SET Kind=excluded.Kind,Name=excluded.Name,Value=excluded.Value,Active=excluded.Active,UpdatedUtc=excluded.UpdatedUtc"; cmd.Parameters.AddWithValue("$id", entry.Id); cmd.Parameters.AddWithValue("$kind", (int)entry.Kind); cmd.Parameters.AddWithValue("$name", entry.Name); cmd.Parameters.AddWithValue("$value", entry.Value); cmd.Parameters.AddWithValue("$active", entry.Active ? 1 : 0); cmd.Parameters.AddWithValue("$updated", entry.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture)); cmd.ExecuteNonQuery(); return entry;
    }
    /// Brand/Category identity lives in TaxonomyEntries.Id, independent of the
    /// user-editable display Name - renaming an entry (Save keeps the same Id) never
    /// changes its identity or breaks existing TaxonomyMappings/history rows that
    /// reference the Id, only CatalogProducts.Brand/Category (free text, matched by
    /// current name) need the name itself to stay in sync with actual product data.
    public TaxonomyUsage Usage(TaxonomyKind kind, TaxonomyEntry entry)
    {
        ValidateKind(kind); using var c = Open();
        return new(CountProductUsage(c, null, kind, entry.Name), CountMappingUsage(c, null, kind, entry.Id));
    }
    static int CountProductUsage(SqliteConnection c, SqliteTransaction? tx, TaxonomyKind kind, string name)
    {
        if (kind is not (TaxonomyKind.Brand or TaxonomyKind.Category)) return 0;
        var column = kind == TaxonomyKind.Brand ? "Brand" : "Category";
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = $"SELECT COUNT(*) FROM CatalogProducts WHERE lower(json_extract(Json,'$.{column}'))=lower($name)";
        cmd.Parameters.AddWithValue("$name", name);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
    static int CountMappingUsage(SqliteConnection c, SqliteTransaction? tx, TaxonomyKind kind, string id)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM TaxonomyMappings WHERE Kind=$kind AND LocalId=$id";
        cmd.Parameters.AddWithValue("$kind", (int)kind); cmd.Parameters.AddWithValue("$id", id);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
    /// Hard-delete is fail-closed and never cascades: usage is re-checked inside the
    /// same write transaction as the delete (not a separate earlier read) so a
    /// concurrently added product/mapping cannot slip through a stale check, and SQLite
    /// serializes the concurrent writer against this transaction rather than losing it.
    public void Delete(TaxonomyKind kind, string id)
    {
        ValidateKind(kind); using var c = Open(); using var tx = c.BeginTransaction();
        using var find = c.CreateCommand(); find.Transaction = tx; find.CommandText = "SELECT Name FROM TaxonomyEntries WHERE Id=$id AND Kind=$kind"; find.Parameters.AddWithValue("$id", id); find.Parameters.AddWithValue("$kind", (int)kind);
        var name = find.ExecuteScalar() as string ?? throw new InvalidOperationException("Silinecek kayıt bulunamadı.");
        var products = CountProductUsage(c, tx, kind, name);
        var mappings = CountMappingUsage(c, tx, kind, id);
        if (products > 0 || mappings > 0) throw new InvalidOperationException($"Bu kayıt kullanımda olduğu için silinemez: {products} üründe, {mappings} kanal eşlemesinde kullanılıyor. Önce bağlantıları kaldırın veya kaydı pasife alın.");
        using var delete = c.CreateCommand(); delete.Transaction = tx; delete.CommandText = "DELETE FROM TaxonomyEntries WHERE Id=$id"; delete.Parameters.AddWithValue("$id", id);
        if (delete.ExecuteNonQuery() != 1) throw new InvalidOperationException("Silinecek kayıt bulunamadı.");
        tx.Commit();
    }
    /// A malformed persisted UpdatedUtc must never resolve to DateTime.MinValue and
    /// pass as a normal (if very old) entry - that would let a mapping freshness
    /// check (MappingViews) misclassify it as STALE based on a fabricated date. This
    /// row is instead excluded from List() and reported only via CorruptEntries();
    /// re-detection runs from the row's own stored text every call, so review state
    /// naturally survives a restart.
    public IReadOnlyList<TaxonomyEntry> List(TaxonomyKind kind)
    {
        ValidateKind(kind);
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT Id,Kind,Name,Value,Active,UpdatedUtc FROM TaxonomyEntries WHERE Kind=$kind ORDER BY Name,Value"; cmd.Parameters.AddWithValue("$kind", (int)kind);
        using var r = cmd.ExecuteReader(); var result = new List<TaxonomyEntry>();
        while (r.Read()) if (TryReadEntry(r, out var entry, out _)) result.Add(entry!);
        return result;
    }
    public IReadOnlyList<CorruptTaxonomyEntry> CorruptEntries(TaxonomyKind kind)
    {
        ValidateKind(kind);
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT Id,Kind,Name,Value,Active,UpdatedUtc FROM TaxonomyEntries WHERE Kind=$kind"; cmd.Parameters.AddWithValue("$kind", (int)kind);
        using var r = cmd.ExecuteReader(); var result = new List<CorruptTaxonomyEntry>();
        while (r.Read()) if (!TryReadEntry(r, out _, out var corrupt)) result.Add(corrupt!);
        return result;
    }
    /// Unlike CorruptEntries(kind), this scans every row regardless of Kind - the
    /// only way to surface a row whose persisted Kind is outside the three declared
    /// enum members, since every other read path filters `WHERE Kind=$kind` on an
    /// already-validated (therefore always-legitimate) value and would never select
    /// such a row in the first place. Never auto-deletes or remaps it.
    public IReadOnlyList<CorruptTaxonomyEntry> CorruptKindEntries()
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT Id,Kind,Name,Value,Active,UpdatedUtc FROM TaxonomyEntries";
        using var r = cmd.ExecuteReader(); var result = new List<CorruptTaxonomyEntry>();
        while (r.Read())
        {
            var rawKind = r.GetInt32(1);
            if (!IsValidKind((TaxonomyKind)rawKind)) result.Add(new(r.GetString(0), (TaxonomyKind)rawKind, $"Unknown Kind value: {rawKind}", DateTime.UtcNow));
        }
        return result;
    }
    static bool TryReadEntry(SqliteDataReader r, out TaxonomyEntry? entry, out CorruptTaxonomyEntry? corrupt)
    {
        entry = null; corrupt = null; var id = r.GetString(0); var rawKind = r.GetInt32(1);
        if (!IsValidKind((TaxonomyKind)rawKind)) { corrupt = new(id, (TaxonomyKind)rawKind, $"Unknown Kind value: {rawKind}", DateTime.UtcNow); return false; }
        var kind = (TaxonomyKind)rawKind;
        if (!TryParseUtc(r.GetString(5), out var updated)) { corrupt = new(id, kind, "Malformed UpdatedUtc timestamp", DateTime.UtcNow); return false; }
        entry = new() { Id = id, Kind = kind, Name = r.GetString(2), Value = r.GetString(3), Active = r.GetInt32(4) != 0, UpdatedUtc = updated };
        return true;
    }
    /// Only ever a format/parse failure - never conflated with a DB-busy/locked
    /// SqliteException, which is raised (and propagates) by the surrounding command,
    /// not by this pure string parse.
    static bool TryParseUtc(string value, out DateTime result) => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out result);
    /// expectedVersion, when given, must equal the mapping row's current Version (0
    /// for "no mapping yet") or the write is rejected with
    /// TaxonomyMappingConflictException instead of silently overwriting whatever
    /// another editor/import just wrote for this exact (Kind,Marketplace,ShopId,
    /// ExternalKey) key - closing the last-writer-wins race between two concurrent
    /// edits (or a first-create race, where both expect version 0). Omitting it
    /// keeps the previous unconditional-upsert behavior for callers (e.g. MapBulk)
    /// that do not carry a per-row snapshot to check against.
    public void Map(TaxonomyKind kind, string externalKey, string localId, string marketplace = "local", string shopId = "default", int? expectedVersion = null)
    {
        ValidateKind(kind); ValidateExternalKey(externalKey); if (string.IsNullOrWhiteSpace(localId) || string.IsNullOrWhiteSpace(marketplace) || string.IsNullOrWhiteSpace(shopId)) throw new InvalidOperationException("Pazaryeri, mağaza, harici anahtar ve yerel eşleme zorunlu.");
        var now = DateTime.UtcNow; using var c = Open(); using var tx = c.BeginTransaction(); using var exists = c.CreateCommand(); exists.Transaction = tx; exists.CommandText = "SELECT Active FROM TaxonomyEntries WHERE Id=$id AND Kind=$kind"; exists.Parameters.AddWithValue("$id", localId); exists.Parameters.AddWithValue("$kind", (int)kind); var active = exists.ExecuteScalar(); if (active is null) throw new InvalidOperationException("Eşlenecek yerel kayıt bulunamadı."); if (Convert.ToInt32(active) == 0) throw new InvalidOperationException("Pasif yerel kayıt eşlenemez.");
        var market = marketplace.Trim().ToLowerInvariant(); var shop = shopId.Trim(); var key = externalKey.Trim();
        int currentVersion;
        using (var find = c.CreateCommand())
        {
            find.Transaction = tx; find.CommandText = "SELECT Version FROM TaxonomyMappings WHERE Kind=$kind AND Marketplace=$market AND ShopId=$shop AND ExternalKey=$key";
            find.Parameters.AddWithValue("$kind", (int)kind); find.Parameters.AddWithValue("$market", market); find.Parameters.AddWithValue("$shop", shop); find.Parameters.AddWithValue("$key", key);
            var stored = find.ExecuteScalar(); currentVersion = stored is null ? 0 : Convert.ToInt32(stored);
        }
        if (expectedVersion.HasValue && currentVersion != expectedVersion.Value) throw new TaxonomyMappingConflictException($"Bu eşleme ({market}/{shop}/{key}) başka bir işlemde değişti; listeyi yenileyip tekrar deneyin.");
        var nextVersion = currentVersion + 1;
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "INSERT INTO TaxonomyMappings(Kind,Marketplace,ShopId,ExternalKey,LocalId,UpdatedUtc,Version) VALUES($kind,$market,$shop,$key,$id,$updated,$version) ON CONFLICT(Kind,Marketplace,ShopId,ExternalKey) DO UPDATE SET LocalId=excluded.LocalId,UpdatedUtc=excluded.UpdatedUtc,Version=excluded.Version"; cmd.Parameters.AddWithValue("$kind", (int)kind); cmd.Parameters.AddWithValue("$market", market); cmd.Parameters.AddWithValue("$shop", shop); cmd.Parameters.AddWithValue("$key", key); cmd.Parameters.AddWithValue("$id", localId); cmd.Parameters.AddWithValue("$updated", now.ToString("O", CultureInfo.InvariantCulture)); cmd.Parameters.AddWithValue("$version", nextVersion); cmd.ExecuteNonQuery(); using var history = c.CreateCommand(); history.Transaction = tx; history.CommandText = "INSERT INTO TaxonomyMappingHistory VALUES($id,$kind,$market,$shop,$key,$local,$action,$at)"; history.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N")); history.Parameters.AddWithValue("$kind", (int)kind); history.Parameters.AddWithValue("$market", market); history.Parameters.AddWithValue("$shop", shop); history.Parameters.AddWithValue("$key", key); history.Parameters.AddWithValue("$local", localId); history.Parameters.AddWithValue("$action", "UPSERT"); history.Parameters.AddWithValue("$at", now.ToString("O", CultureInfo.InvariantCulture)); history.ExecuteNonQuery(); tx.Commit();
    }
    static void ValidateExternalKey(string value) { if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 300 || value.Any(char.IsControl)) throw new InvalidOperationException("Harici eşleme anahtarı boş, çok uzun veya kontrol karakteri içeriyor."); }
    public void MapBulk(TaxonomyKind kind, string marketplace, string shopId, IReadOnlyList<TaxonomySuggestion> suggestions, bool approved) { if (!approved) throw new InvalidOperationException("Toplu kategori/marka/özellik eşlemesi için önizleme onayı gerekli."); foreach (var suggestion in suggestions.Where(x => x.LocalId is not null)) Map(kind, suggestion.ExternalKey, suggestion.LocalId!, marketplace, shopId); }
    public string? Resolve(TaxonomyKind kind, string externalKey, string marketplace = "local", string shopId = "default") { ValidateKind(kind); using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT LocalId FROM TaxonomyMappings WHERE Kind=$kind AND Marketplace=$market AND ShopId=$shop AND ExternalKey=$key"; cmd.Parameters.AddWithValue("$kind", (int)kind); cmd.Parameters.AddWithValue("$market", marketplace.Trim().ToLowerInvariant()); cmd.Parameters.AddWithValue("$shop", shopId.Trim()); cmd.Parameters.AddWithValue("$key", externalKey.Trim()); return cmd.ExecuteScalar() as string; }
    /// Explicit remove, distinct from Map's create/update upsert: the CRUD lifecycle
    /// was missing a way to delete a channel/shop mapping entirely.
    public void Unmap(TaxonomyKind kind, string externalKey, string marketplace = "local", string shopId = "default")
    {
        ValidateKind(kind); var market = marketplace.Trim().ToLowerInvariant(); var shop = shopId.Trim(); var key = externalKey.Trim();
        using var c = Open(); using var tx = c.BeginTransaction();
        using var find = c.CreateCommand(); find.Transaction = tx; find.CommandText = "SELECT LocalId FROM TaxonomyMappings WHERE Kind=$kind AND Marketplace=$market AND ShopId=$shop AND ExternalKey=$key"; find.Parameters.AddWithValue("$kind", (int)kind); find.Parameters.AddWithValue("$market", market); find.Parameters.AddWithValue("$shop", shop); find.Parameters.AddWithValue("$key", key);
        var localId = find.ExecuteScalar() as string ?? throw new InvalidOperationException("Kaldırılacak eşleme bulunamadı.");
        using var delete = c.CreateCommand(); delete.Transaction = tx; delete.CommandText = "DELETE FROM TaxonomyMappings WHERE Kind=$kind AND Marketplace=$market AND ShopId=$shop AND ExternalKey=$key"; delete.Parameters.AddWithValue("$kind", (int)kind); delete.Parameters.AddWithValue("$market", market); delete.Parameters.AddWithValue("$shop", shop); delete.Parameters.AddWithValue("$key", key); delete.ExecuteNonQuery();
        var now = DateTime.UtcNow;
        using var history = c.CreateCommand(); history.Transaction = tx; history.CommandText = "INSERT INTO TaxonomyMappingHistory VALUES($id,$kind,$market,$shop,$key,$local,$action,$at)"; history.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N")); history.Parameters.AddWithValue("$kind", (int)kind); history.Parameters.AddWithValue("$market", market); history.Parameters.AddWithValue("$shop", shop); history.Parameters.AddWithValue("$key", key); history.Parameters.AddWithValue("$local", localId); history.Parameters.AddWithValue("$action", "REMOVE"); history.Parameters.AddWithValue("$at", now.ToString("O", CultureInfo.InvariantCulture)); history.ExecuteNonQuery();
        tx.Commit();
    }
    /// Resolve alone only tells the caller a mapping row exists; it never checked
    /// whether the local target it points to is still usable. ResolveForUse is the
    /// single owner for that usability preflight: NotMapped (no mapping row),
    /// TargetMissing (mapping points at a deleted/unknown entry - fail closed),
    /// TargetInactive (entry exists but deactivated - needs review/reactivation), or
    /// Ready. Live dispatch/preview code should call this instead of raw Resolve.
    public TaxonomyResolutionResult ResolveForUse(TaxonomyKind kind, string externalKey, string marketplace = "local", string shopId = "default")
    {
        var localId = Resolve(kind, externalKey, marketplace, shopId);
        if (localId is null) return new(TaxonomyResolution.NotMapped, null, null);
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Name,Active FROM TaxonomyEntries WHERE Id=$id AND Kind=$kind"; cmd.Parameters.AddWithValue("$id", localId); cmd.Parameters.AddWithValue("$kind", (int)kind);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return new(TaxonomyResolution.TargetMissing, localId, null);
        var name = r.GetString(0); var active = r.GetInt32(1) != 0;
        return active ? new(TaxonomyResolution.Ready, localId, name) : new(TaxonomyResolution.TargetInactive, localId, name);
    }
    public IReadOnlyList<TaxonomyMapping> Mappings(TaxonomyKind kind) { ValidateKind(kind); using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT Marketplace,ShopId,ExternalKey,LocalId,Version FROM TaxonomyMappings WHERE Kind=$kind ORDER BY Marketplace,ShopId,ExternalKey"; cmd.Parameters.AddWithValue("$kind", (int)kind); using var r = cmd.ExecuteReader(); var result = new List<TaxonomyMapping>(); while (r.Read()) result.Add(new(kind, r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4))); return result; }
    /// The current Version for a mapping key (0 if none exists yet) - a caller
    /// snapshots this immediately before editing and passes it back to
    /// Map(...,expectedVersion) so a stale edit is rejected instead of silently
    /// overwriting a concurrent change.
    public int GetMappingVersion(TaxonomyKind kind, string externalKey, string marketplace = "local", string shopId = "default")
    {
        ValidateKind(kind); using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT Version FROM TaxonomyMappings WHERE Kind=$kind AND Marketplace=$market AND ShopId=$shop AND ExternalKey=$key";
        cmd.Parameters.AddWithValue("$kind", (int)kind); cmd.Parameters.AddWithValue("$market", marketplace.Trim().ToLowerInvariant()); cmd.Parameters.AddWithValue("$shop", shopId.Trim()); cmd.Parameters.AddWithValue("$key", externalKey.Trim());
        var stored = cmd.ExecuteScalar(); return stored is null ? 0 : Convert.ToInt32(stored);
    }
    public IReadOnlyList<TaxonomyMappingView> MappingViews(TaxonomyKind kind, string marketplace, string shopId, string? query = null)
    {
        ValidateKind(kind); var entries = List(kind).ToDictionary(x => x.Id); var mappings = Mappings(kind).Where(x => x.Marketplace == marketplace.Trim().ToLowerInvariant() && x.ShopId == shopId.Trim()).ToDictionary(x => x.ExternalKey, StringComparer.OrdinalIgnoreCase); var (updated, corruptTimestamps) = MappingUpdated(kind, marketplace, shopId); var rows = new List<TaxonomyMappingView>();
        foreach (var entry in entries.Values)
        {
            var mapping = mappings.Values.FirstOrDefault(x => x.LocalId == entry.Id); var external = mapping?.ExternalKey ?? "";
            if (!string.IsNullOrWhiteSpace(query) && !($"{entry.Name} {entry.Value} {external}").Contains(query, StringComparison.CurrentCultureIgnoreCase)) continue;
            var when = updated.GetValueOrDefault(external);
            // An unparsable mapping timestamp must never be treated as "very old": that
            // would silently force STALE. It gets its own explicit review state instead.
            var state = mapping is null ? "MISSING" : corruptTimestamps.Contains(external) ? "REVIEW_REQUIRED" : !entry.Active ? "INVALID" : when < DateTime.UtcNow.AddDays(-180) ? "STALE" : "MAPPED";
            rows.Add(new(kind, marketplace.Trim().ToLowerInvariant(), shopId.Trim(), external, mapping?.LocalId ?? entry.Id, entry.Name, state, when));
        }
        return rows.OrderBy(x => x.Status).ThenBy(x => x.LocalName).ToList();
    }
    (Dictionary<string, DateTime> Updated, HashSet<string> Corrupt) MappingUpdated(TaxonomyKind kind, string marketplace, string shopId)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT ExternalKey,UpdatedUtc FROM TaxonomyMappings WHERE Kind=$kind AND Marketplace=$market AND ShopId=$shop"; cmd.Parameters.AddWithValue("$kind", (int)kind); cmd.Parameters.AddWithValue("$market", marketplace.Trim().ToLowerInvariant()); cmd.Parameters.AddWithValue("$shop", shopId.Trim());
        using var r = cmd.ExecuteReader(); var result = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase); var corrupt = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (r.Read()) { var key = r.GetString(0); if (TryParseUtc(r.GetString(1), out var when)) result[key] = when; else corrupt.Add(key); }
        return (result, corrupt);
    }
    /// A row whose ChangedUtc can't be parsed is excluded rather than injected into
    /// the chronology as a fabricated DateTime.MinValue entry; see CorruptHistory() for
    /// the bounded diagnostics covering exactly those excluded rows.
    public IReadOnlyList<TaxonomyMappingHistoryRecord> History(TaxonomyKind kind, string marketplace, string shopId, int limit = 100)
    {
        ValidateKind(kind); if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT ChangedUtc,Kind,Marketplace,ShopId,ExternalKey,LocalId,Action FROM TaxonomyMappingHistory WHERE Kind=$kind AND Marketplace=$market AND ShopId=$shop ORDER BY ChangedUtc DESC LIMIT $limit"; cmd.Parameters.AddWithValue("$kind", (int)kind); cmd.Parameters.AddWithValue("$market", marketplace.Trim().ToLowerInvariant()); cmd.Parameters.AddWithValue("$shop", shopId.Trim()); cmd.Parameters.AddWithValue("$limit", limit);
        using var r = cmd.ExecuteReader(); var result = new List<TaxonomyMappingHistoryRecord>();
        while (r.Read()) if (TryParseUtc(r.GetString(0), out var changed)) result.Add(new(changed, (TaxonomyKind)r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6)));
        return result;
    }
    public IReadOnlyList<CorruptTaxonomyHistoryRow> CorruptHistory(TaxonomyKind kind, string marketplace, string shopId)
    {
        ValidateKind(kind); using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT Id,Kind,ChangedUtc FROM TaxonomyMappingHistory WHERE Kind=$kind AND Marketplace=$market AND ShopId=$shop"; cmd.Parameters.AddWithValue("$kind", (int)kind); cmd.Parameters.AddWithValue("$market", marketplace.Trim().ToLowerInvariant()); cmd.Parameters.AddWithValue("$shop", shopId.Trim());
        using var r = cmd.ExecuteReader(); var result = new List<CorruptTaxonomyHistoryRow>();
        while (r.Read()) if (!TryParseUtc(r.GetString(2), out _)) result.Add(new(r.GetString(0), (TaxonomyKind)r.GetInt32(1), "Malformed ChangedUtc timestamp", DateTime.UtcNow));
        return result;
    }
    public IReadOnlyList<TaxonomySuggestion> SuggestBulk(TaxonomyKind kind, IEnumerable<string> externalKeys) { var entries = List(kind).Where(x => x.Active).ToList(); return externalKeys.Select(key => key.Trim()).Where(key => key.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Select(key => { var normalized = Normalize(key); var best = entries.Select(entry => (entry, score: Score(normalized, Normalize(entry.Name)))).Where(x => x.score > 0).OrderByDescending(x => x.score).FirstOrDefault(); return best.entry is null || best.score < 50 ? new TaxonomySuggestion(key, null, null, "UNMATCHED") : new TaxonomySuggestion(key, best.entry.Id, best.entry.Name, "SUGGESTED"); }).ToList(); }
    static int Score(string left, string right) => left == right ? 100 : right.StartsWith(left, StringComparison.Ordinal) || left.StartsWith(right, StringComparison.Ordinal) ? 75 : left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal) ? 50 : 0;
    static string Normalize(string value) { var form = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD); var filtered = new string(form.Where(x => CharUnicodeInfo.GetUnicodeCategory(x) != UnicodeCategory.NonSpacingMark && (char.IsLetterOrDigit(x) || char.IsWhiteSpace(x))).ToArray()); return string.Join(' ', filtered.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)); }
}
