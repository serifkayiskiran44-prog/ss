using System.Globalization;

namespace TrMarketplaceHubDesktop.Catalog;

public enum MappingImpactLevel { None, Small, Large }

/// <summary>What a pending channel mapping change would touch, before it touches anything: the level, the products whose readiness on that channel changes, their plans, a few sample SKUs, the snapshot version the preview was built on, and the words.</summary>
public sealed record MappingImpactPreview(TaxonomyKind Kind, string Marketplace, string ShopId, string ExternalKey, string LocalId, string LocalName, MappingImpactLevel Level, int AffectedProducts, int AffectedPlans, IReadOnlyList<string> SampleSkus, string Scope, long SnapshotVersion, bool Rebinds, string Headline, IReadOnlyList<string> Lines)
{
    /// <summary>A change that touches something asks first; one with no impact does not.</summary>
    public bool RequiresConfirmation => Level != MappingImpactLevel.None;
    public string Body => string.Join(Environment.NewLine, new[] { Headline }.Concat(Lines));
}

/// <summary>
/// The impact preview of a channel mapping change (#919). Before an external category, brand or attribute key is
/// bound to a local entry for a channel and shop, the preview counts what the binding touches: every product that
/// resolves to that local entry (a category by name or approved alias, a brand by name or approved alias, an
/// attribute by its name in the product's attributes) has its readiness on that channel changed, and every plan of
/// those products on that channel is affected; a rebinding (the external key already mapped elsewhere) is said.
/// Fifty products or more is a large impact; none is none and asks nothing. The preview is bound to the channel
/// scope's taxonomy snapshot version (#918): applying it refreshes the scope first and refuses when the version
/// moved since the preview — someone changed the taxonomy in between, so the counts are not the counts any more
/// and the operator previews again. A cancel writes nothing; a preview writes nothing.
/// </summary>
public static class MappingImpact
{
    public const int LargeThreshold = 50;
    public const int SampleLimit = 5;

    public static MappingImpactPreview Preview(string? directory, TaxonomyKind kind, string externalKey, string localId, string marketplace, string shopId, IReadOnlyList<CatalogProduct> products, IReadOnlyList<ChannelProductPlan> plans)
    {
        ArgumentNullException.ThrowIfNull(products); ArgumentNullException.ThrowIfNull(plans);
        var market = (marketplace ?? "").Trim().ToLowerInvariant(); var shop = (shopId ?? "").Trim(); var key = (externalKey ?? "").Trim();
        var taxonomy = new TaxonomyStore(directory);
        var entry = taxonomy.List(kind).FirstOrDefault(e => e.Id == localId);
        var localName = entry?.Name ?? "bilinmeyen kayıt";
        var affected = entry is null ? new List<CatalogProduct>() : products.Where(p => Resolves(directory, kind, entry, p)).ToList();
        var affectedIds = new HashSet<string>(affected.Select(p => p.Id), StringComparer.Ordinal);
        var affectedPlans = plans.Count(pl => pl.ChannelId.Equals(market, StringComparison.OrdinalIgnoreCase) && pl.ShopId == shop && affectedIds.Contains(pl.ProductId));
        var rebinds = taxonomy.Mappings(kind).Any(m => m.Marketplace == market && m.ShopId == shop && m.ExternalKey == key && m.LocalId != localId);
        var scope = TaxonomySnapshotStore.ChannelScope(market, shop);
        var version = new TaxonomySnapshotStore(directory).Refresh(scope, DateTime.UtcNow).Version;
        var level = affected.Count == 0 ? MappingImpactLevel.None : affected.Count >= LargeThreshold ? MappingImpactLevel.Large : MappingImpactLevel.Small;
        var kindWord = kind switch { TaxonomyKind.Category => "kategori", TaxonomyKind.Brand => "marka", _ => "özellik" };
        var headline = level == MappingImpactLevel.None
            ? $"'{key}' → {localName} ({kindWord}, {market}/{shop}): hiçbir ürünü etkilemiyor."
            : $"'{key}' → {localName} ({kindWord}, {market}/{shop}): {affected.Count.ToString("N0", CultureInfo.CurrentCulture)} ürünün bu kanaldaki hazırlığı değişir" + (level == MappingImpactLevel.Large ? " — büyük etki." : ".");
        var lines = new List<string>();
        if (affectedPlans > 0) lines.Add($"{affectedPlans.ToString("N0", CultureInfo.CurrentCulture)} kanal planı bu eşlemeyle yeniden doğrulanacak.");
        if (affected.Count > 0) lines.Add("Örnek: " + string.Join(", ", affected.Take(SampleLimit).Select(p => p.Sku)) + (affected.Count > SampleLimit ? ", …" : ""));
        if (rebinds) lines.Add($"'{key}' zaten başka bir yerel kayda bağlı; bu işlem eşlemeyi taşır.");
        lines.Add($"Önizleme taksonomi sürümü {version.ToString(CultureInfo.CurrentCulture)} üzerinde alındı; sürüm değişirse uygulama reddedilir.");
        return new(kind, market, shop, key, localId, localName, level, affected.Count, affectedPlans, affected.Take(SampleLimit).Select(p => p.Sku).ToList(), scope, version, rebinds, headline, lines);
    }

    /// <summary>The stale guard: the scope refreshed now must still be the version the preview was built on; otherwise the words say so and nothing is applied.</summary>
    public static void EnsureCurrent(string? directory, MappingImpactPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var current = new TaxonomySnapshotStore(directory).Refresh(preview.Scope, DateTime.UtcNow).Version;
        if (current != preview.SnapshotVersion) throw new InvalidOperationException($"Taksonomi bu önizlemeden sonra değişti (sürüm {preview.SnapshotVersion.ToString(CultureInfo.CurrentCulture)} → {current.ToString(CultureInfo.CurrentCulture)}); etkiyi yeniden önizleyin.");
    }

    /// <summary>Applies a previewed mapping: the stale guard first, then the mapping through the taxonomy owner, then the scope's snapshot refreshed so the next preview binds to the new version.</summary>
    public static TaxonomySnapshot Apply(string? directory, MappingImpactPreview preview)
    {
        EnsureCurrent(directory, preview);
        new TaxonomyStore(directory).Map(preview.Kind, preview.ExternalKey, preview.LocalId, preview.Marketplace, preview.ShopId);
        return new TaxonomySnapshotStore(directory).Refresh(preview.Scope, DateTime.UtcNow);
    }

    static bool Resolves(string? directory, TaxonomyKind kind, TaxonomyEntry entry, CatalogProduct product)
    {
        switch (kind)
        {
            case TaxonomyKind.Category:
            {
                var text = (product.Category ?? "").Trim(); if (text.Length == 0) return false;
                var key = TaxonomyAliasStore.Key(text);
                if (key == TaxonomyAliasStore.Key(entry.Name)) return true;
                return new TaxonomyAliasStore(directory).Resolve(text)?.LocalId == entry.Id;
            }
            case TaxonomyKind.Brand:
            {
                var text = (product.Brand ?? "").Trim(); if (text.Length == 0) return false;
                if (BrandMappingStore.Key(text) == BrandMappingStore.Key(entry.Name)) return true;
                return new BrandMappingStore(directory).ListAliases().Any(a => a.Status == BrandAliasView.ApprovedStatus && a.Key == BrandMappingStore.Key(text) && a.BrandId == entry.Id);
            }
            default:
                return CategoryAttributeSnapshot.Parse(product.AttributesText).ContainsKey(CategoryAttributeRuleStore.Key(entry.Name));
        }
    }
}
