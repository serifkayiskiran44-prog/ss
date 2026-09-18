using System.Text.Json;

namespace TrMarketplaceHubDesktop.Catalog;

/// <summary>Applies an Excel row selectively, keeping XML-protected product fields intact.</summary>
public static class ExcelFieldUpdatePolicy
{
    public static CatalogProduct Merge(CatalogProduct current, CatalogProduct excel, IReadOnlyCollection<string> fields)
    {
        var selected = new HashSet<string>(fields, StringComparer.OrdinalIgnoreCase);
        var result = JsonSerializer.Deserialize<CatalogProduct>(JsonSerializer.Serialize(current))!;
        bool Has(string key) => selected.Contains(key);
        if (Has("Name") && !current.LockName) result.Name = excel.Name;
        if (Has("Description") && !current.LockDescription) result.Description = excel.Description;
        if (Has("ImageUrls") && !current.LockImages) result.ImageUrls = excel.ImageUrls;
        if (Has("Price") && !current.LockPrice) result.Price = excel.Price;
        if (Has("Cost") && !current.LockPrice) result.Cost = excel.Cost;
        if (Has("Currency") && !current.LockPrice) result.Currency = excel.Currency;
        if (Has("VatRate") && !current.LockPrice) result.VatRate = excel.VatRate;
        if (Has("Stock") && !current.LockStock) result.Stock = excel.Stock;
        if (Has("Brand")) result.Brand = excel.Brand;
        if (Has("Category")) result.Category = excel.Category;
        if (Has("Active")) result.Active = excel.Active;
        if (Has("Gtin")) result.Gtin = excel.Gtin;
        if (Has("Mpn")) result.Mpn = excel.Mpn;
        if (Has("InvoiceName")) result.InvoiceName = excel.InvoiceName;
        if (Has("Subtitle")) result.Subtitle = excel.Subtitle;
        if (Has("Shelf")) result.Shelf = excel.Shelf;
        return result;
    }
}
