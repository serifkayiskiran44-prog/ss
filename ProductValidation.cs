using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public sealed record ProductValidationFinding(string Severity, string Section, string Field, string Message);

public sealed record ProductValidationResult(IReadOnlyList<ProductValidationFinding> Findings)
{
    public bool HasBlocking => Findings.Any(f => f.Severity == ProductValidation.Blocking);
    public ProductValidationFinding? FirstBlocking => Findings.FirstOrDefault(f => f.Severity == ProductValidation.Blocking);
    public string FirstBlockingRoute => FirstBlocking is { } first ? ProductWorkspaceSections.Route(first.Section) : "";
    public IEnumerable<ProductValidationFinding> InSection(string section) => Findings.Where(f => f.Section.Equals(section, StringComparison.Ordinal));
    public string Summary
    {
        get
        {
            var blocking = Findings.Count(f => f.Severity == ProductValidation.Blocking);
            var warning = Findings.Count(f => f.Severity == ProductValidation.Warning);
            if (blocking == 0 && warning == 0) return "Kaydetmeyi engelleyen sorun yok.";
            return blocking == 0 ? $"{warning} uyarı" : $"{blocking} engelleyici sorun" + (warning > 0 ? $", {warning} uyarı" : "");
        }
    }
}

/// <summary>
/// One evaluation of a product's validity (#803), shared by the workspace's summary panel and by
/// <see cref="CatalogStore"/>'s save-time refusal, so the panel can never promise a save the store will reject
/// -- the failure mode of writing the rules twice. Findings are ordered by the workspace's own section order,
/// which is what makes "the first blocker" a meaningful thing to link to. Messages name the field and the rule
/// (a limit, a required value) and never quote the offending content: product text has carried tokens and
/// customer addresses, and an error banner is the last place they should surface.
/// </summary>
public static class ProductValidation
{
    public const string Blocking = "blocking";
    public const string Warning = "warning";
    public const string Info = "info";

    public static ProductValidationResult Evaluate(CatalogProduct product)
    {
        ArgumentNullException.ThrowIfNull(product);
        var findings = new List<ProductValidationFinding>();
        void Add(string severity, string section, string field, string message) => findings.Add(new(severity, section, field, message));

        // --- Blocking: exactly the rules CatalogStore.Valid enforces, in one place. ---
        void Length(string section, string field, string? value, int max)
        {
            if ((value ?? "").Length > max) Add(Blocking, section, field, $"{field} en fazla {max} karakter olabilir.");
        }
        Length("identity", "MPN", product.Mpn, 128);
        Length("content", "Fatura adı", product.InvoiceName, 300);
        Length("content", "Alt başlık", product.Subtitle, 300);
        Length("identity", "Raf", product.Shelf, 100);
        // #905: the title pipeline never cuts -- over the limit is refused with the excess counted.
        if (TitleNormalizer.OverLimit(product.Name) is { } overLimit) Add(Blocking, "content", "Başlık", overLimit);
        Length("identity", "SKU", product.Sku, 128);
        Length("identity", "Barkod", product.Barcode, 64);
        // #906: a GTIN must be a GTIN (8/12/13/14 digits, correct check digit); an invalid one is refused, never corrected. A barcode may be any code, but one that looks like a GTIN with a wrong check digit is flagged.
        if (!string.IsNullOrWhiteSpace(product.Gtin) && GtinCode.Inspect(product.Gtin) is { Kind: BarcodeKind.InvalidGtin or BarcodeKind.Custom } badGtin) Add(Blocking, "identity", "GTIN", "GTIN geçersiz: " + badGtin.Words);
        if (GtinCode.Inspect(product.Barcode) is { Kind: BarcodeKind.InvalidGtin } badBarcode) Add(Warning, "identity", "Barkod", "Barkod GTIN gibi görünüyor ama " + badBarcode.Words);
        // #907: a weight or box text with no canonical value beside it was ambiguous or unreadable when it was written.
        foreach (var unitFinding in ProductUnits.Findings(product)) Add(unitFinding.Blocking ? Blocking : Warning, "price-stock", unitFinding.Field, unitFinding.Message);
        // #908: the box stands or falls together and every side and the desi are positive; a typed desi that disagrees with the box is flagged, never overwritten.
        foreach (var dimensionFinding in ProductDimensions.Findings(product)) Add(dimensionFinding.Blocking ? Blocking : Warning, "price-stock", dimensionFinding.Field, dimensionFinding.Message);
        // #909: an unknown tax class is flagged (the price preview refuses it); a known class whose rate the record does not carry is flagged until the next save.
        foreach (var (taxSeverity, taxMessage) in ProductTaxClass.Findings(product)) Add(taxSeverity, "price-stock", "Vergi sınıfı", taxMessage);
        Length("content", "Marka", product.Brand, 200);
        Length("content", "Kategori", product.Category, 200);
        Length("content", "Açıklama", product.Description, 20000);
        if ((product.Currency ?? "").Length != 3) Add(Blocking, "price-stock", "Satış para birimi", "Para birimi üç harfli bir kod olmalı.");
        if (string.IsNullOrWhiteSpace(product.Name)) Add(Blocking, "content", "Başlık", "Ürün adı zorunlu.");
        if (string.IsNullOrWhiteSpace(product.Sku) && string.IsNullOrWhiteSpace(product.Barcode)) Add(Blocking, "identity", "SKU", "SKU veya barkoddan en az biri zorunlu.");
        if (product.Price < 0) Add(Blocking, "price-stock", "Satış fiyatı", "Satış fiyatı negatif olamaz.");
        if (product.Cost < 0) Add(Blocking, "price-stock", "Alış fiyatı", "Alış fiyatı negatif olamaz.");
        if (product.Stock < 0) Add(Blocking, "price-stock", "Stok", "Stok negatif olamaz.");
        if (product.VatRate < 0 || product.VatRate > 100) Add(Blocking, "price-stock", "KDV oranı", "KDV oranı 0 ile 100 arasında olmalı.");

        // --- Warnings: saveable, but not ready to list. Same gaps the quick-inspect readiness reports. ---
        if (product.Price <= 0) Add(Warning, "price-stock", "Satış fiyatı", "Satış fiyatı girilmemiş; kanala gönderilemez.");
        if (string.IsNullOrWhiteSpace(product.Description)) Add(Warning, "content", "Açıklama", "Açıklama boş; çoğu pazaryeri açıklama ister.");
        if (TitleNormalizer.Normalize(product.Name).Changed) Add(Warning, "content", "Başlık", "Başlıkta fazla boşluk veya görünmez karakter var; bir sonraki kayıtta düzeltilir.");
        if (string.IsNullOrWhiteSpace(product.ImageUrls)) Add(Warning, "media", "Görseller", "Görsel yok; ilan açılamaz.");
        if (product.Active && product.Stock == 0) Add(Warning, "price-stock", "Stok", "Ürün aktif ama stok sıfır.");

        // --- Info: worth knowing, never in the way. ---
        if (product.LockPrice || product.LockStock || product.LockName || product.LockDescription || product.LockImages)
            Add(Info, "identity", "Kilitler", "Bazı alanlar kilitli; içe aktarma bu alanları değiştirmez.");

        var order = ProductWorkspaceSections.All.Select((s, i) => (s.Key, i)).ToDictionary(x => x.Key, x => x.i, StringComparer.Ordinal);
        var severityRank = new Dictionary<string, int>(StringComparer.Ordinal) { [Blocking] = 0, [Warning] = 1, [Info] = 2 };
        var ordered = findings
            .OrderBy(f => severityRank.GetValueOrDefault(f.Severity, 3))
            .ThenBy(f => order.GetValueOrDefault(f.Section, int.MaxValue))
            .ToArray();
        return new(ordered);
    }

    /// <summary>The single blocking message a save should fail with, or null when the product is saveable.</summary>
    public static string? BlockingMessage(CatalogProduct product) => Evaluate(product).FirstBlocking?.Message;
}
