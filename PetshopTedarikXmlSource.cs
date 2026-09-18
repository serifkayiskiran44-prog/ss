using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public static class PetshopTedarikXmlSource
{
    public const string Location = "https://www.petshoptedarik.com.tr/marketxml/files/amazon/genel.xml";
    public static readonly string[] AvailableFields = ["sirano", "ad", "satisAd", "link", "aciklama", "fiyat", "satisfiyat", "kdv", "tamyol", "satistamyol", "marka", "satisMarka", "ozellikler", "stok", "satisstok", "resim1", "resim2", "resim3", "resim4", "resim5", "resim6", "resim7", "asin", "ean", "tarih"];

    public static void Ensure(CatalogStore store)
    {
        var existing = store.Sources().FirstOrDefault(source => string.Equals(source.Location, Location, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            // A preset initializes a new source only. User choices always survive restart.
            return;
        }
        store.SaveSource(new XmlSource
        {
            Id = "petshoptedarik-amazon",
            Name = "PetshopTedarik Amazon XML",
            Location = Location,
            ItemPath = "/ArrayOfModel/Model",
            SkuPrefix = "PTD-",
            Currency = "TRY",
            CostCurrency = "TRY",
            PriceMode = "Simple",
            ExchangeRate = 1,
            MarkupPercent = 40,
            SafetyStock = 3,
            MinimumStock = 0,
            MaximumStock = 20,
            IntervalMinutes = 150,
            AutoImport = false,
            Fields = new Dictionary<string, string>
            {
                ["Sku"] = "sirano",
                ["Barcode"] = "",
                ["Gtin"] = "ean",
                ["Mpn"] = "asin",
                ["Name"] = "ad",
                ["Description"] = "aciklama",
                ["Cost"] = "fiyat",
                ["VatRate"] = "kdv",
                ["Stock"] = "stok",
                ["Brand"] = "marka",
                ["Category"] = "tamyol",
                ["Image1"] = "resim1", ["Image2"] = "resim2", ["Image3"] = "resim3", ["Image4"] = "resim4",
                ["Image5"] = "resim5", ["Image6"] = "resim6", ["Image7"] = "resim7"
            }
        });
    }
}
