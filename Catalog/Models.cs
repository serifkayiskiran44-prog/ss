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

 public string Gtin {get;set;}=""; public bool Active {get;set;}=true; public string StatusLabel => Active?"Aktif":"Pasif";
 public string CostCurrency {get;set;}="TRY";
 public decimal? FormulaPriceTry {get;set;}
 public decimal? AppliedTryRate {get;set;}
 public DateTime? FxRateDate {get;set;}
 public bool EtsyCreationAttempted {get;set;}
 public string Id {get;set;}=Guid.NewGuid().ToString("N");public string SourceId {get;set;}="";public string Sku {get;set;}="";public string Barcode {get;set;}="";public string Name {get;set;}="";public string Description {get;set;}="";public string Brand {get;set;}="";public string Category {get;set;}="";public string Currency {get;set;}="USD";public string ImageUrls {get;set;}="";
 public decimal Cost {get;set;} public decimal Price {get;set;} public int Stock {get;set;} public bool LockName {get;set;} public bool LockDescription {get;set;} public bool LockPrice {get;set;} public bool LockStock {get;set;} public bool LockImages {get;set;} public string EtsyListingId {get;set;}=""; public DateTime UpdatedUtc {get;set;}
}
public record XmlScan(string ItemPath,IReadOnlyList<string> Paths,Dictionary<string,string> SuggestedFields);
public record ImportSummary(int Added,int Updated,int Unchanged);
public record CatalogPage(IReadOnlyList<CatalogProduct> Items,int Total,int InStock,int Linked);
public sealed record CatalogUndoReceipt(string Id,IReadOnlyList<CatalogProduct> Before,IReadOnlyList<CatalogProduct> After);




