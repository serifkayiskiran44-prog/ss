namespace TrMarketplaceHubDesktop.Catalog;
public class XmlSource
{
 public string PriceMode {get;set;}="Simple";
 public string Formula {get;set;}="";
 public string CostCurrency {get;set;}="TRY";
 public bool AutoFx {get;set;}
 public decimal TryPerTargetUnit {get;set;}
 public string FxKind {get;set;}="ForexSelling";
 public DateTime? FxRateDate {get;set;}
 public DateTimeOffset? FxFetchedUtc {get;set;}
 public bool AutoImport {get;set;} public DateTime? LastRunUtc {get;set;} public string LastStatus {get;set;}="Henüz çalışmadı";
 // Mapping identity is persisted with the source so a scheduled run cannot silently
 // apply a feed after its XML shape or mapping has changed.
 public int MappingRevision { get; set; } = 1;
 public int LastAppliedMappingRevision { get; set; }
 public string LastMappingShapeFingerprint { get; set; } = "";
 public string LastSuccessfulFeedHash { get; set; } = "";
 public DateTime? LastSuccessfulFeedUtc { get; set; }
 public int LastSuccessfulFeedCount { get; set; }
 public string LastFeedState { get; set; } = "NEVER";
 public int MissingSourceGraceMinutes { get; set; } = 120;
 public string NumberCultureName { get; set; } = "en-US";
 // Reachability/latency health, distinct from LastFeedState (which tracks import outcome, not connectivity).
 // Null on records written before this field existed -- a scheduled check populates it on its first run.
 public DateTimeOffset? LastHealthCheckUtc { get; set; }
 public string LastHealthState { get; set; } = "NEVER_CHECKED";
 public int? LastHealthHttpStatus { get; set; }
 public long? LastHealthLatencyMs { get; set; }
 public string LastHealthError { get; set; } = "";
 // #892: the credential state as a word (presence and the last real answer), persisted so a restart and the source list show it before the next probe. Never a value.
 public string LastCredentialState { get; set; } = "UNKNOWN";
 // #893: the configuration revision this record is at (0 = never saved through the ledger); a save whose loaded revision is stale is refused.
 public int ConfigRevision { get; set; }
 // #896: the source's priority when two sources carry the same product -- the higher one wins a field, equal ones go to the fresher feed.
 public int Priority {get;set;}=100;
 // #898: the refresh SLA profile (0 = derived from the check interval) and the last recorded SLA state word -- state, not configuration.
 public int SlaRefreshMinutes {get;set;} public int SlaGraceMinutes {get;set;} public string LastSlaState {get;set;}="";
 public string Id {get;set;}=Guid.NewGuid().ToString("N"); public string Name {get;set;}=""; public string Location {get;set;}=""; public bool Enabled {get;set;}=true;public int IntervalMinutes {get;set;}=30;
 public string ItemPath {get;set;}="";public string DecimalSeparator {get;set;}=".";public Dictionary<string,string> Fields {get;set;}=new();
 public decimal ExchangeRate {get;set;}=1;public decimal MarkupPercent {get;set;}=40;public decimal FixedAmount {get;set;}=0;public decimal MinimumPrice {get;set;}=0;public string Currency {get;set;}="USD";
 public int SafetyStock {get;set;}=3;public int MinimumStock {get;set;}=0;public int MaximumStock {get;set;}=20;public string BrandFilter {get;set;}="";public string CategoryFilter {get;set;}="";
 public bool UpdateName {get;set;} public bool UpdateDescription {get;set;} public bool UpdateImages {get;set;}
}
public class CatalogProduct {
 public string Mpn {get;set;}="";
 public string InvoiceName {get;set;}="";
 public string Subtitle {get;set;}="";
 public string Shelf {get;set;}="";
 public DateTime? ExpiresOn {get;set;}

 public string Gtin {get;set;}=""; public bool Active {get;set;}=true; public bool SourceMissing {get;set;} public bool Anomaly {get;set;} public bool Duplicate {get;set;} public decimal? ApproximateProfit => Price > 0 && Cost >= 0 ? Price - Cost : null; public decimal? ApproximateMarginPercent => Price > 0 && ApproximateProfit.HasValue ? ApproximateProfit.Value / Price * 100m : null; public string StatusLabel => Active?"Aktif":"Pasif";
 public string CostCurrency {get;set;}="TRY";
 public decimal? FormulaPriceTry {get;set;}
 public decimal? AppliedTryRate {get;set;}
 public DateTime? FxRateDate {get;set;}
 public bool EtsyCreationAttempted {get;set;}
 public string Id {get;set;}=Guid.NewGuid().ToString("N");public string SourceId {get;set;}="";public string SourceKind {get;set;}="manual";public string PriceSource {get;set;}="manual";public string StockSource {get;set;}="manual";public string MediaSource {get;set;}="manual";public DateTime? SourceUpdatedUtc {get;set;}public string Sku {get;set;}="";public string Barcode {get;set;}="";public string Name {get;set;}="";public string Description {get;set;}="";public string Brand {get;set;}="";public string Category {get;set;}="";public string Currency {get;set;}="USD";public decimal VatRate {get;set;}=20;public string ImageUrls {get;set;}="";
 public decimal Cost {get;set;} public decimal Price {get;set;} public int Stock {get;set;} public bool LockName {get;set;} public bool LockDescription {get;set;} public bool LockPrice {get;set;} public bool LockStock {get;set;} public bool LockImages {get;set;} public string EtsyListingId {get;set;}=""; public DateTime UpdatedUtc {get;set;}
 // Shipping desi (volumetric/actual weight unit carriers bracket cost by). Null on records written before this field existed; profit calculations must treat null as NEEDS_WEIGHT_DATA, never as 0.
 public decimal? Desi {get;set;}
 // Independent, named price points (e.g. "Etsy fixed", "Wholesale") a channel can select instead of the formula-based PricePolicy.
 // Absent/empty on records written before this field existed; legacy Price/formula behavior is unchanged when empty.
 public List<CatalogPriceField> PriceFields {get;set;}=new();
 // #895: where each written field came from (source, source revision, import run, moment; or the operator). Null on records written before this existed.
 public Dictionary<string,FieldOrigin>? FieldOrigins {get;set;}
 // #904: why and since when each locked field is locked, keyed by field -- null on records written before this.
 public Dictionary<string,LockNote>? LockReasons {get;set;}
}
public sealed record CatalogPriceField(string Name, decimal Value, string Currency);
public record XmlScan(string ItemPath,IReadOnlyList<string> Paths,Dictionary<string,string> SuggestedFields);
public record ImportSummary(int Added,int Updated,int Unchanged, bool AlreadyApplied = false, string FeedHash = "");
public sealed class XmlImportContext
{
 public string FeedHash { get; init; } = "";
 public string MappingShapeFingerprint { get; init; } = "";
 public string PreviewFingerprint { get; init; } = "";
 public bool CompleteFeed { get; init; }
 public bool AllowMappingRevisionChange { get; init; }
 public DateTimeOffset? ObservedAtUtc { get; init; }
 // #895: the run and the source configuration revision this import writes under, stamped on every field it writes.
 public string RunId { get; init; } = "";
 public int SourceRevision { get; init; }
}
public sealed record XmlMappingSnapshot(string Fingerprint, int ItemCount, IReadOnlyList<string> MissingFields, IReadOnlyList<string> Warnings)
{
 public bool IsUsable => MissingFields.Count == 0 && ItemCount > 0;
}
public sealed class XmlMappingBlockedException : InvalidOperationException
{
 public string ReasonCode { get; }
 public XmlMappingBlockedException(string reasonCode, string message) : base(message) => ReasonCode = reasonCode;
}
public record CatalogPage(IReadOnlyList<CatalogProduct> Items,int Total,int InStock,int Linked);
public sealed record CatalogUndoReceipt(string Id,IReadOnlyList<CatalogProduct> Before,IReadOnlyList<CatalogProduct> After);




