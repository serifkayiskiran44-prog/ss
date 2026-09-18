using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop.Trendyol;

public sealed record TrendyolTaxonomySuggestion(long? RemoteId, string Name, string Reason);
public sealed record TrendyolProductSuggestion(string? Barcode, string Reason);
public static class TrendyolMatching
{
    public static string Normalize(string value) => Regex.Replace(value.Normalize(NormalizationForm.FormKC).Trim(), @"\s+", " ")
        .ToUpper(new CultureInfo("tr-TR"));
    static string PathKey(string value) => string.Join(">", value.Split('>').Select(Normalize));
    public static TrendyolTaxonomySuggestion Category(string name, IEnumerable<TrendyolCategory> categories)
    {
        var leaves = categories.Where(c => c.IsLeaf).ToArray();
        var exact = leaves.Where(c => PathKey(c.Path) == PathKey(name)).ToArray();
        var candidates = exact.Length > 0 ? exact : leaves.Where(c => Normalize(c.Name) == Normalize(name.Split('>').Last())).ToArray();
        return candidates.Length == 1 ? new(candidates[0].Id, candidates[0].Path, "Tek kesin ad/yol karşılığı; onay bekliyor")
            : new(null, "", candidates.Length > 1 ? "Birden fazla kategori; elle seçin" : "Karşılık bulunamadı; elle seçin");
    }
    public static TrendyolTaxonomySuggestion Brand(string name, IEnumerable<TrendyolBrand> brands)
    {
        var matches = brands.Where(b => Normalize(b.Name) == Normalize(name)).ToArray();
        return matches.Length == 1 ? new(matches[0].Id, matches[0].Name, "Tek kesin ad karşılığı; onay bekliyor")
            : new(null, "", matches.Length > 1 ? "Birden fazla marka; elle seçin" : "Karşılık bulunamadı; elle seçin");
    }
    public static TrendyolProductSuggestion Product(CatalogProduct product, string integrationCode, IEnumerable<TrendyolRemoteProduct> remote)
    {
        var rows = remote.ToArray();
        var matches = integrationCode.Length > 0 ? rows.Where(p => p.Barcode == integrationCode).ToArray()
            : rows.Where(p => (product.Barcode.Length > 0 && p.Barcode == product.Barcode) ||
                (product.Gtin.Length > 0 && p.Barcode == product.Gtin) ||
                (product.Sku.Length > 0 && p.StockCode.Equals(product.Sku, StringComparison.OrdinalIgnoreCase))).ToArray();
        return matches.Length == 1 ? new(matches[0].Barcode, integrationCode.Length>0?"Kayıtlı entegrasyon barkodu":matches[0].Barcode==product.Gtin?"GTIN → Trendyol barkodu":matches[0].Barcode==product.Barcode?"Barkod karşılığı":"Stok kodu / SKU karşılığı")
            : new(null, matches.Length == 0 ? "SKU, barkod ve GTIN karşılığı yok; mağazada adla arayıp elle seçin" : "SKU / barkod / GTIN farklı ürünleri gösteriyor; elle seçin");
    }
}
