namespace TrMarketplaceHubDesktop.Catalog;

public sealed record XmlFieldDefinition(string Key, string Label, bool IsImage = false, bool IsAttribute = false);

public static class XmlFieldDefinitions
{
    public static readonly XmlFieldDefinition[] All =
    [
        new("SourceProductId", "XML ürün ID"), new("Sku", "Ürün kodu / SKU"), new("Name", "Ürün adı"),
        new("Stock", "Ürün adedi"), new("Cost", "Alış fiyatı"), new("CostCurrency", "Para birimi"),
        new("VatRate", "KDV oranı"), new("Description", "Açıklama"), new("InvoiceName", "Fatura adı"),
        new("Subtitle", "Alt başlık 1"), new("Subtitle2", "Alt başlık 2", IsAttribute:true), new("Description2", "Ek açıklama 2", IsAttribute:true), new("Description3", "Ek açıklama 3", IsAttribute:true), new("Shelf", "Raf numarası"),
        new("Brand", "Marka"), new("BrandId", "Marka ID", IsAttribute:true), new("Category", "Kategori 1 / ağaç"),
        new("Category2", "Kategori 2"), new("Category3", "Kategori 3"), new("Category4", "Kategori 4"), new("Category5", "Kategori 5"),
        new("CategoryId1", "Kategori ID 1", IsAttribute:true), new("CategoryId2", "Kategori ID 2", IsAttribute:true),
        new("CategoryId3", "Kategori ID 3", IsAttribute:true), new("CategoryId4", "Kategori ID 4", IsAttribute:true), new("CategoryId5", "Kategori ID 5", IsAttribute:true),
        new("Barcode", "Barkod"), new("Gtin", "GTIN / EAN / UPC"), new("Mpn", "MPN / ASIN"),
        new("Desi", "Desi", IsAttribute:true), new("Weight", "Ağırlık", IsAttribute:true), new("Width", "En", IsAttribute:true),
        new("Height", "Boy", IsAttribute:true), new("Depth", "Derinlik", IsAttribute:true), new("Origin", "Menşei", IsAttribute:true),
        new("Gtip", "GTİP", IsAttribute:true), new("ExpiresOn", "Miad"), new("PackageType", "Kargo paket tipi", IsAttribute:true),
        new("Image1", "Görsel 1", true), new("Image2", "Görsel 2", true), new("Image3", "Görsel 3", true),
        new("Image4", "Görsel 4", true), new("Image5", "Görsel 5", true), new("Image6", "Görsel 6", true),
        new("Image7", "Görsel 7", true), new("Image8", "Görsel 8", true), new("Image9", "Görsel 9", true),
        new("ImageUrls", "Görsel listesi", true)
    ];
}
