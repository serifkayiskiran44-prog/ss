using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;

namespace TrMarketplaceHubDesktop.Catalog;

/// <summary>One unmapped category value as the queue shows it: the value as the source wrote it, its key, the source, how often and when it was seen, a sample SKU, its state and, once resolved, the category.</summary>
public sealed record CategoryReviewItem(string SourceId, string Key, string Value, int Count, DateTime FirstSeenUtc, DateTime LastSeenUtc, string SampleSku, string Status, string ResolvedLocalId, string ResolvedName, DateTime? ResolvedUtc)
{
    public const string Pending = "PENDING", Approved = "APPROVED", Rejected = "REJECTED";
}

public sealed record CategoryReviewSighting(string Value, string Sku);

/// <summary>
/// The unmapped category review queue (#913). Every category value a feed writes that matches neither a local
/// category's name nor an approved alias (#912) lands in the queue with its evidence — the source it came from,
/// how many rows carried it, when it was first and last seen, one sample SKU — one row per source and value key
/// (two sources writing the same spelling are two rows: a source's evidence is its own). A repeated value grows
/// its count, never a second row. Approving a value binds it as an approved alias of a local category through the
/// dictionary (so the next run applies it) and marks the row; rejecting marks the row and keeps it out of the
/// pending list until the value is seen again. Bulk approve and reject act only on the exact items the operator
/// previewed, never on "everything pending". A value that has become mapped is resolved on its next sighting.
/// </summary>
public sealed class CategoryReviewQueue
{
    public const int ValueLimit = 300;
    readonly string directory; readonly string connectionString;

    public CategoryReviewQueue(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop"); Directory.CreateDirectory(directory);
        this.directory = directory;
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "catalog.db") }.ToString();
        _ = new TaxonomyStore(directory); // the entries table is the taxonomy owner's schema
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS CategoryReviewQueue(SourceId TEXT NOT NULL, Key TEXT NOT NULL, Value TEXT NOT NULL, Count INTEGER NOT NULL, FirstSeenUtc TEXT NOT NULL, LastSeenUtc TEXT NOT NULL, SampleSku TEXT NOT NULL DEFAULT '', Status TEXT NOT NULL, ResolvedLocalId TEXT NOT NULL DEFAULT '', ResolvedUtc TEXT NOT NULL DEFAULT '', PRIMARY KEY(SourceId,Key))";
        cmd.ExecuteNonQuery();
    }

    SqliteConnection Open() => SqliteConnectionPolicy.Open(connectionString);

    /// <summary>What the import needs to tell a mapped value from an unmapped one: every local category name and every approved alias, keyed.</summary>
    public IReadOnlyDictionary<string, string> KnownMap()
    {
        var known = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in new TaxonomyStore(directory).List(TaxonomyKind.Category).Where(e => e.Active)) known[TaxonomyAliasStore.Key(entry.Name)] = entry.Name;
        foreach (var (key, name) in new TaxonomyAliasStore(directory).ApprovedMap()) known.TryAdd(key, name);
        return known;
    }

    /// <summary>Records a run's sightings of unmapped values for a source: a new value is one pending row, a repeated one grows its count and moves its last-seen; an already resolved row seen again stays resolved, a rejected one becomes pending again. Returns the number of distinct values recorded.</summary>
    public int Record(string sourceId, IEnumerable<CategoryReviewSighting> sightings, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(sightings);
        if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("Kaynak kimliği gerekli.", nameof(sourceId));
        var grouped = sightings.Where(s => !string.IsNullOrWhiteSpace(s.Value)).GroupBy(s => TaxonomyAliasStore.Key(s.Value)).Where(g => g.Key.Length > 0).ToList();
        if (grouped.Count == 0) return 0;
        var now = nowUtc.ToString("O", CultureInfo.InvariantCulture);
        using var c = Open(); using var tx = c.BeginTransaction();
        foreach (var group in grouped)
        {
            var first = group.First(); var value = Limit(first.Value.Trim()); var sample = Limit((first.Sku ?? "").Trim(), 128);
            using var cmd = c.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO CategoryReviewQueue(SourceId,Key,Value,Count,FirstSeenUtc,LastSeenUtc,SampleSku,Status) VALUES($source,$key,$value,$count,$now,$now,$sample,$pending) ON CONFLICT(SourceId,Key) DO UPDATE SET Count=Count+excluded.Count,LastSeenUtc=excluded.LastSeenUtc,Value=excluded.Value,SampleSku=CASE WHEN SampleSku='' THEN excluded.SampleSku ELSE SampleSku END,Status=CASE WHEN Status=$rejected THEN $pending ELSE Status END";
            cmd.Parameters.AddWithValue("$source", sourceId); cmd.Parameters.AddWithValue("$key", group.Key); cmd.Parameters.AddWithValue("$value", value); cmd.Parameters.AddWithValue("$count", group.Count()); cmd.Parameters.AddWithValue("$now", now); cmd.Parameters.AddWithValue("$sample", sample); cmd.Parameters.AddWithValue("$pending", CategoryReviewItem.Pending); cmd.Parameters.AddWithValue("$rejected", CategoryReviewItem.Rejected);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return grouped.Count;
    }

    public IReadOnlyList<CategoryReviewItem> List(string? status = null, string? sourceId = null)
    {
        using var c = Open(); var names = new TaxonomyStore(directory).List(TaxonomyKind.Category).ToDictionary(e => e.Id, e => e.Name, StringComparer.Ordinal);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT SourceId,Key,Value,Count,FirstSeenUtc,LastSeenUtc,SampleSku,Status,ResolvedLocalId,ResolvedUtc FROM CategoryReviewQueue WHERE ($status IS NULL OR Status=$status) AND ($source IS NULL OR SourceId=$source) ORDER BY Count DESC, LastSeenUtc DESC";
        cmd.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value); cmd.Parameters.AddWithValue("$source", (object?)sourceId ?? DBNull.Value);
        using var r = cmd.ExecuteReader(); var rows = new List<CategoryReviewItem>();
        while (r.Read())
        {
            var resolved = r.GetString(8);
            rows.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), ParseUtc(r.GetString(4)), ParseUtc(r.GetString(5)), r.GetString(6), r.GetString(7), resolved, resolved.Length > 0 ? names.GetValueOrDefault(resolved, "silinmiş kategori") : "", r.GetString(9) is { Length: > 0 } at ? ParseUtc(at) : null));
        }
        return rows;
    }

    /// <summary>Approves one queued value as an approved alias of a local category — through the dictionary, with its rules — and marks the row; the value of that source only.</summary>
    public CategoryReviewItem Approve(string sourceId, string key, string localId, DateTime nowUtc)
    {
        var item = Find(sourceId, key) ?? throw new InvalidOperationException("Kuyrukta böyle bir değer yok.");
        new TaxonomyAliasStore(directory).Save(item.Value, localId, approved: true, source: "review:" + sourceId);
        Mark(sourceId, key, CategoryReviewItem.Approved, localId, nowUtc);
        return Find(sourceId, key)!;
    }

    public CategoryReviewItem Reject(string sourceId, string key, DateTime nowUtc)
    {
        _ = Find(sourceId, key) ?? throw new InvalidOperationException("Kuyrukta böyle bir değer yok.");
        Mark(sourceId, key, CategoryReviewItem.Rejected, "", nowUtc);
        return Find(sourceId, key)!;
    }

    /// <summary>Bulk approve: exactly the previewed items, all to one category, refused without the preview's confirmation; every item is checked before the first is written, so a conflict leaves the queue untouched.</summary>
    public int ApproveBulk(IReadOnlyList<CategoryReviewItem> items, string localId, bool confirmed, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (!confirmed) throw new InvalidOperationException("Toplu onay için önizleme onayı gerekli.");
        if (items.Count == 0) return 0;
        var aliases = new TaxonomyAliasStore(directory);
        foreach (var item in items)
        {
            if (Find(item.SourceId, item.Key) is not { Status: CategoryReviewItem.Pending }) throw new InvalidOperationException($"'{item.Value}' artık beklemede değil; listeyi yenileyin.");
            if (aliases.Refusal(item.Value, localId) is { } why) throw new InvalidOperationException(why); // checked before anything is written: a refused item leaves the dictionary and the queue as they were
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items) if (seen.Add(item.Key)) aliases.Save(item.Value, localId, approved: true, source: "review:" + item.SourceId);
        foreach (var item in items) Mark(item.SourceId, item.Key, CategoryReviewItem.Approved, localId, nowUtc);
        return items.Count;
    }

    public int RejectBulk(IReadOnlyList<CategoryReviewItem> items, bool confirmed, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (!confirmed) throw new InvalidOperationException("Toplu ret için önizleme onayı gerekli.");
        var done = 0;
        foreach (var item in items) if (Find(item.SourceId, item.Key) is { Status: CategoryReviewItem.Pending }) { Mark(item.SourceId, item.Key, CategoryReviewItem.Rejected, "", nowUtc); done++; }
        return done;
    }

    public CategoryReviewItem? Find(string sourceId, string key) => List(null, sourceId).FirstOrDefault(i => i.Key == key);

    void Mark(string sourceId, string key, string status, string localId, DateTime nowUtc)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE CategoryReviewQueue SET Status=$status,ResolvedLocalId=$local,ResolvedUtc=$at WHERE SourceId=$source AND Key=$key";
        cmd.Parameters.AddWithValue("$status", status); cmd.Parameters.AddWithValue("$local", localId ?? ""); cmd.Parameters.AddWithValue("$at", nowUtc.ToString("O", CultureInfo.InvariantCulture)); cmd.Parameters.AddWithValue("$source", sourceId); cmd.Parameters.AddWithValue("$key", key);
        cmd.ExecuteNonQuery();
    }

    static string Limit(string value, int max = ValueLimit) => value.Length > max ? value[..max] : value;
    static DateTime ParseUtc(string value) => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) ? date : DateTime.MinValue;
}
