using System.Globalization;
using System.Reflection;
using System.Xml.Linq;

namespace TrMarketplaceHubDesktop.Catalog;

// Field.Formula, when set, is evaluated by the existing PriceFormula engine against the field's numeric value
// (e.g. "x*1.2" to export price marked up 20%); no new formula engine is written.
public sealed record XmlExportField(string Property, string Tag, string? Formula = null);
public sealed record XmlExportTemplate(string Name, string RootTag, string ItemTag, IReadOnlyList<XmlExportField> Fields);
public sealed record XmlExportFilter(string? Category = null, string? Brand = null, string? SourceId = null);

public static class XmlCatalogExporter
{
    public static readonly XmlExportTemplate StandardTemplate = new("standard", "Products", "Product", new[]
    {
        new XmlExportField(nameof(CatalogProduct.Sku), "Sku"),
        new XmlExportField(nameof(CatalogProduct.Barcode), "Barcode"),
        new XmlExportField(nameof(CatalogProduct.Name), "Name"),
        new XmlExportField(nameof(CatalogProduct.Description), "Description"),
        new XmlExportField(nameof(CatalogProduct.Brand), "Brand"),
        new XmlExportField(nameof(CatalogProduct.Category), "Category"),
        new XmlExportField(nameof(CatalogProduct.Cost), "Cost"),
        new XmlExportField(nameof(CatalogProduct.Stock), "Stock"),
    });

    static readonly IReadOnlyDictionary<string, PropertyInfo> Properties =
        typeof(CatalogProduct).GetProperties().ToDictionary(p => p.Name, p => p);

    public static IReadOnlyList<CatalogProduct> Filter(IEnumerable<CatalogProduct> products, XmlExportFilter filter) => products.Where(p =>
        (filter.Category is null || p.Category.Equals(filter.Category, StringComparison.OrdinalIgnoreCase)) &&
        (filter.Brand is null || p.Brand.Equals(filter.Brand, StringComparison.OrdinalIgnoreCase)) &&
        (filter.SourceId is null || p.SourceId == filter.SourceId)).ToList();

    public static string Export(IEnumerable<CatalogProduct> products, XmlExportTemplate template, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(products);
        ArgumentNullException.ThrowIfNull(template);
        if (template.Fields.Count == 0) throw new ArgumentException("Export şablonu en az bir alan eşlemesi içermeli.");
        if (string.IsNullOrWhiteSpace(template.RootTag) || string.IsNullOrWhiteSpace(template.ItemTag)) throw new ArgumentException("Export şablonu kök ve ürün etiketi gerektirir.");
        foreach (var field in template.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Tag)) throw new ArgumentException("Her alan eşlemesi bir XML etiket adı gerektirir.");
            if (!Properties.ContainsKey(field.Property)) throw new ArgumentException($"'{field.Property}' CatalogProduct üzerinde tanımlı bir alan değil.");
            if (field.Formula is not null) _ = PriceFormula.Compile(field.Formula); // fail fast on a bad formula before writing any product
        }

        var root = new XElement(template.RootTag);
        foreach (var product in products)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = new XElement(template.ItemTag);
            foreach (var field in template.Fields)
            {
                var raw = Properties[field.Property].GetValue(product);
                string text;
                if (field.Formula is not null)
                {
                    var numeric = raw switch { decimal d => d, int i => i, _ => throw new InvalidOperationException($"'{field.Property}' formülle işlenemez; sayısal bir alan olmalı.") };
                    text = PriceFormula.Evaluate(field.Formula, numeric).ToString(CultureInfo.InvariantCulture);
                }
                else text = Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "";
                item.Add(new XElement(field.Tag, text));
            }
            root.Add(item);
        }
        return new XDocument(root).ToString(SaveOptions.DisableFormatting);
    }

    // Runs the (CPU-bound, potentially large) export off the calling thread so a UI caller can await it without blocking.
    public static Task<string> ExportAsync(IEnumerable<CatalogProduct> products, XmlExportTemplate template, CancellationToken cancellationToken = default)
        => Task.Run(() => Export(products, template, cancellationToken), cancellationToken);

    // Symmetric read-back source for the standard template's own tag names, used for round-trip verification;
    // reuses the existing import pipeline (XmlCatalog.Preview) rather than a bespoke reader.
    public static XmlSource StandardRoundTripSource(string id) => new()
    {
        Id = id,
        ItemPath = $"{StandardTemplate.RootTag}/{StandardTemplate.ItemTag}",
        Fields = new Dictionary<string, string> { ["Name"] = "Name", ["Sku"] = "Sku", ["Barcode"] = "Barcode", ["Cost"] = "Cost", ["Stock"] = "Stock", ["Description"] = "Description", ["Brand"] = "Brand", ["Category"] = "Category" },
        PriceMode = "Simple", Formula = "x", SafetyStock = 0, MinimumStock = 0, MaximumStock = int.MaxValue, Currency = "USD"
    };
}
