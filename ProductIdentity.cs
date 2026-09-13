using System.Text.RegularExpressions;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public enum ProductKeyRole { Canonical, Sku, Barcode, SourceRecord, ChannelListing }

/// <summary>One key's role in the product identity contract: what it is for, and whether modules may join on it.</summary>
public sealed record ProductKeyContract(ProductKeyRole Role, string Field, string Purpose, bool JoinsAcrossModules);

/// <summary>The outcome of matching an external key pair (SKU, barcode) to the catalogue: matched, none, ambiguous (several products) or conflict (the SKU and the barcode name different products).</summary>
public sealed record ProductMatch(string Outcome, CatalogProduct? Product, IReadOnlyList<CatalogProduct> Candidates, string Reason)
{
    public bool IsMatch => Outcome == ProductIdentity.Matched;
}

/// <summary>
/// The canonical product identity contract (#902). A product has one identity every module joins on — its
/// internal id, minted once and never derived from a code — and several external keys that match records to it:
/// the SKU (the operator's and the feeds' code), the barcode (GTIN/EAN), the source record (which feed carried it;
/// a remap changes the home, never the identity) and the channel listing ids (a marketplace's own id, keyed by the
/// canonical id and the channel). This owner holds the one matching rule the import, the import preview and the
/// order lines all use, so a duplicate SKU or a barcode that names another product is refused the same way
/// everywhere, and a row with no barcode still matches by its SKU.
/// </summary>
public static class ProductIdentity
{
    public const string Matched = "matched", None = "none", Ambiguous = "ambiguous", Conflict = "conflict";
    public const string AmbiguousWords = "Barkod veya SKU birden fazla ürünle eşleşiyor.";
    public const string ConflictWords = "SKU ve barkod farklı ürünlerle eşleşiyor.";
    public const string NoSingleWords = "tek bir merkezi ürün eşleşmesi bulunamadı";
    public const string RemapAction = "product-remap";
    static readonly Regex CanonicalShape = new("^[0-9a-f]{32}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<ProductKeyContract> Contract { get; } = new[]
    {
        new ProductKeyContract(ProductKeyRole.Canonical, "CatalogProduct.Id", "the one identity every module joins on (channel plans, sightings, field origins, stock, history); minted once, never reused, never derived from a code", true),
        new ProductKeyContract(ProductKeyRole.Sku, "CatalogProduct.Sku", "the operator's and the feeds' product code; matches a feed row or an order line to a product; may be missing on a barcode-only row; must name one product when used as a key", false),
        new ProductKeyContract(ProductKeyRole.Barcode, "CatalogProduct.Barcode", "GTIN/EAN as the second match key; may be missing; a barcode naming a different product than the SKU is a conflict, never a tie-break; a value of 8/12/13/14 digits with a correct check digit is a GTIN (GtinCode), anything else a custom code", false),
        new ProductKeyContract(ProductKeyRole.SourceRecord, "CatalogProduct.SourceId + the feed row's keys", "which feed carried the product (home source, sightings per source); a remap changes the home, never the canonical id", false),
        new ProductKeyContract(ProductKeyRole.ChannelListing, "ChannelProductPlan.ListingId / CatalogProduct.EtsyListingId", "a marketplace's own id for the listing, kept beside the canonical id and the channel; never a join key inside the catalogue", false),
    };

    public static string NormalizeCode(string? value) => (value ?? "").Trim();
    public static bool IsCanonical(string? id) => id is not null && CanonicalShape.IsMatch(id);
    public static string NewCanonical() => Guid.NewGuid().ToString("N");

    /// <summary>The feed-row and preview rule: the SKU first, the barcode when the SKU matches nothing; several matches are ambiguous; a barcode naming a different product than the SKU is a conflict.</summary>
    public static ProductMatch Resolve(IEnumerable<CatalogProduct> pool, string? sku, string? barcode)
    {
        ArgumentNullException.ThrowIfNull(pool);
        var list = pool as IReadOnlyList<CatalogProduct> ?? pool.ToList();
        var code = NormalizeCode(sku); var bar = NormalizeCode(barcode);
        var bySku = code.Length == 0 ? new List<CatalogProduct>() : list.Where(p => string.Equals(NormalizeCode(p.Sku), code, StringComparison.Ordinal)).ToList();
        var byBarcode = bar.Length == 0 ? new List<CatalogProduct>() : list.Where(p => string.Equals(NormalizeCode(p.Barcode), bar, StringComparison.Ordinal)).ToList();
        return Decide(bySku, byBarcode, code, bar);
    }

    /// <summary>The same rule over pre-built lookups (the import's indexes), so the store and every caller decide identically.</summary>
    public static ProductMatch Resolve(IReadOnlyList<CatalogProduct> bySku, IReadOnlyList<CatalogProduct> byBarcode, string? sku, string? barcode)
    {
        ArgumentNullException.ThrowIfNull(bySku); ArgumentNullException.ThrowIfNull(byBarcode);
        return Decide(bySku, byBarcode, NormalizeCode(sku), NormalizeCode(barcode));
    }

    /// <summary>The order-line rule: the SKU alone, and exactly one product.</summary>
    public static ProductMatch ResolveSku(IEnumerable<CatalogProduct> pool, string? sku)
    {
        ArgumentNullException.ThrowIfNull(pool);
        var code = NormalizeCode(sku);
        var matches = code.Length == 0 ? new List<CatalogProduct>() : pool.Where(p => string.Equals(NormalizeCode(p.Sku), code, StringComparison.Ordinal)).ToList();
        return matches.Count switch
        {
            0 => new(None, null, matches, NoSingleWords),
            1 => new(Matched, matches[0], matches, "SKU ile eşleşti"),
            _ => new(Ambiguous, null, matches, NoSingleWords + " (birden fazla ürün aynı SKU'yu taşıyor)"),
        };
    }

    static ProductMatch Decide(IReadOnlyList<CatalogProduct> bySku, IReadOnlyList<CatalogProduct> byBarcode, string code, string bar)
    {
        var matches = bySku.Count > 0 ? bySku : byBarcode;
        if (matches.Count == 0) return new(None, null, matches, code.Length == 0 && bar.Length == 0 ? "SKU ve barkod yok" : "havuzda eşleşme yok");
        if (matches.Count == 1 && byBarcode.Any(p => p.Id != matches[0].Id)) return new(Conflict, null, matches.Concat(byBarcode).DistinctBy(p => p.Id).ToList(), ConflictWords);
        if (matches.Count > 1) return new(Ambiguous, null, matches, AmbiguousWords);
        return new(Matched, matches[0], matches, bySku.Count == 1 ? "SKU ile eşleşti" : "barkod ile eşleşti");
    }
}
