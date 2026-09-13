using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;

namespace TrMarketplaceHubDesktop.Catalog;

/// <summary>One value alias as the dictionary shows it: the attribute, the supplier's spelling and its key, the canonical allowed value it names, approval, origin, and its state (APPROVED / PENDING / ORPHAN when the canonical value is no longer allowed).</summary>
public sealed record AttributeValueAliasView(string AttributeKey, string Attribute, string Alias, string AliasKey, string CanonicalValue, bool Approved, string Source, DateTime UpdatedUtc, string Status)
{
    public const string ApprovedStatus = "APPROVED", Pending = "PENDING", Orphan = "ORPHAN";
}

/// <summary>One unmapped value in the queue: the attribute, the value as seen and its key, how often and when, a sample SKU, its state.</summary>
public sealed record AttributeValueQueueItem(string AttributeKey, string Attribute, string ValueKey, string Value, int Count, DateTime FirstSeenUtc, DateTime LastSeenUtc, string SampleSku, string Status)
{
    public const string Pending = "PENDING", Approved = "APPROVED", Rejected = "REJECTED";
}

public sealed record AttributeValueSighting(string Attribute, string Value, string Sku);

/// <summary>
/// Enum attribute value mapping (#916). An attribute's allowed values are the taxonomy's Attribute entries of its
/// name (#915); a supplier writes "Kirmizi", "KIRMIZI" or "red" for the allowed value "Kırmızı". Each spelling is an
/// alias keyed by the display fold (#868), naming exactly one allowed value of that attribute: the same alias again
/// is the same alias, for another value a conflict refused by name; an alias that is itself an allowed value is
/// refused. Only an approved alias is applied — by the coverage (#915) on an exact key — a pending one is a
/// suggestion. A value that resolves to nothing lands in the unmapped queue with its evidence (count, first and
/// last seen, a sample SKU); approving a queued value writes the alias and marks the row; rejecting keeps it out
/// of the pending list until it is seen again. An alias whose canonical value was removed from the allowed values
/// (the entry deleted or deactivated) is stale: it is listed as an orphan and applies no more. No XML variant
/// mapping: values are per product attribute, not per option.
/// </summary>
public sealed class AttributeValueMappingStore
{
    public const int ValueLimit = 200;
    public const char MapSeparator = '\u001f'; // the unit separator: never part of a folded key
    readonly string directory; readonly string connectionString;

    public AttributeValueMappingStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop"); Directory.CreateDirectory(directory);
        this.directory = directory;
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "catalog.db") }.ToString();
        _ = new TaxonomyStore(directory); // the entries table is the taxonomy owner's schema
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS AttributeValueAliases(AttributeKey TEXT NOT NULL, AliasKey TEXT NOT NULL, Attribute TEXT NOT NULL, Alias TEXT NOT NULL, CanonicalValue TEXT NOT NULL, Approved INTEGER NOT NULL, Source TEXT NOT NULL DEFAULT 'manual', UpdatedUtc TEXT NOT NULL, PRIMARY KEY(AttributeKey,AliasKey));CREATE TABLE IF NOT EXISTS AttributeValueQueue(AttributeKey TEXT NOT NULL, ValueKey TEXT NOT NULL, Attribute TEXT NOT NULL, Value TEXT NOT NULL, Count INTEGER NOT NULL, FirstSeenUtc TEXT NOT NULL, LastSeenUtc TEXT NOT NULL, SampleSku TEXT NOT NULL DEFAULT '', Status TEXT NOT NULL, PRIMARY KEY(AttributeKey,ValueKey))";
        cmd.ExecuteNonQuery();
    }

    SqliteConnection Open() => SqliteConnectionPolicy.Open(connectionString);
    public static string Key(string? text) => TaxonomyAliasStore.Key(text);
    public static string MapKey(string attributeKey, string valueKey) => attributeKey + MapSeparator + valueKey;

    /// <summary>The allowed values of an attribute: the active Attribute entries of that name with a value.</summary>
    public IReadOnlyList<string> AllowedValues(string attribute)
    {
        var key = Key(attribute);
        return new TaxonomyStore(directory).List(TaxonomyKind.Attribute).Where(e => e.Active && e.Value.Trim().Length > 0 && Key(e.Name) == key).Select(e => e.Value.Trim()).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Binds a supplier's spelling to one allowed value of the attribute; refused when the value is not allowed, when the alias is itself an allowed value, or when the same alias already names another value; the same alias for the same value is updated in place.</summary>
    public AttributeValueAliasView SaveAlias(string attribute, string alias, string canonicalValue, bool approved, string source = "manual")
    {
        var name = (attribute ?? "").Trim(); var attributeKey = Key(name); if (attributeKey.Length == 0) throw new InvalidOperationException("Özellik adı gerekli.");
        var text = (alias ?? "").Trim(); if (text.Length == 0 || text.Length > ValueLimit || text.Any(char.IsControl)) throw new InvalidOperationException("Değer takma adı 1-200 karakter olmalı ve kontrol karakteri içermemeli.");
        var aliasKey = Key(text); if (aliasKey.Length == 0) throw new InvalidOperationException("Değer takma adı harf veya rakam içermeli.");
        var allowed = AllowedValues(name);
        var canonical = allowed.FirstOrDefault(v => Key(v) == Key(canonicalValue)) ?? throw new InvalidOperationException($"'{(canonicalValue ?? "").Trim()}' bu özelliğin izinli değeri değil; önce sözlüğe ekleyin.");
        if (allowed.Any(v => Key(v) == aliasKey)) throw new InvalidOperationException($"'{text}' zaten izinli bir değer; takma ad olamaz.");
        using var c = Open(); using var tx = c.BeginTransaction();
        using var find = c.CreateCommand(); find.Transaction = tx; find.CommandText = "SELECT CanonicalValue FROM AttributeValueAliases WHERE AttributeKey=$attribute AND AliasKey=$alias"; find.Parameters.AddWithValue("$attribute", attributeKey); find.Parameters.AddWithValue("$alias", aliasKey);
        if (find.ExecuteScalar() is string existing && Key(existing) != Key(canonical)) throw new InvalidOperationException($"'{text}' takma adı zaten '{existing}' değerine bağlı; önce oradan kaldırın.");
        var now = DateTime.UtcNow;
        using var upsert = c.CreateCommand(); upsert.Transaction = tx;
        upsert.CommandText = "INSERT INTO AttributeValueAliases(AttributeKey,AliasKey,Attribute,Alias,CanonicalValue,Approved,Source,UpdatedUtc) VALUES($attribute,$alias,$name,$text,$canonical,$approved,$source,$updated) ON CONFLICT(AttributeKey,AliasKey) DO UPDATE SET Attribute=excluded.Attribute,Alias=excluded.Alias,CanonicalValue=excluded.CanonicalValue,Approved=excluded.Approved,Source=excluded.Source,UpdatedUtc=excluded.UpdatedUtc";
        upsert.Parameters.AddWithValue("$attribute", attributeKey); upsert.Parameters.AddWithValue("$alias", aliasKey); upsert.Parameters.AddWithValue("$name", name); upsert.Parameters.AddWithValue("$text", text); upsert.Parameters.AddWithValue("$canonical", canonical); upsert.Parameters.AddWithValue("$approved", approved ? 1 : 0); upsert.Parameters.AddWithValue("$source", (source ?? "manual").Trim()); upsert.Parameters.AddWithValue("$updated", now.ToString("O", CultureInfo.InvariantCulture));
        upsert.ExecuteNonQuery(); tx.Commit();
        return new(attributeKey, name, text, aliasKey, canonical, approved, (source ?? "manual").Trim(), now, approved ? AttributeValueAliasView.ApprovedStatus : AttributeValueAliasView.Pending);
    }

    public bool RemoveAlias(string attribute, string alias)
    {
        var attributeKey = Key(attribute); var aliasKey = Key(alias); if (attributeKey.Length == 0 || aliasKey.Length == 0) return false;
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "DELETE FROM AttributeValueAliases WHERE AttributeKey=$attribute AND AliasKey=$alias"; cmd.Parameters.AddWithValue("$attribute", attributeKey); cmd.Parameters.AddWithValue("$alias", aliasKey);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Every alias with its state: an alias whose canonical value is no longer allowed is an orphan (stale) and applies no more.</summary>
    public IReadOnlyList<AttributeValueAliasView> ListAliases(string? attribute = null)
    {
        var allowed = AllowedByAttribute();
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT AttributeKey,AliasKey,Attribute,Alias,CanonicalValue,Approved,Source,UpdatedUtc FROM AttributeValueAliases WHERE ($attribute IS NULL OR AttributeKey=$attribute) ORDER BY Attribute,Alias";
        cmd.Parameters.AddWithValue("$attribute", attribute is null ? DBNull.Value : Key(attribute));
        using var r = cmd.ExecuteReader(); var rows = new List<AttributeValueAliasView>();
        while (r.Read())
        {
            var attributeKey = r.GetString(0); var canonical = r.GetString(4); var approved = r.GetInt32(5) != 0;
            var live = allowed.TryGetValue(attributeKey, out var values) && values.Any(v => Key(v) == Key(canonical));
            rows.Add(new(attributeKey, r.GetString(2), r.GetString(3), r.GetString(1), canonical, approved, r.GetString(6), ParseUtc(r.GetString(7)), !live ? AttributeValueAliasView.Orphan : approved ? AttributeValueAliasView.ApprovedStatus : AttributeValueAliasView.Pending));
        }
        return rows;
    }

    /// <summary>The stale aliases: approved or pending, their canonical value removed from the allowed values.</summary>
    public IReadOnlyList<AttributeValueAliasView> Stale() => ListAliases().Where(a => a.Status == AttributeValueAliasView.Orphan).ToList();

    /// <summary>The canonical value an approved, live alias names for a supplier's spelling — exact key match only; null otherwise.</summary>
    public string? Resolve(string attribute, string? value)
    {
        var attributeKey = Key(attribute); var valueKey = Key(value); if (attributeKey.Length == 0 || valueKey.Length == 0) return null;
        return ListAliases(attribute).FirstOrDefault(a => a.AliasKey == valueKey && a.Status == AttributeValueAliasView.ApprovedStatus)?.CanonicalValue;
    }

    /// <summary>Every approved, live alias keyed for the coverage: attribute key + separator + alias key → canonical value.</summary>
    public IReadOnlyDictionary<string, string> ApprovedMap()
        => ListAliases().Where(a => a.Status == AttributeValueAliasView.ApprovedStatus).ToDictionary(a => MapKey(a.AttributeKey, a.AliasKey), a => a.CanonicalValue, StringComparer.Ordinal);

    /// <summary>Records sightings of values that resolved to nothing: one row per attribute and value, a repeat growing its count; a rejected value seen again is pending again.</summary>
    public int Record(IEnumerable<AttributeValueSighting> sightings, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(sightings);
        var grouped = sightings.Where(s => !string.IsNullOrWhiteSpace(s.Attribute) && !string.IsNullOrWhiteSpace(s.Value)).GroupBy(s => (AttributeKey: Key(s.Attribute), ValueKey: Key(s.Value))).Where(g => g.Key.AttributeKey.Length > 0 && g.Key.ValueKey.Length > 0).ToList();
        if (grouped.Count == 0) return 0;
        var now = nowUtc.ToString("O", CultureInfo.InvariantCulture);
        using var c = Open(); using var tx = c.BeginTransaction();
        foreach (var group in grouped)
        {
            var first = group.First();
            using var cmd = c.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO AttributeValueQueue(AttributeKey,ValueKey,Attribute,Value,Count,FirstSeenUtc,LastSeenUtc,SampleSku,Status) VALUES($attribute,$key,$name,$value,$count,$now,$now,$sample,$pending) ON CONFLICT(AttributeKey,ValueKey) DO UPDATE SET Count=Count+excluded.Count,LastSeenUtc=excluded.LastSeenUtc,SampleSku=CASE WHEN SampleSku='' THEN excluded.SampleSku ELSE SampleSku END,Status=CASE WHEN Status=$rejected THEN $pending ELSE Status END";
            cmd.Parameters.AddWithValue("$attribute", group.Key.AttributeKey); cmd.Parameters.AddWithValue("$key", group.Key.ValueKey); cmd.Parameters.AddWithValue("$name", first.Attribute.Trim()); cmd.Parameters.AddWithValue("$value", Limit(first.Value.Trim())); cmd.Parameters.AddWithValue("$count", group.Count()); cmd.Parameters.AddWithValue("$now", now); cmd.Parameters.AddWithValue("$sample", Limit((first.Sku ?? "").Trim(), 128)); cmd.Parameters.AddWithValue("$pending", AttributeValueQueueItem.Pending); cmd.Parameters.AddWithValue("$rejected", AttributeValueQueueItem.Rejected);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return grouped.Count;
    }

    public IReadOnlyList<AttributeValueQueueItem> ListQueue(string? status = null)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT AttributeKey,ValueKey,Attribute,Value,Count,FirstSeenUtc,LastSeenUtc,SampleSku,Status FROM AttributeValueQueue WHERE ($status IS NULL OR Status=$status) ORDER BY Count DESC, LastSeenUtc DESC";
        cmd.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value);
        using var r = cmd.ExecuteReader(); var rows = new List<AttributeValueQueueItem>();
        while (r.Read()) rows.Add(new(r.GetString(0), r.GetString(2), r.GetString(1), r.GetString(3), r.GetInt32(4), ParseUtc(r.GetString(5)), ParseUtc(r.GetString(6)), r.GetString(7), r.GetString(8)));
        return rows;
    }

    public AttributeValueQueueItem? FindQueued(string attribute, string value) => ListQueue().FirstOrDefault(i => i.AttributeKey == Key(attribute) && i.ValueKey == Key(value));

    /// <summary>Approves a queued value as an approved alias of one allowed value — through the alias rules — and marks the row.</summary>
    public AttributeValueQueueItem ApproveQueued(string attribute, string value, string canonicalValue)
    {
        var item = FindQueued(attribute, value) ?? throw new InvalidOperationException("Kuyrukta böyle bir değer yok.");
        SaveAlias(item.Attribute, item.Value, canonicalValue, approved: true, source: "review");
        Mark(item.AttributeKey, item.ValueKey, AttributeValueQueueItem.Approved);
        return FindQueued(attribute, value)!;
    }

    public AttributeValueQueueItem RejectQueued(string attribute, string value)
    {
        var item = FindQueued(attribute, value) ?? throw new InvalidOperationException("Kuyrukta böyle bir değer yok.");
        Mark(item.AttributeKey, item.ValueKey, AttributeValueQueueItem.Rejected);
        return FindQueued(attribute, value)!;
    }

    void Mark(string attributeKey, string valueKey, string status)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "UPDATE AttributeValueQueue SET Status=$status WHERE AttributeKey=$attribute AND ValueKey=$key"; cmd.Parameters.AddWithValue("$status", status); cmd.Parameters.AddWithValue("$attribute", attributeKey); cmd.Parameters.AddWithValue("$key", valueKey); cmd.ExecuteNonQuery();
    }

    Dictionary<string, List<string>> AllowedByAttribute()
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var entry in new TaxonomyStore(directory).List(TaxonomyKind.Attribute).Where(e => e.Active && e.Value.Trim().Length > 0))
        { var key = Key(entry.Name); if (!result.TryGetValue(key, out var list)) result[key] = list = new List<string>(); list.Add(entry.Value.Trim()); }
        return result;
    }

    static string Limit(string value, int max = ValueLimit) => value.Length > max ? value[..max] : value;
    static DateTime ParseUtc(string value) => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) ? date : DateTime.MinValue;
}
