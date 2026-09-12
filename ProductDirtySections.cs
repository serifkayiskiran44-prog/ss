using System.Reflection;
using System.Text.Json;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public sealed record ProductDirtySection(string Key, string Label, IReadOnlyList<string> Fields);
public sealed record ProductDirtyState(IReadOnlyList<ProductDirtySection> Sections, string Summary)
{
    public bool IsDirty => Sections.Count > 0;
    public bool Contains(string sectionKey) => Sections.Any(s => s.Key.Equals(sectionKey, StringComparison.Ordinal));
}

/// <summary>
/// Which workspace sections hold unsaved edits (#802). The existing guard already refuses to lose an edit; what
/// this adds is *which* part is unsaved and the ability to undo one section without discarding the rest.
/// Changes are attributed by property, and the indicator carries field **names** only -- putting the old and
/// new value of a cost or a customer-facing description into a banner leaks exactly the numbers an operator
/// would not want on a shared screen. Every editable property is mapped to a section, and a test fails if one
/// is ever added without a home, so a change can never happen with no indicator at all.
/// </summary>
public static class ProductDirtySections
{
    // Property -> (section key, operator-facing field name). Read-only/derived properties are excluded below.
    static readonly Dictionary<string, (string Section, string Label)> Map = new(StringComparer.Ordinal)
    {
        ["Sku"] = ("identity", "SKU"),
        ["Barcode"] = ("identity", "Barkod"),
        ["Gtin"] = ("identity", "GTIN"),
        ["Mpn"] = ("identity", "MPN"),
        ["Shelf"] = ("identity", "Raf"),
        ["ExpiresOn"] = ("identity", "Son kullanma"),
        ["Active"] = ("identity", "Durum"),
        ["SourceId"] = ("identity", "Kaynak kimliği"),
        ["SourceKind"] = ("identity", "Kaynak türü"),
        ["SourceMissing"] = ("identity", "Kaynak durumu"),
        ["Anomaly"] = ("identity", "Anomali işareti"),
        ["Duplicate"] = ("identity", "Yinelenen işareti"),
        ["Id"] = ("identity", "Kimlik"),
        ["UpdatedUtc"] = ("identity", "Güncelleme zamanı"),
        ["SourceUpdatedUtc"] = ("identity", "Kaynak güncelleme zamanı"),

        ["Name"] = ("content", "Başlık"),
        ["Description"] = ("content", "Açıklama"),
        ["Brand"] = ("content", "Marka"),
        ["Category"] = ("content", "Kategori"),
        ["Subtitle"] = ("content", "Alt başlık"),
        ["InvoiceName"] = ("content", "Fatura adı"),
        ["LockName"] = ("content", "Başlık kilidi"),
        ["LockDescription"] = ("content", "Açıklama kilidi"),

        ["Price"] = ("price-stock", "Satış fiyatı"),
        ["Currency"] = ("price-stock", "Satış para birimi"),
        ["Cost"] = ("price-stock", "Alış fiyatı"),
        ["CostCurrency"] = ("price-stock", "Alış para birimi"),
        ["VatRate"] = ("price-stock", "KDV oranı"),
        ["Stock"] = ("price-stock", "Stok"),
        ["Desi"] = ("price-stock", "Desi"),
        ["LockPrice"] = ("price-stock", "Fiyat kilidi"),
        ["LockStock"] = ("price-stock", "Stok kilidi"),
        ["PriceFields"] = ("price-stock", "Fiyat alanları"),
        ["PriceSource"] = ("price-stock", "Fiyat kaynağı"),
        ["StockSource"] = ("price-stock", "Stok kaynağı"),
        ["FormulaPriceTry"] = ("price-stock", "Formül fiyatı"),
        ["AppliedTryRate"] = ("price-stock", "Uygulanan kur"),
        ["FxRateDate"] = ("price-stock", "Kur tarihi"),

        ["ImageUrls"] = ("media", "Görsel URL'leri"),
        ["LockImages"] = ("media", "Görsel kilidi"),
        ["MediaSource"] = ("media", "Medya kaynağı"),

        ["EtsyListingId"] = ("channel", "Etsy ilan kimliği"),
        ["EtsyCreationAttempted"] = ("channel", "Etsy gönderim denemesi"),
    };

    static IEnumerable<PropertyInfo> Editable() => typeof(CatalogProduct)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite);

    /// <summary>Editable properties with no section: a change to one of these would produce no indicator.</summary>
    public static IEnumerable<string> UnmappedEditableProperties() => Editable().Select(p => p.Name).Where(n => !Map.ContainsKey(n)).OrderBy(n => n, StringComparer.Ordinal);

    public static CatalogProduct Clone(CatalogProduct product) => JsonSerializer.Deserialize<CatalogProduct>(JsonSerializer.Serialize(product))!;

    public static ProductDirtyState Compare(CatalogProduct baseline, CatalogProduct current)
    {
        ArgumentNullException.ThrowIfNull(baseline); ArgumentNullException.ThrowIfNull(current);
        var changed = new List<(string Section, string Label)>();
        foreach (var property in Editable())
        {
            if (!Map.TryGetValue(property.Name, out var mapped)) continue;
            // Values are compared as JSON so lists and nullable value types behave, but the values themselves
            // never leave this method -- only the field's name travels into the indicator.
            var before = JsonSerializer.Serialize(property.GetValue(baseline));
            var after = JsonSerializer.Serialize(property.GetValue(current));
            if (!string.Equals(before, after, StringComparison.Ordinal)) changed.Add(mapped);
        }

        var sections = ProductWorkspaceSections.All
            .Select(section => new ProductDirtySection(section.Key, section.Label, changed.Where(c => c.Section == section.Key).Select(c => c.Label).Distinct(StringComparer.Ordinal).ToArray()))
            .Where(s => s.Fields.Count > 0)
            .ToArray();

        var fieldCount = sections.Sum(s => s.Fields.Count);
        var summary = sections.Length == 0 ? "" : $"Kaydedilmemiş değişiklik: {sections.Length} bölüm, {fieldCount} alan";
        return new(sections, summary);
    }

    /// <summary>Undo one section's edits, keeping every other section's unsaved work.</summary>
    public static CatalogProduct ResetSection(CatalogProduct baseline, CatalogProduct current, string sectionKey)
    {
        ArgumentNullException.ThrowIfNull(baseline); ArgumentNullException.ThrowIfNull(current);
        var result = Clone(current);
        foreach (var property in Editable())
            if (Map.TryGetValue(property.Name, out var mapped) && mapped.Section.Equals(sectionKey, StringComparison.Ordinal))
                property.SetValue(result, property.GetValue(baseline));
        return result;
    }
}
