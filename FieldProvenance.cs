using System.Globalization;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

/// <summary>Where one product field's value came from: a feed (its source, the source's configuration revision and the import run) or the operator, and when it was observed.</summary>
public sealed class FieldOrigin
{
    public string Kind { get; set; } = FieldProvenance.ManualKind;
    public string SourceId { get; set; } = "";
    public int SourceRevision { get; set; }
    public string RunId { get; set; } = "";
    public DateTime ObservedUtc { get; set; }
    /// <summary>#896: the words of the last priority decision on this field, when two sources contested it; empty otherwise.</summary>
    public string Decision { get; set; } = "";
}

/// <summary>
/// Field-level provenance (#895). Every product field the import writes — cost, price and currency, stock, name,
/// description, images, GTIN — carries the source it came from, the source's configuration revision (#893), the
/// import run (#8xx) and the moment it was observed; a field the operator writes carries "manual" and the moment.
/// The origins live on the product record (JSON, no migration) and are written by the same transactions that
/// write the values, so a restart shows what was recorded and a deleted source is still named by its id while the
/// view says the source is gone. Nothing here holds a value, an address or a credential — ids, numbers and times only.
/// </summary>
public static class FieldProvenance
{
    public const string ManualKind = "manual";
    public const string FeedKind = "xml";
    /// <summary>#908: computed from other fields of the same record (the desi from the box), so neither a feed's nor the operator's.</summary>
    public const string DerivedKind = "derived";
    public static readonly IReadOnlyList<string> Fields = new[] { "Cost", "Price", "Currency", "Stock", "Name", "Description", "ImageUrls", "Gtin", "WeightText", "DimensionsText", "Desi", "TaxClass", "CountryOfOrigin" }; // #908: the box texts and the desi; #909: the tax class; #910: the country of origin

    /// <summary>Stamps the fields a feed just wrote on a product.</summary>
    public static void StampFeed(CatalogProduct product, IEnumerable<string> fields, string sourceId, int sourceRevision, string runId, DateTime observedUtc)
    {
        ArgumentNullException.ThrowIfNull(product); ArgumentNullException.ThrowIfNull(fields);
        product.FieldOrigins ??= new Dictionary<string, FieldOrigin>(StringComparer.Ordinal);
        foreach (var field in fields)
            product.FieldOrigins[field] = new FieldOrigin { Kind = FeedKind, SourceId = sourceId ?? "", SourceRevision = Math.Max(0, sourceRevision), RunId = (runId ?? "").Trim(), ObservedUtc = observedUtc };
    }

    /// <summary>Stamps as the operator's every tracked field whose value differs between the record as it was and as it is being saved.</summary>
    public static IReadOnlyList<string> StampManual(CatalogProduct before, CatalogProduct after, DateTime observedUtc)
    {
        ArgumentNullException.ThrowIfNull(before); ArgumentNullException.ThrowIfNull(after);
        after.FieldOrigins ??= new Dictionary<string, FieldOrigin>(before.FieldOrigins ?? new Dictionary<string, FieldOrigin>(), StringComparer.Ordinal);
        var changed = new List<string>();
        foreach (var field in Fields)
        {
            if (string.Equals(ValueOf(before, field), ValueOf(after, field), StringComparison.Ordinal)) continue;
            after.FieldOrigins[field] = new FieldOrigin { Kind = ManualKind, ObservedUtc = observedUtc };
            changed.Add(field);
        }
        return changed;
    }

    public static FieldOrigin? Of(CatalogProduct product, string field) => product?.FieldOrigins is { } origins && origins.TryGetValue(field, out var origin) ? origin : null;

    /// <summary>The words for one origin: "kaynak · rev. 3 · çalıştırma a1b2c3d4 · 2 sa önce", "elle · 5 dk önce", or "kaydedilmedi" when the field predates provenance; a source that no longer exists is said to be gone.</summary>
    public static string Describe(FieldOrigin? origin, Func<string, XmlSource?> sourceById, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(sourceById);
        if (origin is null) return "kaydedilmedi";
        var when = Ago(nowUtc - origin.ObservedUtc);
        if (string.Equals(origin.Kind, DerivedKind, StringComparison.OrdinalIgnoreCase)) return $"boyuttan hesaplandı · {when}"; // #908
        if (string.Equals(origin.Kind, ManualKind, StringComparison.OrdinalIgnoreCase)) return $"elle · {when}" + (origin.Decision.Length > 0 ? " · " + origin.Decision : "");
        var source = origin.SourceId.Length > 0 ? sourceById(origin.SourceId) : null;
        var who = source is null ? (origin.SourceId.Length > 0 ? "kaynak silinmiş" : "kaynak bilinmiyor") : AuditStore.Redact(source.Name).Trim();
        var revision = origin.SourceRevision > 0 ? $" · rev. {origin.SourceRevision.ToString(CultureInfo.CurrentCulture)}" : "";
        var run = origin.RunId.Length > 0 ? $" · çalıştırma {origin.RunId[..Math.Min(8, origin.RunId.Length)]}" : "";
        var decision = origin.Decision.Length > 0 ? " · " + origin.Decision : "";
        return $"{who}{revision}{run} · {when}{decision}";
    }

    static string ValueOf(CatalogProduct p, string field) => field switch
    {
        "Cost" => p.Cost.ToString(CultureInfo.InvariantCulture), "Price" => p.Price.ToString(CultureInfo.InvariantCulture), "Currency" => p.Currency ?? "",
        "Stock" => p.Stock.ToString(CultureInfo.InvariantCulture),
        "Name" => TitleNormalizer.Normalize(p.Name).Normalized, // #905: whitespace and invisible characters are not an edit
        "Description" => p.Description ?? "", "ImageUrls" => p.ImageUrls ?? "", "Gtin" => p.Gtin ?? "",
        "WeightText" => p.WeightText ?? "", "DimensionsText" => p.DimensionsText ?? "", "Desi" => p.Desi?.ToString(CultureInfo.InvariantCulture) ?? "", // #908
        "TaxClass" => ProductTaxClass.Canonical(p.TaxClass), // #909
        "CountryOfOrigin" => (p.CountryOfOrigin ?? "").Trim(), // #910
        _ => "",
    };

    static string Ago(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalMinutes < 1) return "az önce";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} dk önce";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} sa önce";
        return $"{(int)span.TotalDays} gün önce";
    }
}
