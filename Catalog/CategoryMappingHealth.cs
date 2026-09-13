namespace TrMarketplaceHubDesktop.Catalog;

/// <summary>The verdict on a product's category against one channel/shop: the state, the words, and whether it blocks listing readiness.</summary>
public sealed record CategoryMappingVerdict(string Status, string Words, bool Blocks)
{
    public const string Ok = "OK", NoCategory = "NO_CATEGORY", UnknownCategory = "UNKNOWN_CATEGORY", EntryInactive = "ENTRY_INACTIVE", MappingMissing = "MAPPING_MISSING", MappingRenamed = "MAPPING_RENAMED";
}

/// <summary>A channel mapping whose local category no longer exists: the external key it still names, the id it points to, and when it was written.</summary>
public sealed record CategoryMappingOrphan(string ExternalKey, string LocalId, DateTime UpdatedUtc);

/// <summary>
/// The stale category mapping detector (#914). A channel maps an external category key to a local category by id
/// (#843's taxonomy mappings); a product carries its category as text. This snapshot, taken once per channel/shop,
/// joins the three: the product's text resolves to a local category by name or by an approved alias (#912),
/// the category must be active, the channel must map it, and the mapping must be younger than the category's
/// last change (a label renamed after the mapping was made is a mapping to confirm). A category deactivated
/// under a mapped product blocks listing readiness; a category the dictionary does not know, a missing mapping
/// or a changed label warns; a mapping whose category is gone is an orphan named by its external key. A rename
/// keeps the old name resolving: the taxonomy owner writes it as an approved alias when the label changes.
/// </summary>
public sealed class CategoryMappingSnapshot
{
    readonly IReadOnlyDictionary<string, TaxonomyEntry> byKey;
    readonly IReadOnlyDictionary<string, (string ExternalKey, DateTime UpdatedUtc)> mappingsByLocalId;
    public string Marketplace { get; }
    public string ShopId { get; }
    public IReadOnlyList<CategoryMappingOrphan> Orphans { get; }

    internal CategoryMappingSnapshot(string marketplace, string shopId, IReadOnlyDictionary<string, TaxonomyEntry> byKey, IReadOnlyDictionary<string, (string ExternalKey, DateTime UpdatedUtc)> mappingsByLocalId, IReadOnlyList<CategoryMappingOrphan> orphans)
    { Marketplace = marketplace; ShopId = shopId; this.byKey = byKey; this.mappingsByLocalId = mappingsByLocalId; Orphans = orphans; }

    public CategoryMappingVerdict Evaluate(string? productCategory)
    {
        var text = (productCategory ?? "").Trim();
        if (text.Length == 0) return new(CategoryMappingVerdict.NoCategory, "kategori girilmemiş", false);
        var key = TaxonomyAliasStore.Key(text);
        if (key.Length == 0 || !byKey.TryGetValue(key, out var entry)) return new(CategoryMappingVerdict.UnknownCategory, "kategori sözlükte yok: " + Safe(text), false);
        if (!entry.Active) return new(CategoryMappingVerdict.EntryInactive, $"kategori pasif: {entry.Name}; ilan gönderimi engellenir", true);
        if (!mappingsByLocalId.TryGetValue(entry.Id, out var mapping)) return new(CategoryMappingVerdict.MappingMissing, $"kanal eşlemesi yok: {entry.Name} → {Marketplace}/{ShopId}", false);
        if (mapping.UpdatedUtc < entry.UpdatedUtc) return new(CategoryMappingVerdict.MappingRenamed, $"kategori kaydı eşlemeden sonra değişti: {entry.Name} (harici anahtar {mapping.ExternalKey}); eşlemeyi doğrulayın", false);
        return new(CategoryMappingVerdict.Ok, $"{entry.Name} → {mapping.ExternalKey}", false);
    }

    static string Safe(string text) => AuditStore.Redact(text.Length > 60 ? text[..60] + "…" : text);
}

public static class CategoryMappingHealth
{
    /// <summary>One read of the taxonomy for a channel/shop: the active and inactive categories by name key and by approved alias key, the channel's mappings by local id with their times, and the orphans.</summary>
    public static CategoryMappingSnapshot Snapshot(string? directory, string marketplace, string shopId)
    {
        var market = (marketplace ?? "").Trim().ToLowerInvariant(); var shop = (shopId ?? "").Trim();
        var taxonomy = new TaxonomyStore(directory);
        var entries = taxonomy.List(TaxonomyKind.Category);
        var byId = entries.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var byKey = new Dictionary<string, TaxonomyEntry>(StringComparer.Ordinal);
        foreach (var entry in entries.OrderByDescending(e => e.Active)) byKey.TryAdd(TaxonomyAliasStore.Key(entry.Name), entry);
        foreach (var alias in new TaxonomyAliasStore(directory).List().Where(a => a.Approved && byId.ContainsKey(a.LocalId))) byKey.TryAdd(alias.Key, byId[alias.LocalId]);
        var times = taxonomy.MappingTimes(TaxonomyKind.Category, market, shop);
        var mappingsByLocalId = new Dictionary<string, (string ExternalKey, DateTime UpdatedUtc)>(StringComparer.Ordinal);
        var orphans = new List<CategoryMappingOrphan>();
        foreach (var mapping in taxonomy.Mappings(TaxonomyKind.Category).Where(m => m.Marketplace == market && m.ShopId == shop))
        {
            var updated = times.GetValueOrDefault(mapping.ExternalKey);
            if (!byId.ContainsKey(mapping.LocalId)) { orphans.Add(new(mapping.ExternalKey, mapping.LocalId, updated)); continue; }
            if (!mappingsByLocalId.TryGetValue(mapping.LocalId, out var existing) || existing.UpdatedUtc < updated) mappingsByLocalId[mapping.LocalId] = (mapping.ExternalKey, updated);
        }
        return new(market, shop, byKey, mappingsByLocalId, orphans);
    }
}
