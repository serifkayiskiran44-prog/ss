using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop.Trendyol;

public sealed class TrendyolWorkspaceState
{
    public string SellerId { get; set; } = "";
    public long Revision { get; set; }
    public DateTime? DictionaryUpdatedUtc { get; set; }
    public DateTime? ProductsUpdatedUtc { get; set; }
    public DateTime? AddressesUpdatedUtc { get; set; }
    public List<TrendyolCategory> Categories { get; set; } = [];
    public List<TrendyolBrand> Brands { get; set; } = [];
    public List<TrendyolRemoteProduct> Products { get; set; } = [];
    public List<TrendyolAddress> Addresses { get; set; } = [];
    public List<TrendyolCarrier> Carriers { get; set; } = [];
    public Dictionary<long, List<TrendyolAttribute>> Attributes { get; set; } = [];
    public Dictionary<long, DateTime> AttributesUpdatedUtc { get; set; } = [];
    public List<TrendyolOutboundMapping> Mappings { get; set; } = [];
    public List<TrendyolProductProfile> Profiles { get; set; } = [];
    public List<TrendyolDeliveryTemplate> Templates { get; set; } = [];
    public List<TrendyolBrandSafetyTemplate> BrandSafetyTemplates { get; set; } = [];
}

public sealed record TrendyolOutboundMapping(TaxonomyKind Kind, string LocalId, string LocalName, long RemoteId);
public sealed record TrendyolAttributeSelection(long AttributeId, long[] ValueIds, string CustomValue = "");
public sealed class TrendyolProductProfile
{
    public string ProductId { get; set; } = "";
    public string IntegrationCode { get; set; } = "";
    // User-selected barcode for new listings; a legacy remote link is not a barcode source.
    public string ListingBarcode { get; set; } = "";
    public long? CategoryId { get; set; }
    public long? BrandId { get; set; }
    public string Origin { get; set; } = "";
    public string ModelCode { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal? SalePriceTry { get; set; }
    public decimal? ListPriceTry { get; set; }
    public string DeliveryTemplateId { get; set; } = "";
    public List<TrendyolAttributeSelection> Attributes { get; set; } = [];
    public decimal? CompetitionMinimum { get; set; }
    public decimal? CompetitionMaximum { get; set; }
    public decimal CompetitionDifference { get; set; } = 0.01m;
}
public sealed record TrendyolDeliveryTemplate(string Id, string Name, string CarrierCode, int? DurationDays, long? ShipmentAddressId, long? ReturningAddressId)
{
    public bool IncludeProductDesi { get; init; } = true;
}
public enum TrendyolOperation { Create, Price, Stock, PriceAndStock, Delivery, ShippingDetails, UpdateUnapproved, Content }
public sealed record TrendyolPreviewRow(string ProductId, string Sku, string Name, string Barcode, string Status, string Detail, string? ItemJson);
public sealed record TrendyolPlan(string Id, string SellerId, string AccountFingerprint, long Revision, DateTime CreatedUtc,
    TrendyolOperation Operation, string CatalogFingerprint, IReadOnlyList<TrendyolPreviewRow> Rows, string PayloadJson);
public sealed record TrendyolReceipt(string PlanId, string SellerId, DateTime CreatedUtc, string Operation, string Status, string BatchId, string Detail);
