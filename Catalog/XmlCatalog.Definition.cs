using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Xml.XPath;

namespace TrMarketplaceHubDesktop.Catalog;

public static partial class XmlCatalog
{
    static readonly Regex RelativeFieldPath = new(@"^@?[\w-]+(?:/(?:@?[\w-]+))*$", RegexOptions.CultureInvariant);
    static bool IsImageKey(string key) => key is "ImageUrls" or "Image" or "Images" || Regex.IsMatch(key, @"^Image[1-9]$");

    public static List<CatalogProduct> Preview(string xml, XmlSource source, CatalogStore catalog)
    {
        var persisted = catalog.Sources().SingleOrDefault(candidate => string.Equals(candidate.Id, source.Id, StringComparison.Ordinal));
        if (persisted is null) throw new InvalidOperationException("XML kaynağı silinmiş; yenileme önizlemesi üretilemez.");
        if (!source.Enabled || !persisted.Enabled) throw new InvalidOperationException("XML kaynağı pasif; yenileme önizlemesi üretilemez.");
        return PreviewMapped(xml, source);
    }

    public static string SampleValue(IReadOnlyDictionary<string, string> values, string paths) =>
        string.Join(" | ", paths.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(p => values.GetValueOrDefault(p, "")).Where(v => v.Length > 0));

    public static IReadOnlyList<IReadOnlyDictionary<string, string>> FieldSamples(string xml, string itemPath, int limit = 10)
    {
        if (limit is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(limit));
        var items = Document(xml).XPathSelectElements(itemPath).Take(limit).ToList();
        if (items.Count == 0) throw new InvalidOperationException("Seçilen ürün yolunda örnek ürün yok.");
        return items.Select(item =>
        {
            var values = new Dictionary<string, string>();
            foreach (var element in item.DescendantsAndSelf())
            {
                var path = string.Join("/", element.AncestorsAndSelf().TakeWhile(e => e != item).Reverse().Select(e => e.Name.LocalName));
                void Add(string key, string value)
                {
                    if (key.Length == 0 || values.Count >= 512) return;
                    value = value.Length > 4000 ? value[..4000] : value;
                    if (!values.TryGetValue(key, out var previous) || previous.Length == 0) values[key] = value;
                }
                if (!element.HasElements) Add(path, element.Value.Trim());
                foreach (var attribute in element.Attributes()) Add((path.Length == 0 ? "" : path + "/") + "@" + attribute.Name.LocalName, attribute.Value.Trim());
            }
            return (IReadOnlyDictionary<string, string>)values;
        }).ToList();
    }

    static void ValidateDefinition(XmlSource source)
    {
        if (source.DefaultVatRate is < 0 or > 100 || source.FixedStock < 0 || source.AvailableStockQuantity < 0 || source.StockDecimalSeparator is not ("." or ","))
            throw new InvalidOperationException("Varsayılan KDV, sabit stok veya stok ayracı geçersiz.");
        if (source.StockIsText && string.IsNullOrWhiteSpace(source.AvailableStockText)) throw new InvalidOperationException("Stok var kabul edilecek metni yazın.");
        if (new[] { source.DefaultCategory, source.FixedCategory, source.DefaultBrand, source.FixedBrand }.Any(v => v.Length > 200)) throw new InvalidOperationException("Kategori ve marka en fazla 200 karakter olabilir.");
    }

    static List<CatalogProduct> PreviewMapped(string xml, XmlSource source)
    {
        if (!source.Enabled) throw new InvalidOperationException("XML kaynağı pasif; yenileme önizlemesi üretilemez.");
        Validate(source);
        var calculate = CatalogPricing.Create(source);
        var applyCategory = XmlCategoryRules.Create(source);
        bool Has(string key) => source.Fields.TryGetValue(key, out var path) && !string.IsNullOrWhiteSpace(path);
        if (string.IsNullOrWhiteSpace(source.ItemPath) || !Has("Name") || !Has("Cost") || (!Has("Stock") && !source.UseFixedStock) || !(Has("Sku") || Has("Barcode")))
            throw new InvalidOperationException("Ürün yolu, ad, fiyat, stok (veya sabit stok) ve SKU veya barkod eşleştirmesi gerekli.");
        IEnumerable<string> Paths(string key) => IsImageKey(key) ? source.Fields[key].Split('|', StringSplitOptions.TrimEntries) : [source.Fields[key]];
        foreach (var path in source.Fields.Keys.Where(Has).SelectMany(Paths))
            if (!RelativeFieldPath.IsMatch(path)) throw new InvalidOperationException("Alanlar göreli XML yolları olmalı.");
        var rows = new List<CatalogProduct>();
        var seenSku = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenBar = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenId = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var item in Document(xml).XPathSelectElements(source.ItemPath))
        {
            if (++count > 100000) throw new InvalidOperationException("100.000 ürün sınırı aşıldı.");
            string Field(string key) => !Has(key) ? "" : string.Join(" | ", Paths(key).SelectMany(path => ((IEnumerable)item.XPathEvaluate(path)).Cast<object>()).Select(value => value is XElement element ? element.Value : ((XAttribute)value).Value).Where(v => !string.IsNullOrWhiteSpace(v))).Trim();
            decimal Number(string key, string separator)
            {
                var raw = Field(key);
                if (!Regex.IsMatch(raw, @"^\d+(?:" + Regex.Escape(separator) + @"\d+)?$") || !decimal.TryParse(raw.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
                    throw new InvalidOperationException($"Satır {count}: {key} geçersiz sayı.");
                return value;
            }
            var vat = Field("VatRate").Length == 0 ? source.DefaultVatRate : Number("VatRate", source.DecimalSeparator);
            if (vat > 100) throw new InvalidOperationException($"Satır {count}: KDV oranı 0–100 arasında olmalı.");
            var cost = Number("Cost", source.DecimalSeparator);
            if (!source.PriceIncludesVat) cost = checked(cost * (1 + vat / 100));
            var currency = Field("CostCurrency").ToUpperInvariant();
            if (currency is "TL" or "TRL") currency = "TRY";
            if (currency.Length == 0) currency = source.CostCurrency;
            if (!TrMarketplaceHubDesktop.LocaleSettings.SupportedCurrencies.Contains(currency)) throw new InvalidOperationException($"Satır {count}: XML para birimi desteklenmiyor.");
            if (source.PriceMode == "Formula" && currency != "TRY") throw new InvalidOperationException($"Satır {count}: formül TL alış fiyatı bekler, XML para birimi {currency}.");
            decimal stock = source.UseFixedStock ? source.FixedStock : source.StockIsText ? (string.Equals(Field("Stock"), source.AvailableStockText.Trim(), StringComparison.OrdinalIgnoreCase) ? source.AvailableStockQuantity : 0) : Number("Stock", source.StockDecimalSeparator);
            if (stock > int.MaxValue || stock != decimal.Truncate(stock)) throw new InvalidOperationException("Stok tam sayı olmalı.");
            var category = string.Join(" > ", new[] { "Category", "Category2", "Category3", "Category4", "Category5" }.Select(Field).Where(v => v.Length > 0));
            var brand = source.FixedBrand.Length > 0 ? source.FixedBrand : Field("Brand");
            if (brand.Length == 0) brand = source.DefaultBrand;
            var images = source.Fields.Keys.Where(IsImageKey).Select(Field).SelectMany(v => v.Split(" | ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.Ordinal);
            var row = new CatalogProduct
            {
                SourceId = source.Id, SourceProductId = Field("SourceProductId"), SourceKind = "xml", PriceSource = "xml", StockSource = "xml", MediaSource = "xml", SourceUpdatedUtc = DateTime.UtcNow,
                Sku = Field("Sku").Length == 0 ? "" : source.SkuPrefix + Field("Sku"), Barcode = Field("Barcode"), Name = Field("Name"), Description = Field("Description"),
                Gtin = Field("Gtin"), Mpn = Field("Mpn"), InvoiceName = Field("InvoiceName"), Subtitle = Field("Subtitle"), Shelf = Field("Shelf"),
                Cost = cost, VatRate = vat, CostCurrency = currency, Currency = source.Currency, Brand = brand, Category = category, ImageUrls = string.Join(" | ", images)
            };
            var expiry = Field("ExpiresOn");
            if (expiry.Length > 0)
            {
                if (!DateTime.TryParse(expiry, CultureInfo.GetCultureInfo("tr-TR"), DateTimeStyles.None, out var date)) throw new InvalidOperationException($"Satır {count}: miad tarihi geçersiz.");
                row.ExpiresOn = date.Date;
            }
            foreach (var definition in XmlFieldDefinitions.All.Where(d => d.IsAttribute && Has(d.Key))) row.XmlAttributes[definition.Key] = Field(definition.Key);
            if (row.Name == "" || (row.Sku == "" && row.Barcode == "")) throw new InvalidOperationException("Ad ve kimlik boş olamaz.");
            if ((row.Sku != "" && !seenSku.Add(row.Sku)) || (row.Barcode != "" && !seenBar.Add(row.Barcode))) throw new InvalidOperationException("XML içinde yinelenen SKU veya barkod.");
            if (Has("SourceProductId") && (row.SourceProductId.Length == 0 || !seenId.Add(row.SourceProductId))) throw new InvalidOperationException("XML ürün ID alanı boş veya yineleniyor.");
            var price = calculate(cost);
            row.Price = price.FinalPrice; row.FormulaPriceTry = price.SourcePrice;
            row.AppliedTryRate = source.PriceMode == "Formula" ? (source.Currency == "TRY" ? 1 : source.TryPerTargetUnit) : null;
            row.FxRateDate = source.PriceMode == "Formula" && source.AutoFx ? source.FxRateDate : null;
            row.FxFetchedUtc = source.PriceMode == "Formula" && source.AutoFx ? source.FxFetchedUtc : null;
            row.Stock = source.UseFixedStock ? source.FixedStock : stock <= source.SafetyStock || stock < source.MinimumStock ? 0 : Math.Min(source.MaximumStock, (int)stock - source.SafetyStock);
            bool Included(string value, string filter) => string.IsNullOrWhiteSpace(filter) || filter.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains(value, StringComparer.OrdinalIgnoreCase);
            if (!Included(row.Brand, source.BrandFilter) || !Included(row.Category, source.CategoryFilter)) continue;
            applyCategory(row);
            if (source.FixedCategory.Length > 0) row.Category = source.FixedCategory;
            else if (row.Category.Length == 0) row.Category = source.DefaultCategory;
            rows.Add(row);
        }
        if (count == 0) throw new InvalidOperationException("Ürün bulunamadı; katalog değiştirilmedi.");
        return rows;
    }
}
