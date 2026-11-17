using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop.Hepsiburada;

public static class HepsiburadaMatching
{
    public static HepsiburadaProductMatch Suggest(CatalogProduct product, IReadOnlyList<HepsiburadaMerchantProduct> remote)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(remote);
        var barcode = product.Barcode?.Trim() ?? "";
        var sku = product.Sku?.Trim() ?? "";
        var byBarcode = barcode.Length == 0 ? [] : remote.Where(row => string.Equals(row.Barcode, barcode, StringComparison.Ordinal)).ToArray();
        if (byBarcode.Length == 1) return Matched(product.Id, byBarcode[0], "Barkod", "Tam barkod eşleşmesi.");
        if (byBarcode.Length > 1) return Conflict(product.Id, "Aynı barkod birden fazla uzaktaki üründe var.");

        var bySku = sku.Length == 0 ? [] : remote.Where(row => string.Equals(row.MerchantSku, sku, StringComparison.Ordinal)).ToArray();
        if (bySku.Length == 1)
        {
            var row = bySku[0];
            if (row.Barcode.Length == 0 || barcode.Length == 0 || string.Equals(row.Barcode, barcode, StringComparison.Ordinal))
                return Matched(product.Id, row, "Merchant SKU", "Çelişkisiz Merchant SKU eşleşmesi.");
            return Conflict(product.Id, "SKU eşleşiyor ancak barkod çelişiyor.");
        }
        if (bySku.Length > 1) return Conflict(product.Id, "Merchant SKU birden fazla uzaktaki üründe var.");
        return new(product.Id, "", "", "", ProductChannelMatchOutcome.NewListingCandidate, "", "Eşleşme bulunamadı; yeni ürün adayı.");
    }

    static HepsiburadaProductMatch Matched(string productId, HepsiburadaMerchantProduct row, string method, string detail) =>
        new(productId, row.HepsiburadaSku, row.MerchantSku, row.Barcode, ProductChannelMatchOutcome.Matched, method, detail);

    static HepsiburadaProductMatch Conflict(string productId, string detail) =>
        new(productId, "", "", "", ProductChannelMatchOutcome.Conflict, "", detail);
}
