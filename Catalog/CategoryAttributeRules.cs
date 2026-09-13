using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;

namespace TrMarketplaceHubDesktop.Catalog;

/// <summary>One rule: a category requires (or merely allows) an attribute; the attribute's allowed values, when any are defined, are the taxonomy's Attribute entries of that name.</summary>
public sealed record CategoryAttributeRule(string CategoryId, string CategoryName, string Attribute, string AttributeKey, bool Required, DateTime UpdatedUtc);

/// <summary>A product's coverage of its category's attribute rules: the status, what is missing, what is invalid, what is present.</summary>
public sealed record AttributeCoverage(string Status, IReadOnlyList<string> Missing, IReadOnlyList<(string Attribute, string Value, string Allowed)> Invalid, IReadOnlyList<string> Present, IReadOnlyList<string> OptionalMissing, string Words)
{
    public const string Complete = "COMPLETE", Incomplete = "INCOMPLETE", NoRules = "NO_RULES", NoCategory = "NO_CATEGORY", UnknownCategory = "UNKNOWN_CATEGORY";
    public bool Blocks => Status == Incomplete;
}

/// <summary>
/// Attribute completeness per category (#915). The taxonomy owner keeps, per local category, which attributes are
/// required and which are optional; the taxonomy's Attribute entries (name → values) are the allowed values of an
/// attribute when any are defined, so a value outside them is invalid — a free-text attribute has no such entries
/// and any value stands. A product carries its attributes as "ad=değer; ad=değer" text; the coverage of the rules
/// of the product's category (resolved by name or approved alias, #912) is one verdict: a required attribute
/// missing or a value outside the allowed values makes the product incomplete for listing; an optional attribute
/// missing is said, not counted against it; a category without rules has nothing to cover. No variant engine:
/// attributes are per product, not per option.
/// </summary>
public sealed class CategoryAttributeRuleStore
{
    public const int AttributeLimit = 100;
    readonly string directory; readonly string connectionString;

    public CategoryAttributeRuleStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop"); Directory.CreateDirectory(directory);
        this.directory = directory;
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "catalog.db") }.ToString();
        _ = new TaxonomyStore(directory); // the entries table is the taxonomy owner's schema
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS CategoryAttributeRules(CategoryId TEXT NOT NULL, AttributeKey TEXT NOT NULL, Attribute TEXT NOT NULL, Required INTEGER NOT NULL, UpdatedUtc TEXT NOT NULL, PRIMARY KEY(CategoryId,AttributeKey))";
        cmd.ExecuteNonQuery();
    }

    SqliteConnection Open() => SqliteConnectionPolicy.Open(connectionString);

    public static string Key(string? attribute) => TaxonomyAliasStore.Key(attribute);

    /// <summary>Saves a rule for an active local category; the same attribute again updates the requirement in place.</summary>
    public CategoryAttributeRule Save(string categoryId, string attribute, bool required)
    {
        var name = (attribute ?? "").Trim();
        if (name.Length == 0 || name.Length > AttributeLimit || name.Any(char.IsControl) || name.Contains('=') || name.Contains(';')) throw new InvalidOperationException("Özellik adı 1-100 karakter olmalı; '=' ve ';' içeremez.");
        var key = Key(name); if (key.Length == 0) throw new InvalidOperationException("Özellik adı harf veya rakam içermeli.");
        var category = new TaxonomyStore(directory).List(TaxonomyKind.Category).FirstOrDefault(e => e.Id == categoryId) ?? throw new InvalidOperationException("Kural için yerel kategori bulunamadı.");
        if (!category.Active) throw new InvalidOperationException("Pasif kategoriye özellik kuralı bağlanamaz.");
        var now = DateTime.UtcNow;
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO CategoryAttributeRules(CategoryId,AttributeKey,Attribute,Required,UpdatedUtc) VALUES($category,$key,$name,$required,$updated) ON CONFLICT(CategoryId,AttributeKey) DO UPDATE SET Attribute=excluded.Attribute,Required=excluded.Required,UpdatedUtc=excluded.UpdatedUtc";
        cmd.Parameters.AddWithValue("$category", categoryId); cmd.Parameters.AddWithValue("$key", key); cmd.Parameters.AddWithValue("$name", name); cmd.Parameters.AddWithValue("$required", required ? 1 : 0); cmd.Parameters.AddWithValue("$updated", now.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
        return new(categoryId, category.Name, name, key, required, now);
    }

    public bool Remove(string categoryId, string attribute)
    {
        var key = Key(attribute); if (key.Length == 0) return false;
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "DELETE FROM CategoryAttributeRules WHERE CategoryId=$category AND AttributeKey=$key"; cmd.Parameters.AddWithValue("$category", categoryId ?? ""); cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteNonQuery() > 0;
    }

    public IReadOnlyList<CategoryAttributeRule> List(string? categoryId = null)
    {
        var names = new TaxonomyStore(directory).List(TaxonomyKind.Category).ToDictionary(e => e.Id, e => e.Name, StringComparer.Ordinal);
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT CategoryId,AttributeKey,Attribute,Required,UpdatedUtc FROM CategoryAttributeRules WHERE ($category IS NULL OR CategoryId=$category) ORDER BY CategoryId,Required DESC,Attribute";
        cmd.Parameters.AddWithValue("$category", (object?)categoryId ?? DBNull.Value);
        using var r = cmd.ExecuteReader(); var rows = new List<CategoryAttributeRule>();
        while (r.Read()) rows.Add(new(r.GetString(0), names.GetValueOrDefault(r.GetString(0), "silinmiş kategori"), r.GetString(2), r.GetString(1), r.GetInt32(3) != 0, DateTime.TryParse(r.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : DateTime.MinValue));
        return rows;
    }

    /// <summary>One read of everything the coverage needs: the categories by name and alias key, the rules by category, the allowed values by attribute key.</summary>
    public CategoryAttributeSnapshot Snapshot()
    {
        var taxonomy = new TaxonomyStore(directory);
        var categories = taxonomy.List(TaxonomyKind.Category); var byId = categories.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var byKey = new Dictionary<string, TaxonomyEntry>(StringComparer.Ordinal);
        foreach (var entry in categories.OrderByDescending(e => e.Active)) byKey.TryAdd(TaxonomyAliasStore.Key(entry.Name), entry);
        foreach (var alias in new TaxonomyAliasStore(directory).List().Where(a => a.Approved && byId.ContainsKey(a.LocalId))) byKey.TryAdd(alias.Key, byId[alias.LocalId]);
        var rules = List().GroupBy(r => r.CategoryId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => (IReadOnlyList<CategoryAttributeRule>)g.ToList(), StringComparer.Ordinal);
        var allowed = taxonomy.List(TaxonomyKind.Attribute).Where(e => e.Active && e.Value.Trim().Length > 0).GroupBy(e => Key(e.Name), StringComparer.Ordinal).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(e => e.Value.Trim()).Distinct(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        return new(byKey, rules, allowed, new AttributeValueMappingStore(directory).ApprovedMap()); // #916: the approved value aliases ride along
    }
}

public sealed class CategoryAttributeSnapshot
{
    readonly IReadOnlyDictionary<string, TaxonomyEntry> categoriesByKey;
    readonly IReadOnlyDictionary<string, IReadOnlyList<CategoryAttributeRule>> rulesByCategory;
    readonly IReadOnlyDictionary<string, IReadOnlyList<string>> allowedByAttribute;
    readonly IReadOnlyDictionary<string, string> valueAliases; // #916: attribute key + separator + alias key -> the canonical allowed value

    internal CategoryAttributeSnapshot(IReadOnlyDictionary<string, TaxonomyEntry> categoriesByKey, IReadOnlyDictionary<string, IReadOnlyList<CategoryAttributeRule>> rulesByCategory, IReadOnlyDictionary<string, IReadOnlyList<string>> allowedByAttribute, IReadOnlyDictionary<string, string>? valueAliases = null)
    { this.categoriesByKey = categoriesByKey; this.rulesByCategory = rulesByCategory; this.allowedByAttribute = allowedByAttribute; this.valueAliases = valueAliases ?? new Dictionary<string, string>(StringComparer.Ordinal); }

    /// <summary>"Renk=Kırmızı; Beden=M" → the pairs, keyed by the attribute's fold; a pair without '=' or with an empty name is ignored; the last value of a repeated name wins.</summary>
    public static IReadOnlyDictionary<string, (string Attribute, string Value)> Parse(string? attributesText)
    {
        var pairs = new Dictionary<string, (string Attribute, string Value)>(StringComparer.Ordinal);
        foreach (var piece in (attributesText ?? "").Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cut = piece.IndexOf('='); if (cut <= 0) continue;
            var name = piece[..cut].Trim(); var value = piece[(cut + 1)..].Trim(); var key = CategoryAttributeRuleStore.Key(name);
            if (key.Length == 0) continue;
            pairs[key] = (name, value);
        }
        return pairs;
    }

    public AttributeCoverage Evaluate(CatalogProduct product)
    {
        ArgumentNullException.ThrowIfNull(product);
        return Evaluate(product.Category, product.AttributesText);
    }

    public AttributeCoverage Evaluate(string? category, string? attributesText)
    {
        var text = (category ?? "").Trim();
        if (text.Length == 0) return new(AttributeCoverage.NoCategory, Array.Empty<string>(), Array.Empty<(string, string, string)>(), Array.Empty<string>(), Array.Empty<string>(), "kategori girilmemiş; özellik kuralı uygulanamaz");
        if (!categoriesByKey.TryGetValue(TaxonomyAliasStore.Key(text), out var entry)) return new(AttributeCoverage.UnknownCategory, Array.Empty<string>(), Array.Empty<(string, string, string)>(), Array.Empty<string>(), Array.Empty<string>(), "kategori sözlükte yok; özellik kuralı uygulanamaz");
        if (!rulesByCategory.TryGetValue(entry.Id, out var rules) || rules.Count == 0) return new(AttributeCoverage.NoRules, Array.Empty<string>(), Array.Empty<(string, string, string)>(), Array.Empty<string>(), Array.Empty<string>(), $"{entry.Name}: özellik kuralı tanımlı değil");
        var pairs = Parse(attributesText);
        var missing = new List<string>(); var optionalMissing = new List<string>(); var present = new List<string>(); var invalid = new List<(string, string, string)>();
        foreach (var rule in rules)
        {
            if (!pairs.TryGetValue(rule.AttributeKey, out var pair) || pair.Value.Length == 0) { if (rule.Required) missing.Add(rule.Attribute); else optionalMissing.Add(rule.Attribute); continue; }
            var value = valueAliases.TryGetValue(AttributeValueMappingStore.MapKey(rule.AttributeKey, TaxonomyAliasStore.Key(pair.Value)), out var canonical) ? canonical : pair.Value; // #916: an approved value alias is the only auto-apply
            if (allowedByAttribute.TryGetValue(rule.AttributeKey, out var allowed) && !allowed.Any(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase) || TaxonomyAliasStore.Key(v) == TaxonomyAliasStore.Key(value)))
            { invalid.Add((rule.Attribute, pair.Value, string.Join(", ", allowed.Take(6)) + (allowed.Count > 6 ? ", …" : ""))); continue; }
            present.Add(rule.Attribute);
        }
        var required = rules.Count(r => r.Required); var status = missing.Count == 0 && invalid.Count == 0 ? AttributeCoverage.Complete : AttributeCoverage.Incomplete;
        var words = status == AttributeCoverage.Complete
            ? $"{entry.Name}: {required.ToString(CultureInfo.CurrentCulture)} zorunlu özellik tamam" + (optionalMissing.Count > 0 ? $" · isteğe bağlı eksik: {string.Join(", ", optionalMissing)}" : "")
            : $"{entry.Name}: " + string.Join(" · ", new[] { missing.Count > 0 ? "eksik zorunlu: " + string.Join(", ", missing) : "", invalid.Count > 0 ? "geçersiz değer: " + string.Join(", ", invalid.Select(i => $"{i.Item1} = {i.Item2} (izinli: {i.Item3})")) : "" }.Where(w => w.Length > 0));
        return new(status, missing, invalid, present, optionalMissing, words);
    }
}
