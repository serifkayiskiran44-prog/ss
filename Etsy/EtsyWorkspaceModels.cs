namespace TrMarketplaceHubDesktop.Etsy;

public sealed class EtsyWorkspaceState
{
    public string ShopId { get; set; } = "";
    public string ShopName { get; set; } = "";
    public string Currency { get; set; } = "";
    public long Revision { get; set; }
    public DateTime? LastRefreshUtc { get; set; }
    public List<EtsyListing> Listings { get; set; } = [];
    public List<EtsyTaxonomyNode> Categories { get; set; } = [];
    public List<EtsyShippingProfile> ShippingProfiles { get; set; } = [];
    public List<EtsyProcessingProfile> ProcessingProfiles { get; set; } = [];
    public List<EtsyShopSection> Sections { get; set; } = [];
    public List<EtsyCategoryMapping> CategoryMappings { get; set; } = [];
    public List<EtsyWorkspaceTemplate> Templates { get; set; } = [];
    public List<EtsyProductProfile> Profiles { get; set; } = [];
}
public sealed class EtsyCategoryMapping { public string LocalCategory { get; set; } = ""; public long TaxonomyId { get; set; } }
public sealed class EtsyWorkspaceTemplate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public EtsyListingTemplate Listing { get; set; } = new() { Currency = "" };
    public long? ShopSectionId { get; set; }
}
public sealed class EtsyProductProfile
{
    public string ProductId { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Tags { get; set; } = "";
    public string Materials { get; set; } = "";
    public long? ListingId { get; set; }
    public long? TaxonomyId { get; set; }
    public long? ShippingProfileId { get; set; }
    public long? ReadinessStateId { get; set; }
    public long? ShopSectionId { get; set; }
    public decimal? Price { get; set; }
    public string PriceCurrency { get; set; } = "";
    public List<EtsyProductProperty> Properties { get; set; } = [];
}
public sealed class EtsyProductProperty
{
    public long PropertyId { get; set; }
    public long? ScaleId { get; set; }
    public long[] ValueIds { get; set; } = [];
    public string[] Values { get; set; } = [];
}
public enum EtsyOperation { CreateDraft, Price, Stock, PriceAndStock, Content, Publish, Deactivate }
public sealed class EtsyPreviewRow
{
    public string ProductId { get; set; } = "";
    public string Sku { get; set; } = "";
    public string Title { get; set; } = "";
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
    public string PayloadJson { get; set; } = "";
    public long? ListingId { get; set; }
    public bool CanSend { get; set; }
    public string RemoteFingerprint { get; set; } = "";
    public string InventoryFingerprint { get; set; } = "";
    public List<EtsyWriteStep> Steps { get; set; } = [];
}
public sealed class EtsyWriteStep
{
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public string Json { get; set; } = "";
    public Dictionary<string,string> Form { get; set; } = [];
    public byte[]? Image { get; set; }
}
public sealed class EtsyOperationPlan
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ShopId { get; set; } = "";
    public EtsyOperation Operation { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public IReadOnlyList<EtsyPreviewRow> Rows { get; set; } = [];
    public long WorkspaceRevision { get; set; }
    public string CatalogFingerprint { get; set; } = "";
    public string AccountFingerprint { get; set; } = "";
    public string ShopCurrency { get; set; } = "";
}
public sealed class EtsyOperationReceipt
{
    public string PlanId { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string Sku { get; set; } = "";
    public string Status { get; set; } = "";
    public string Detail { get; set; } = "";
    public long? ListingId { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
public sealed class EtsyMatchRow
{
    public string ProductId { get; set; } = "";
    public string Sku { get; set; } = "";
    public string Title { get; set; } = "";
    public string Status { get; set; } = "";
    public string Detail { get; set; } = "";
    public long? ListingId { get; set; }
    public bool CanMatch { get; set; }
}
