using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace TrMarketplaceHubDesktop.Catalog;

/// Product-to-XML export (distinct from the supplier XML *import* owner in
/// XmlCatalog.cs). Field identity/order/version is deterministic and independent
/// of a display label change; output is always well-formed UTF-8 XML with a
/// matching declaration. No variant/bundle scope is written.
public static class CatalogXmlExport
{
    /// Bump only when the exported element/attribute set or order changes.
    public const int SchemaVersion = 1;

    static readonly (string Key, Func<CatalogProduct, string> Value)[] Columns =
    [
        ("Sku", p => p.Sku), ("Barcode", p => p.Barcode), ("Name", p => p.Name), ("Brand", p => p.Brand), ("Category", p => p.Category),
        ("Description", p => p.Description), ("Cost", p => p.Cost.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ("Price", p => p.Price.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("Currency", p => p.Currency),
        ("VatRate", p => p.VatRate.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("Stock", p => p.Stock.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ("Active", p => p.Active ? "true" : "false"), ("Gtin", p => p.Gtin), ("ImageUrls", p => p.ImageUrls),
    ];
    public static IReadOnlyList<string> ExportElementKeys => Columns.Select(x => x.Key).ToList();

    public static void Export(string path, IReadOnlyList<CatalogProduct> products) => Export(path, products, null);
    public static void Export(string path, IReadOnlyList<CatalogProduct> products, IReadOnlyCollection<string>? visibleFields)
    {
        var columns = visibleFields is null || visibleFields.Count == 0 ? Columns : Columns.Where(x => visibleFields.Contains(x.Key, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (columns.Length == 0) throw new InvalidOperationException("Dışa aktarım için en az bir alan seçin.");

        // Deterministic order regardless of the caller-supplied/store iteration order.
        var ordered = products.OrderBy(p => p.Sku, StringComparer.Ordinal).ThenBy(p => p.Barcode, StringComparer.Ordinal).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();

        var root = new XElement("Products", new XAttribute("SchemaVersion", SchemaVersion));
        foreach (var product in ordered)
        {
            var element = new XElement("Product");
            foreach (var (key, value) in columns)
            {
                string text;
                try { text = value(product); }
                catch (Exception ex) { throw new InvalidOperationException($"Ürün {product.Sku} alanı '{key}' dışa aktarılamadı: {ex.Message}", ex); }
                try { XmlConvert.VerifyXmlChars(text); }
                catch (XmlException ex) { throw new InvalidOperationException($"Ürün {product.Sku} alanı '{key}' geçerli XML metni içermiyor (kontrol karakteri).", ex); }
                element.Add(new XElement(key, text));
            }
            root.Add(element);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $"{Path.GetFileNameWithoutExtension(path)}.tmp-{Guid.NewGuid():N}{Path.GetExtension(path)}");
        try
        {
            var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, OmitXmlDeclaration = false };
            using (var writer = XmlWriter.Create(temporary, settings)) new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(writer);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
