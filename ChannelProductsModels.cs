namespace TrMarketplaceHubDesktop;
public sealed class ChannelProductPlan
{
 public string ChannelId {get;set;}="";
 public string ShopId {get;set;}="";
 public string ProductId {get;set;}="";
 public string ListingId {get;set;}="";
 public string ListingUrl {get;set;}="";
 public string TargetCategory {get;set;}="";
 public decimal PlannedPrice {get;set;}
 public string Currency {get;set;}="USD";
 public int PlannedStock {get;set;}
 public string Notes {get;set;}="";
 public DateTime UpdatedUtc {get;set;}
 // #918: the taxonomy snapshot (scope and version) this plan was validated with on its last save; 0 when never stamped.
 public long TaxonomySnapshotVersion {get;set;} public string TaxonomySnapshotScope {get;set;}="";
 public string Provenance => "Yerel taslak";
}
