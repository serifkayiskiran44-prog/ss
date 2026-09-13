using System.Globalization;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public sealed record ProductQuickInspectRow(string Section, string Label, string Value);

public sealed record ProductQuickInspectView(IReadOnlyList<ProductQuickInspectRow> Rows)
{
    public IReadOnlyList<string> Sections => Rows.Select(r => r.Section).Distinct(StringComparer.Ordinal).ToArray();
    public IEnumerable<ProductQuickInspectRow> InSection(string section) => Rows.Where(r => r.Section.Equals(section, StringComparison.Ordinal));
}

/// <summary>
/// The read-only quick-inspect content for a product row (#796). It is a flat list of label/value rows on
/// purpose: there is nothing in this model a drawer could bind two-way to, so "read-only" is a property of the
/// shape rather than a rule someone has to remember. Missing data is rendered as an em dash rather than left
/// blank, every value goes through <see cref="AuditStore.Sanitize"/> and a length cap -- a product name or a
/// supplier's description is free text that has, in practice, contained tokens and customer contact details.
/// </summary>
public static class ProductQuickInspect
{
    const string Dash = "—";

    public static ProductQuickInspectView Build(CatalogProduct product, IReadOnlyList<SyncJob> jobs, DateTime nowUtc, IReadOnlyList<XmlSource>? sources = null)
    {
        ArgumentNullException.ThrowIfNull(product); ArgumentNullException.ThrowIfNull(jobs);
        var rows = new List<ProductQuickInspectRow>();
        void Add(string section, string label, string? value) => rows.Add(new(section, label, Clean(value)));

        Add("Kimlik", "SKU", product.Sku);
        Add("Kimlik", "Barkod", product.Barcode);
        Add("Kimlik", "GTIN", string.IsNullOrWhiteSpace(product.Gtin) ? null : product.Gtin + " · " + GtinCode.Inspect(product.Gtin).Words); // #906
        Add("Kimlik", "Ürün adı", product.Name);
        Add("Kimlik", "Marka / kategori", Join(product.Brand, product.Category));

        Add("Fiyat", "Satış fiyatı", product.Price > 0 ? Money(product.Price, product.Currency) : null);
        Add("Fiyat", "Alış fiyatı", product.Cost > 0 ? Money(product.Cost, product.CostCurrency) : null);
        Add("Fiyat", "KDV", product.VatRate.ToString("0.##", CultureInfo.CurrentCulture) + "%");

        Add("Stok", "Stok", product.Stock.ToString("N0", CultureInfo.CurrentCulture));
        Add("Stok", "Kilitler", Join(product.LockPrice ? "fiyat" : "", product.LockStock ? "stok" : "", product.LockName ? "başlık" : "", product.LockImages ? "görsel" : ""));
        // #907: the canonical weight and box beside the texts as given.
        Add("Stok", "Ağırlık (kargo)", ProductUnits.DescribeWeight(product));
        Add("Stok", "Boyut (kargo)", ProductUnits.DescribeDimensions(product));
        Add("Stok", "Desi (kargo)", ProductDimensions.Describe(product)); // #908: the desi with its origin, or why there is none

        Add("Kaynak", "Kaynak türü", product.SourceKind);
        Add("Kaynak", "Son kaynak güncellemesi", product.SourceUpdatedUtc is { } touched ? Ago(nowUtc - touched) : null);
        Add("Kaynak", "Satır durumu", ProductRowState.Classify(product, nowUtc).Badge);
        // #903: field-level freshness, only when the caller can name the sources (the thresholds are theirs).
        if (sources is not null)
            foreach (var field in ProductFreshness.Evaluate(product, id => sources.FirstOrDefault(s => s.Id == id), nowUtc).Fields) Add("Güncellik", field.Label, field.Words);

        var missing = Missing(product);
        Add("Hazırlık", "Durum", missing.Count == 0 ? "Hazır" : $"Eksik ({missing.Count})");
        Add("Hazırlık", "Eksikler", missing.Count == 0 ? null : string.Join(", ", missing));
        Add("Hazırlık", "Etsy ilanı", string.IsNullOrWhiteSpace(product.EtsyListingId) ? (product.EtsyCreationAttempted ? "Gönderim denendi, ilan kimliği yok" : null) : product.EtsyListingId);

        var failure = jobs.Where(j => j is not null && j.Status == SyncStatus.Failed).OrderByDescending(j => j.UpdatedUtc).FirstOrDefault();
        Add("Son hata", "Son hata", failure?.LastError);
        Add("Son hata", "Zamanı", failure is null ? null : Ago(nowUtc - failure.UpdatedUtc));

        return new(rows);
    }

    static List<string> Missing(CatalogProduct product)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(product.Sku) && string.IsNullOrWhiteSpace(product.Barcode)) missing.Add("SKU veya barkod");
        else if (string.IsNullOrWhiteSpace(product.Sku)) missing.Add("SKU");
        if (string.IsNullOrWhiteSpace(product.Name)) missing.Add("ürün adı");
        if (product.Price <= 0) missing.Add("satış fiyatı");
        if (string.IsNullOrWhiteSpace(product.Description)) missing.Add("açıklama");
        if (string.IsNullOrWhiteSpace(product.ImageUrls)) missing.Add("görsel");
        return missing;
    }

    static string Money(decimal amount, string currency) => amount.ToString("N2", CultureInfo.CurrentCulture) + " " + (string.IsNullOrWhiteSpace(currency) ? "" : currency.Trim().ToUpperInvariant());

    static string Join(params string[] parts) => string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    static string Ago(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        if (elapsed.TotalMinutes < 1) return "az önce";
        if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes} dakika önce";
        if (elapsed.TotalDays < 1) return $"{(int)elapsed.TotalHours} saat önce";
        return $"{(int)elapsed.TotalDays} gün önce";
    }

    static string Clean(string? value)
    {
        var safe = AuditStore.Sanitize(value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (safe.Length == 0) return Dash;
        return safe.Length > 300 ? safe[..300] : safe;
    }
}
