using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;

namespace TrMarketplaceHubDesktop.Catalog;

public sealed record BrandAliasView(string Alias, string Key, string BrandId, string BrandName, bool Approved, string Source, DateTime UpdatedUtc, string Status)
{
    public const string ApprovedStatus = "APPROVED", Pending = "PENDING", Orphan = "ORPHAN";
}

/// <summary>A suggested canonical brand for a supplier's string, with the confidence and the reason it was suggested — never applied by itself.</summary>
public sealed record BrandSuggestion(string BrandId, string BrandName, int Confidence, string Reason)
{
    public const int Exact = 100, Typo = 90, Partial = 60;
}

public sealed record BrandReviewItem(string SourceId, string Key, string Value, int Count, DateTime FirstSeenUtc, DateTime LastSeenUtc, string SampleSku, string Status, string SuggestedBrandId, string SuggestedBrand, int Confidence, string Reason, string RejectedBrandId)
{
    public const string Pending = "PENDING", Approved = "APPROVED", Rejected = "REJECTED";
}

public sealed record BrandSighting(string Value, string Sku);
public sealed record BrandDuplicate(string Key, IReadOnlyList<string> Names);

/// <summary>
/// Brand mapping review queue (#917). A supplier writes "acme", "ACME Ltd." or "Acmee" for the local brand "Acme".
/// A brand string a feed writes that is neither a local brand's name nor an approved brand alias lands in the
/// queue with its evidence (source, count, first and last seen, a sample SKU) and a suggestion with its confidence
/// and reason — the same name in another casing (100), a spelling one or two letters away (90), one name
/// containing the other (60) — or no suggestion at all. Nothing merges silently: only an approved alias on an exact
/// key is applied by the import, and a suggestion becomes an alias only when the operator approves it. A rejected
/// suggestion is remembered as a false positive: that brand is not suggested again for that string. Two local
/// brands whose names fold to the same key are duplicate canonicals and are reported, never picked between.
/// </summary>
public sealed class BrandMappingStore
{
    public const int ValueLimit = 200;
    readonly string directory; readonly string connectionString;

    public BrandMappingStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop"); Directory.CreateDirectory(directory);
        this.directory = directory;
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "catalog.db") }.ToString();
        _ = new TaxonomyStore(directory); // the entries table is the taxonomy owner's schema
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS BrandAliases(Key TEXT PRIMARY KEY, Alias TEXT NOT NULL, BrandId TEXT NOT NULL, Approved INTEGER NOT NULL, Source TEXT NOT NULL DEFAULT 'manual', UpdatedUtc TEXT NOT NULL);CREATE TABLE IF NOT EXISTS BrandReviewQueue(SourceId TEXT NOT NULL, Key TEXT NOT NULL, Value TEXT NOT NULL, Count INTEGER NOT NULL, FirstSeenUtc TEXT NOT NULL, LastSeenUtc TEXT NOT NULL, SampleSku TEXT NOT NULL DEFAULT '', Status TEXT NOT NULL, SuggestedBrandId TEXT NOT NULL DEFAULT '', Confidence INTEGER NOT NULL DEFAULT 0, Reason TEXT NOT NULL DEFAULT '', RejectedBrandId TEXT NOT NULL DEFAULT '', PRIMARY KEY(SourceId,Key))";
        cmd.ExecuteNonQuery();
    }

    SqliteConnection Open() => SqliteConnectionPolicy.Open(connectionString);
    public static string Key(string? text) => TaxonomyAliasStore.Key(text);

    public IReadOnlyList<TaxonomyEntry> Brands() => new TaxonomyStore(directory).List(TaxonomyKind.Brand);

    /// <summary>Local brands whose names fold to the same key: duplicate canonicals, reported so the operator merges them by hand — never picked between here.</summary>
    public IReadOnlyList<BrandDuplicate> Duplicates()
        => Brands().Where(b => b.Active).GroupBy(b => Key(b.Name), StringComparer.Ordinal).Where(g => g.Key.Length > 0 && g.Count() > 1).Select(g => new BrandDuplicate(g.Key, g.Select(b => b.Name).OrderBy(n => n, StringComparer.CurrentCulture).ToList())).ToList();

    /// <summary>Binds a supplier's spelling to one active local brand; refused when the spelling is a brand's own name or another brand's name, or already names another brand; the same alias for the same brand is updated in place.</summary>
    public BrandAliasView SaveAlias(string alias, string brandId, bool approved, string source = "manual")
    {
        var text = (alias ?? "").Trim();
        if (text.Length == 0 || text.Length > ValueLimit || text.Any(char.IsControl)) throw new InvalidOperationException("Marka takma adı 1-200 karakter olmalı ve kontrol karakteri içermemeli.");
        var key = Key(text); if (key.Length == 0) throw new InvalidOperationException("Marka takma adı harf veya rakam içermeli.");
        var brands = Brands(); var target = brands.FirstOrDefault(b => b.Id == brandId) ?? throw new InvalidOperationException("Takma adın bağlanacağı yerel marka bulunamadı.");
        if (!target.Active) throw new InvalidOperationException("Pasif markaya takma ad bağlanamaz.");
        if (Key(target.Name) == key) throw new InvalidOperationException("Takma ad markanın kendi adıyla aynı; gerek yok.");
        foreach (var other in brands) if (other.Id != brandId && other.Active && Key(other.Name) == key) throw new InvalidOperationException($"'{text}' başka bir markanın adı ({other.Name}); takma ad olamaz.");
        using var c = Open(); using var tx = c.BeginTransaction();
        using var find = c.CreateCommand(); find.Transaction = tx; find.CommandText = "SELECT BrandId FROM BrandAliases WHERE Key=$key"; find.Parameters.AddWithValue("$key", key);
        if (find.ExecuteScalar() is string existing && existing != brandId) throw new InvalidOperationException($"'{text}' takma adı zaten başka bir markaya bağlı ({brands.FirstOrDefault(b => b.Id == existing)?.Name ?? "silinmiş marka"}); önce oradan kaldırın.");
        var now = DateTime.UtcNow;
        using var upsert = c.CreateCommand(); upsert.Transaction = tx;
        upsert.CommandText = "INSERT INTO BrandAliases(Key,Alias,BrandId,Approved,Source,UpdatedUtc) VALUES($key,$alias,$brand,$approved,$source,$updated) ON CONFLICT(Key) DO UPDATE SET Alias=excluded.Alias,Approved=excluded.Approved,Source=excluded.Source,UpdatedUtc=excluded.UpdatedUtc";
        upsert.Parameters.AddWithValue("$key", key); upsert.Parameters.AddWithValue("$alias", text); upsert.Parameters.AddWithValue("$brand", brandId); upsert.Parameters.AddWithValue("$approved", approved ? 1 : 0); upsert.Parameters.AddWithValue("$source", (source ?? "manual").Trim()); upsert.Parameters.AddWithValue("$updated", now.ToString("O", CultureInfo.InvariantCulture));
        upsert.ExecuteNonQuery(); tx.Commit();
        return new(text, key, brandId, target.Name, approved, (source ?? "manual").Trim(), now, approved ? BrandAliasView.ApprovedStatus : BrandAliasView.Pending);
    }

    public bool RemoveAlias(string alias)
    {
        var key = Key(alias); if (key.Length == 0) return false;
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "DELETE FROM BrandAliases WHERE Key=$key"; cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteNonQuery() > 0;
    }

    public IReadOnlyList<BrandAliasView> ListAliases()
    {
        var brands = Brands().ToDictionary(b => b.Id, StringComparer.Ordinal);
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT Key,Alias,BrandId,Approved,Source,UpdatedUtc FROM BrandAliases ORDER BY Alias";
        using var r = cmd.ExecuteReader(); var rows = new List<BrandAliasView>();
        while (r.Read())
        {
            var brandId = r.GetString(2); var approved = r.GetInt32(3) != 0; var brand = brands.GetValueOrDefault(brandId);
            rows.Add(new(r.GetString(1), r.GetString(0), brandId, brand?.Name ?? "", approved, r.GetString(4), ParseUtc(r.GetString(5)), brand is null || !brand.Active ? BrandAliasView.Orphan : approved ? BrandAliasView.ApprovedStatus : BrandAliasView.Pending));
        }
        return rows;
    }

    /// <summary>The canonical brand name an approved, live alias gives a string — exact key only; null otherwise.</summary>
    public string? Resolve(string? text)
    {
        var key = Key(text); if (key.Length == 0) return null;
        return ListAliases().FirstOrDefault(a => a.Key == key && a.Status == BrandAliasView.ApprovedStatus)?.BrandName;
    }

    /// <summary>What the import applies: every active brand's own name (its canonical casing) and every approved alias, keyed → the brand's name.</summary>
    public IReadOnlyDictionary<string, string> KnownMap()
    {
        var known = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var brand in Brands().Where(b => b.Active)) known.TryAdd(Key(brand.Name), brand.Name.Trim());
        foreach (var alias in ListAliases().Where(a => a.Status == BrandAliasView.ApprovedStatus)) known.TryAdd(alias.Key, alias.BrandName);
        return known;
    }

    public static string? Match(IReadOnlyDictionary<string, string> known, string? text)
    {
        ArgumentNullException.ThrowIfNull(known);
        var key = Key(text);
        return key.Length > 0 && known.TryGetValue(key, out var name) ? name : null;
    }

    /// <summary>The best suggestion for a string that no name or alias places — the same name in another casing (100), a spelling one or two letters away (90), one name containing the other (60) — or null; a brand the operator rejected for this string is never suggested again.</summary>
    public BrandSuggestion? Suggest(string? text, string? rejectedBrandId = null)
    {
        var key = Key(text); if (key.Length == 0) return null;
        BrandSuggestion? best = null;
        foreach (var brand in Brands().Where(b => b.Active && b.Id != (rejectedBrandId ?? "")))
        {
            var brandKey = Key(brand.Name); if (brandKey.Length == 0) continue;
            BrandSuggestion? candidate = null;
            if (brandKey == key) candidate = new(brand.Id, brand.Name, BrandSuggestion.Exact, "aynı ad, farklı yazım");
            else if (key.Length >= 4 && brandKey.Length >= 4 && Distance(key, brandKey) is var distance && distance <= 2) candidate = new(brand.Id, brand.Name, BrandSuggestion.Typo, $"yazım farkı ({distance.ToString(CultureInfo.CurrentCulture)} harf)");
            else if (key.Length >= 4 && brandKey.Length >= 4 && (key.Contains(brandKey, StringComparison.Ordinal) || brandKey.Contains(key, StringComparison.Ordinal))) candidate = new(brand.Id, brand.Name, BrandSuggestion.Partial, "kısmi eşleşme");
            if (candidate is not null && (best is null || candidate.Confidence > best.Confidence)) best = candidate;
        }
        return best;
    }

    /// <summary>Records a run's unplaced brand strings for a source: one row per string, a repeat growing the count; the suggestion is refreshed on every sighting (a rejected brand stays excluded); a rejected row seen again is pending again.</summary>
    public int Record(string sourceId, IEnumerable<BrandSighting> sightings, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(sightings);
        if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("Kaynak kimliği gerekli.", nameof(sourceId));
        var grouped = sightings.Where(s => !string.IsNullOrWhiteSpace(s.Value)).GroupBy(s => Key(s.Value)).Where(g => g.Key.Length > 0).ToList();
        if (grouped.Count == 0) return 0;
        var existing = List(null, sourceId).ToDictionary(i => i.Key, StringComparer.Ordinal);
        var now = nowUtc.ToString("O", CultureInfo.InvariantCulture);
        using var c = Open(); using var tx = c.BeginTransaction();
        foreach (var group in grouped)
        {
            var first = group.First(); var rejected = existing.GetValueOrDefault(group.Key)?.RejectedBrandId ?? "";
            var suggestion = Suggest(first.Value, rejected.Length > 0 ? rejected : null);
            using var cmd = c.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO BrandReviewQueue(SourceId,Key,Value,Count,FirstSeenUtc,LastSeenUtc,SampleSku,Status,SuggestedBrandId,Confidence,Reason) VALUES($source,$key,$value,$count,$now,$now,$sample,$pending,$suggested,$confidence,$reason) ON CONFLICT(SourceId,Key) DO UPDATE SET Count=Count+excluded.Count,LastSeenUtc=excluded.LastSeenUtc,Value=excluded.Value,SampleSku=CASE WHEN SampleSku='' THEN excluded.SampleSku ELSE SampleSku END,Status=CASE WHEN Status=$rejected THEN $pending ELSE Status END,SuggestedBrandId=CASE WHEN Status=$approved THEN SuggestedBrandId ELSE excluded.SuggestedBrandId END,Confidence=CASE WHEN Status=$approved THEN Confidence ELSE excluded.Confidence END,Reason=CASE WHEN Status=$approved THEN Reason ELSE excluded.Reason END";
            cmd.Parameters.AddWithValue("$source", sourceId); cmd.Parameters.AddWithValue("$key", group.Key); cmd.Parameters.AddWithValue("$value", Limit(first.Value.Trim())); cmd.Parameters.AddWithValue("$count", group.Count()); cmd.Parameters.AddWithValue("$now", now); cmd.Parameters.AddWithValue("$sample", Limit((first.Sku ?? "").Trim(), 128)); cmd.Parameters.AddWithValue("$pending", BrandReviewItem.Pending); cmd.Parameters.AddWithValue("$rejected", BrandReviewItem.Rejected); cmd.Parameters.AddWithValue("$approved", BrandReviewItem.Approved);
            cmd.Parameters.AddWithValue("$suggested", suggestion?.BrandId ?? ""); cmd.Parameters.AddWithValue("$confidence", suggestion?.Confidence ?? 0); cmd.Parameters.AddWithValue("$reason", suggestion?.Reason ?? "");
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return grouped.Count;
    }

    public IReadOnlyList<BrandReviewItem> List(string? status = null, string? sourceId = null)
    {
        var brands = Brands().ToDictionary(b => b.Id, b => b.Name, StringComparer.Ordinal);
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT SourceId,Key,Value,Count,FirstSeenUtc,LastSeenUtc,SampleSku,Status,SuggestedBrandId,Confidence,Reason,RejectedBrandId FROM BrandReviewQueue WHERE ($status IS NULL OR Status=$status) AND ($source IS NULL OR SourceId=$source) ORDER BY Confidence DESC, Count DESC, LastSeenUtc DESC";
        cmd.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value); cmd.Parameters.AddWithValue("$source", (object?)sourceId ?? DBNull.Value);
        using var r = cmd.ExecuteReader(); var rows = new List<BrandReviewItem>();
        while (r.Read()) { var suggested = r.GetString(8); rows.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), ParseUtc(r.GetString(4)), ParseUtc(r.GetString(5)), r.GetString(6), r.GetString(7), suggested, suggested.Length > 0 ? brands.GetValueOrDefault(suggested, "silinmiş marka") : "", r.GetInt32(9), r.GetString(10), r.GetString(11))); }
        return rows;
    }

    public BrandReviewItem? Find(string sourceId, string key) => List(null, sourceId).FirstOrDefault(i => i.Key == key);

    /// <summary>Approves a queued string as an approved alias of one local brand — through the alias rules — and marks the row; the operator's choice, not the suggestion's.</summary>
    public BrandReviewItem Approve(string sourceId, string key, string brandId)
    {
        var item = Find(sourceId, key) ?? throw new InvalidOperationException("Kuyrukta böyle bir değer yok.");
        SaveAlias(item.Value, brandId, approved: true, source: "review:" + sourceId);
        Mark(sourceId, key, BrandReviewItem.Approved, rejectedBrandId: null);
        return Find(sourceId, key)!;
    }

    /// <summary>Rejects a queued string: the suggested brand is remembered as a false positive for it and is not suggested again.</summary>
    public BrandReviewItem Reject(string sourceId, string key)
    {
        var item = Find(sourceId, key) ?? throw new InvalidOperationException("Kuyrukta böyle bir değer yok.");
        Mark(sourceId, key, BrandReviewItem.Rejected, rejectedBrandId: item.SuggestedBrandId.Length > 0 ? item.SuggestedBrandId : item.RejectedBrandId);
        return Find(sourceId, key)!;
    }

    void Mark(string sourceId, string key, string status, string? rejectedBrandId)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = rejectedBrandId is null ? "UPDATE BrandReviewQueue SET Status=$status WHERE SourceId=$source AND Key=$key" : "UPDATE BrandReviewQueue SET Status=$status,RejectedBrandId=$rejected,SuggestedBrandId='',Confidence=0,Reason='' WHERE SourceId=$source AND Key=$key";
        cmd.Parameters.AddWithValue("$status", status); cmd.Parameters.AddWithValue("$source", sourceId); cmd.Parameters.AddWithValue("$key", key); if (rejectedBrandId is not null) cmd.Parameters.AddWithValue("$rejected", rejectedBrandId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Levenshtein distance between two folded keys.</summary>
    public static int Distance(string left, string right)
    {
        var previous = new int[right.Length + 1]; var current = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++) previous[j] = j;
        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++) current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[right.Length];
    }

    static string Limit(string value, int max = ValueLimit) => value.Length > max ? value[..max] : value;
    static DateTime ParseUtc(string value) => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) ? date : DateTime.MinValue;
}
