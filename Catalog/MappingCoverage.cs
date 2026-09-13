using System.Globalization;

namespace TrMarketplaceHubDesktop.Catalog;

/// <summary>One record behind a coverage figure: the scope it was counted in (and the channel when the scope is a shop), the product, the value examined, the bucket it fell into and the words.</summary>
public sealed record MappingCoverageRecord(string Scope, string ScopeId, string Channel, TaxonomyKind Kind, string ProductId, string Sku, string ProductName, string Value, string Bucket, string Words);

/// <summary>One row of the report: a scope — a feed source, a channel shop, or a channel as the sum of its shops — and a kind, with the counts and the state they make.</summary>
public sealed record MappingCoverageRow(string Scope, string ScopeId, string ScopeName, TaxonomyKind Kind, int Mapped, int Unmapped, int Stale, int Total, string State)
{
    public const string SourceScope = "source", ShopScope = "shop", ChannelScope = "channel";
    public const string Empty = "EMPTY", Partial = "PARTIAL", StaleState = "STALE", Full = "FULL";
    public int Percent => Total == 0 ? 0 : (int)Math.Round(100.0 * Mapped / Total, MidpointRounding.AwayFromZero);
    /// <summary>No records is empty; anything unmapped is partial; nothing unmapped but something stale is stale; everything mapped is full.</summary>
    public static string StateOf(int mapped, int unmapped, int stale) => mapped + unmapped + stale == 0 ? Empty : unmapped > 0 ? Partial : stale > 0 ? StaleState : Full;
}

/// <summary>The report: its rows and the real records behind each of them.</summary>
public sealed class MappingCoverageReport
{
    readonly IReadOnlyList<MappingCoverageRecord> records;
    public DateTime GeneratedUtc { get; }
    public IReadOnlyList<MappingCoverageRow> Rows { get; }

    internal MappingCoverageReport(DateTime generatedUtc, IReadOnlyList<MappingCoverageRow> rows, IReadOnlyList<MappingCoverageRecord> records) { GeneratedUtc = generatedUtc; Rows = rows; this.records = records; }

    public MappingCoverageRow? Find(string scope, string scopeId, TaxonomyKind kind) => Rows.FirstOrDefault(r => r.Scope == scope && r.ScopeId == scopeId && r.Kind == kind);

    /// <summary>The records behind a row in work order — unmapped first, then stale, then mapped, by shop and SKU — or one bucket of them; a channel row drills into every shop of the channel.</summary>
    public IReadOnlyList<MappingCoverageRecord> Drill(MappingCoverageRow row, string? bucket = null)
    {
        ArgumentNullException.ThrowIfNull(row);
        var hits = row.Scope == MappingCoverageRow.ChannelScope
            ? records.Where(r => r.Scope == MappingCoverageRow.ShopScope && r.Channel == row.ScopeId)
            : records.Where(r => r.Scope == row.Scope && r.ScopeId == row.ScopeId);
        return hits.Where(r => r.Kind == row.Kind && (bucket is null || r.Bucket == bucket)).OrderBy(r => Order(r.Bucket)).ThenBy(r => r.ScopeId, StringComparer.Ordinal).ThenBy(r => r.Sku, StringComparer.Ordinal).ToList();
    }

    static int Order(string bucket) => bucket == MappingCoverage.Unmapped ? 0 : bucket == MappingCoverage.Stale ? 1 : 2;
}

/// <summary>
/// The mapping coverage report (#921). Every product is counted three times per scope — its category, its brand,
/// its attributes — into one of three buckets. In a feed source's scope the question is the dictionary's: the text
/// resolves to an active local entry by name or approved alias (#912, #917) → mapped; to an inactive one → stale;
/// to nothing, or there is no text → unmapped; for attributes the category's rule coverage (#915, #916) complete or
/// without rules → mapped, else unmapped. In a channel shop's scope the question is the channel's, asked only of
/// what the dictionary already resolved: the local entry must be mapped on that channel and shop (#843) → mapped;
/// mapped before the entry last changed, or not confirmed for 180 days (the taxonomy owner's own rule) → stale; not
/// mapped, or unresolved on the dictionary side → unmapped; for attributes every known attribute value the product
/// carries must be mapped. A channel is the sum of its shops. A row's state: empty without records, partial with
/// anything unmapped, stale with nothing unmapped but something stale, full otherwise. Every figure drills down to
/// the real products behind it, unmapped first. Words carry dictionary names and external keys, never a product's
/// free text beyond a redacted, cut sample of the value that failed to resolve.
/// </summary>
public static class MappingCoverage
{
    public const string Mapped = "MAPPED", Unmapped = "UNMAPPED", Stale = "STALE";
    public static readonly TimeSpan StaleAge = TimeSpan.FromDays(180);
    static readonly TaxonomyKind[] Kinds = { TaxonomyKind.Category, TaxonomyKind.Brand, TaxonomyKind.Attribute };

    sealed record Verdict(string Bucket, string Words, TaxonomyEntry? Entry);
    sealed record ProductFacts(Verdict Category, Verdict Brand, AttributeCoverage Coverage, IReadOnlyList<TaxonomyEntry> AttributeEntries);

    public static MappingCoverageReport Build(string? directory, IReadOnlyList<XmlSource> sources, IReadOnlyList<CatalogProduct> products, IEnumerable<MarketplaceConnection> connections, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(sources); ArgumentNullException.ThrowIfNull(products); ArgumentNullException.ThrowIfNull(connections);
        var taxonomy = new TaxonomyStore(directory);
        var categories = KeyMap(taxonomy.List(TaxonomyKind.Category), new TaxonomyAliasStore(directory).List().Where(a => a.Approved).Select(a => (a.Key, a.LocalId)));
        var brands = KeyMap(taxonomy.List(TaxonomyKind.Brand), new BrandMappingStore(directory).ListAliases().Where(a => a.Status == BrandAliasView.ApprovedStatus).Select(a => (a.Key, a.BrandId)));
        var attributes = new Dictionary<string, TaxonomyEntry>(StringComparer.Ordinal);
        foreach (var entry in taxonomy.List(TaxonomyKind.Attribute).OrderByDescending(e => e.Active)) attributes.TryAdd(AttributeValueMappingStore.MapKey(CategoryAttributeRuleStore.Key(entry.Name), AttributeValueMappingStore.Key(entry.Value)), entry);
        var valueAliases = new AttributeValueMappingStore(directory).ApprovedMap();
        var rules = new CategoryAttributeRuleStore(directory).Snapshot();
        var facts = products.ToDictionary(p => p.Id, p => Facts(p, categories, brands, attributes, valueAliases, rules), StringComparer.Ordinal);

        var records = new List<MappingCoverageRecord>();
        foreach (var p in products)
        {
            var f = facts[p.Id]; var sourceId = p.SourceId ?? "";
            records.Add(new(MappingCoverageRow.SourceScope, sourceId, "", TaxonomyKind.Category, p.Id, p.Sku, p.Name, Safe(p.Category), f.Category.Bucket, f.Category.Words));
            records.Add(new(MappingCoverageRow.SourceScope, sourceId, "", TaxonomyKind.Brand, p.Id, p.Sku, p.Name, Safe(p.Brand), f.Brand.Bucket, f.Brand.Words));
            records.Add(new(MappingCoverageRow.SourceScope, sourceId, "", TaxonomyKind.Attribute, p.Id, p.Sku, p.Name, Safe(p.AttributesText), Covered(f.Coverage) ? Mapped : Unmapped, f.Coverage.Words));
        }
        var shops = connections.Select(c => (Market: (c.Channel ?? "").Trim().ToLowerInvariant(), Shop: (c.ShopId ?? "").Trim(), Name: (c.DisplayName ?? "").Trim()))
            .Where(s => s.Market.Length > 0 && s.Shop.Length > 0).GroupBy(s => (s.Market, s.Shop)).Select(g => g.First())
            .OrderBy(s => s.Market, StringComparer.Ordinal).ThenBy(s => s.Shop, StringComparer.Ordinal).ToList();
        foreach (var s in shops)
        {
            var scopeId = s.Market + "/" + s.Shop;
            var categoryMaps = ShopMappings(taxonomy, TaxonomyKind.Category, s.Market, s.Shop); var brandMaps = ShopMappings(taxonomy, TaxonomyKind.Brand, s.Market, s.Shop); var attributeMaps = ShopMappings(taxonomy, TaxonomyKind.Attribute, s.Market, s.Shop);
            foreach (var p in products)
            {
                var f = facts[p.Id];
                var category = f.Category.Bucket == Mapped && f.Category.Entry is not null ? OnChannel(f.Category.Entry, f.Category.Entry.Name, categoryMaps, nowUtc) : f.Category;
                var brand = f.Brand.Bucket == Mapped && f.Brand.Entry is not null ? OnChannel(f.Brand.Entry, f.Brand.Entry.Name, brandMaps, nowUtc) : f.Brand;
                var attribute = AttributesOnChannel(f, attributeMaps, nowUtc);
                records.Add(new(MappingCoverageRow.ShopScope, scopeId, s.Market, TaxonomyKind.Category, p.Id, p.Sku, p.Name, Safe(p.Category), category.Bucket, category.Words));
                records.Add(new(MappingCoverageRow.ShopScope, scopeId, s.Market, TaxonomyKind.Brand, p.Id, p.Sku, p.Name, Safe(p.Brand), brand.Bucket, brand.Words));
                records.Add(new(MappingCoverageRow.ShopScope, scopeId, s.Market, TaxonomyKind.Attribute, p.Id, p.Sku, p.Name, Safe(p.AttributesText), attribute.Bucket, attribute.Words));
            }
        }

        var rows = new List<MappingCoverageRow>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal); foreach (var source in sources) names.TryAdd(source.Id ?? "", source.Name ?? "");
        var sourceIds = sources.Select(x => x.Id ?? "").Concat(products.Select(p => p.SourceId ?? "")).Distinct(StringComparer.Ordinal)
            .OrderBy(id => SourceName(names, id), StringComparer.CurrentCultureIgnoreCase).ThenBy(id => id, StringComparer.Ordinal).ToList();
        foreach (var id in sourceIds) foreach (var kind in Kinds) rows.Add(Aggregate(MappingCoverageRow.SourceScope, id, SourceName(names, id), kind, records.Where(r => r.Scope == MappingCoverageRow.SourceScope && r.ScopeId == id && r.Kind == kind)));
        foreach (var channel in shops.Select(s => s.Market).Distinct(StringComparer.Ordinal))
        {
            var channelName = MarketplaceConnectionCatalog.All.FirstOrDefault(d => d.Id.Equals(channel, StringComparison.OrdinalIgnoreCase))?.Name ?? channel;
            foreach (var kind in Kinds) rows.Add(Aggregate(MappingCoverageRow.ChannelScope, channel, channelName, kind, records.Where(r => r.Scope == MappingCoverageRow.ShopScope && r.Channel == channel && r.Kind == kind)));
            foreach (var s in shops.Where(x => x.Market == channel))
            {
                var scopeId = s.Market + "/" + s.Shop;
                foreach (var kind in Kinds) rows.Add(Aggregate(MappingCoverageRow.ShopScope, scopeId, s.Name.Length > 0 ? s.Name : scopeId, kind, records.Where(r => r.Scope == MappingCoverageRow.ShopScope && r.ScopeId == scopeId && r.Kind == kind)));
            }
        }
        return new(nowUtc, rows, records);
    }

    public static string Describe(MappingCoverageRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var scope = row.Scope switch { MappingCoverageRow.SourceScope => "kaynak", MappingCoverageRow.ShopScope => "mağaza", MappingCoverageRow.ChannelScope => "kanal", _ => row.Scope };
        var kind = row.Kind switch { TaxonomyKind.Category => "kategori", TaxonomyKind.Brand => "marka", _ => "özellik" };
        var state = row.State switch { MappingCoverageRow.Empty => "boş", MappingCoverageRow.Partial => "kısmi", MappingCoverageRow.StaleState => "stale", _ => "tam" };
        if (row.Total == 0) return $"{scope} {row.ScopeName} · {kind}: kayıt yok ({state}).";
        return $"{scope} {row.ScopeName} · {kind}: {N(row.Total)} kayıttan {N(row.Mapped)} eşli (%{N(row.Percent)}) · {N(row.Unmapped)} eşlenmemiş · {N(row.Stale)} stale ({state}).";
    }

    public static string Summarize(MappingCoverageReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Rows.Count == 0) return "Kapsam raporu boş: kaynak, ürün veya mağaza yok.";
        int Count(string state) => report.Rows.Count(r => r.State == state);
        return $"Kapsam raporu: {N(report.Rows.Count)} satır · {N(Count(MappingCoverageRow.Full))} tam · {N(Count(MappingCoverageRow.Partial))} kısmi · {N(Count(MappingCoverageRow.StaleState))} stale · {N(Count(MappingCoverageRow.Empty))} boş.";
    }

    static string N(int value) => value.ToString(CultureInfo.CurrentCulture);
    static bool Covered(AttributeCoverage coverage) => coverage.Status is AttributeCoverage.Complete or AttributeCoverage.NoRules;
    static string SourceName(IReadOnlyDictionary<string, string> names, string id) => names.TryGetValue(id, out var name) && name.Length > 0 ? name : id.Length == 0 ? "kaynaksız" : id;
    static string Safe(string? text) { var value = (text ?? "").Trim(); return AuditStore.Redact(value.Length > 60 ? value[..60] + "…" : value); }

    static MappingCoverageRow Aggregate(string scope, string scopeId, string scopeName, TaxonomyKind kind, IEnumerable<MappingCoverageRecord> hits)
    {
        int mapped = 0, unmapped = 0, stale = 0;
        foreach (var hit in hits) { if (hit.Bucket == Mapped) mapped++; else if (hit.Bucket == Stale) stale++; else unmapped++; }
        return new(scope, scopeId, scopeName, kind, mapped, unmapped, stale, mapped + unmapped + stale, MappingCoverageRow.StateOf(mapped, unmapped, stale));
    }

    /// <summary>Entries by their name key (active first, so a duplicate label resolves to the live one) and by approved alias key.</summary>
    static Dictionary<string, TaxonomyEntry> KeyMap(IReadOnlyList<TaxonomyEntry> entries, IEnumerable<(string Key, string LocalId)> aliases)
    {
        var byId = entries.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var byKey = new Dictionary<string, TaxonomyEntry>(StringComparer.Ordinal);
        foreach (var entry in entries.OrderByDescending(e => e.Active)) { var key = TaxonomyAliasStore.Key(entry.Name); if (key.Length > 0) byKey.TryAdd(key, entry); }
        foreach (var (key, localId) in aliases) if (key.Length > 0 && byId.TryGetValue(localId, out var entry)) byKey.TryAdd(key, entry);
        return byKey;
    }

    /// <summary>A channel/shop's mappings by local id, the newest per entry, with the moment each was written.</summary>
    static Dictionary<string, (string ExternalKey, DateTime UpdatedUtc)> ShopMappings(TaxonomyStore taxonomy, TaxonomyKind kind, string market, string shop)
    {
        var times = taxonomy.MappingTimes(kind, market, shop);
        var result = new Dictionary<string, (string ExternalKey, DateTime UpdatedUtc)>(StringComparer.Ordinal);
        foreach (var mapping in taxonomy.Mappings(kind).Where(m => m.Marketplace == market && m.ShopId == shop))
        {
            var updated = times.GetValueOrDefault(mapping.ExternalKey);
            if (!result.TryGetValue(mapping.LocalId, out var existing) || existing.UpdatedUtc < updated) result[mapping.LocalId] = (mapping.ExternalKey, updated);
        }
        return result;
    }

    static ProductFacts Facts(CatalogProduct p, IReadOnlyDictionary<string, TaxonomyEntry> categories, IReadOnlyDictionary<string, TaxonomyEntry> brands, IReadOnlyDictionary<string, TaxonomyEntry> attributes, IReadOnlyDictionary<string, string> valueAliases, CategoryAttributeSnapshot rules)
    {
        var category = Known(categories, p.Category, "kategori"); var brand = Known(brands, p.Brand, "marka");
        var coverage = rules.Evaluate(p);
        var entries = new List<TaxonomyEntry>();
        foreach (var (attributeKey, pair) in CategoryAttributeSnapshot.Parse(p.AttributesText))
        {
            var valueKey = AttributeValueMappingStore.Key(pair.Value);
            if (valueAliases.TryGetValue(AttributeValueMappingStore.MapKey(attributeKey, valueKey), out var canonical)) valueKey = AttributeValueMappingStore.Key(canonical);
            if (attributes.TryGetValue(AttributeValueMappingStore.MapKey(attributeKey, valueKey), out var entry)) entries.Add(entry);
        }
        return new(category, brand, coverage, entries);
    }

    static Verdict Known(IReadOnlyDictionary<string, TaxonomyEntry> byKey, string? text, string what)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0) return new(Unmapped, what + " girilmemiş", null);
        var key = TaxonomyAliasStore.Key(value);
        if (key.Length == 0 || !byKey.TryGetValue(key, out var entry)) return new(Unmapped, $"{what} sözlükte yok: {Safe(value)}", null);
        if (!entry.Active) return new(Stale, $"{what} pasif: {entry.Name}", entry);
        return new(Mapped, TaxonomyAliasStore.Key(entry.Name) == key ? entry.Name : $"{entry.Name} (takma ad: {Safe(value)})", entry);
    }

    static Verdict OnChannel(TaxonomyEntry entry, string label, IReadOnlyDictionary<string, (string ExternalKey, DateTime UpdatedUtc)> mappings, DateTime nowUtc)
    {
        if (!mappings.TryGetValue(entry.Id, out var mapping)) return new(Unmapped, "kanal eşlemesi yok: " + label, entry);
        if (mapping.UpdatedUtc < entry.UpdatedUtc) return new(Stale, $"kayıt eşlemeden sonra değişti: {label} (harici anahtar {mapping.ExternalKey}); eşlemeyi doğrulayın", entry);
        if (mapping.UpdatedUtc < nowUtc - StaleAge) return new(Stale, $"eşleme {N((int)(nowUtc - mapping.UpdatedUtc).TotalDays)} gündür doğrulanmadı: {label} → {mapping.ExternalKey}", entry);
        return new(Mapped, $"{label} → {mapping.ExternalKey}", entry);
    }

    static Verdict AttributesOnChannel(ProductFacts f, IReadOnlyDictionary<string, (string ExternalKey, DateTime UpdatedUtc)> mappings, DateTime nowUtc)
    {
        if (!Covered(f.Coverage)) return new(Unmapped, f.Coverage.Words, null);
        if (f.AttributeEntries.Count == 0) return new(Mapped, "eşlenecek özellik değeri yok", null);
        var verdicts = f.AttributeEntries.Select(e => e.Active ? OnChannel(e, $"{e.Name}={e.Value}", mappings, nowUtc) : new Verdict(Stale, $"özellik değeri pasif: {e.Name}={e.Value}", e)).ToList();
        var unmapped = verdicts.Where(v => v.Bucket == Unmapped).ToList();
        if (unmapped.Count > 0) return new(Unmapped, $"{N(unmapped.Count)} özellik değeri eşlenmemiş: {string.Join("; ", unmapped.Take(3).Select(v => v.Words))}", null);
        var stale = verdicts.Where(v => v.Bucket == Stale).ToList();
        if (stale.Count > 0) return new(Stale, $"{N(stale.Count)} özellik değeri stale: {string.Join("; ", stale.Take(3).Select(v => v.Words))}", null);
        return new(Mapped, $"{N(verdicts.Count)} özellik değeri eşli: {string.Join("; ", verdicts.Take(3).Select(v => v.Words))}", null);
    }
}
