namespace TrMarketplaceHubDesktop.Catalog;
public class XmlSource
{
 public decimal DefaultVatRate {get;set;}=20;
 public string DefaultBrand {get;set;}="";
 public string FixedBrand {get;set;}="";
 public string DefaultCategory {get;set;}="";
 public string FixedCategory {get;set;}="";
 public bool PriceIncludesVat {get;set;}=true;
 public bool UseFixedStock {get;set;}
 public int FixedStock {get;set;}=5;
 public bool StockIsText {get;set;}
 public string AvailableStockText {get;set;}="var";
 public int AvailableStockQuantity {get;set;}=5;
 public bool UpdateDetails {get;set;}
 public List<XmlCategoryRule> CategoryRules {get;set;}=[];
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
 public string ItemPath {get;set;}="";public string DecimalSeparator {get;set;}=".";public string StockDecimalSeparator {get;set;}=".";public string SkuPrefix {get;set;}="";public Dictionary<string,string> Fields {get;set;}=new();
 public decimal ExchangeRate {get;set;}=1;public decimal MarkupPercent {get;set;}=40;public decimal FixedAmount {get;set;}=0;public decimal MinimumPrice {get;set;}=0;public string Currency {get;set;}="USD";
 public int SafetyStock {get;set;}=3;public int MinimumStock {get;set;}=0;public int MaximumStock {get;set;}=20;public string BrandFilter {get;set;}="";public string CategoryFilter {get;set;}="";
 public bool UpdateName {get;set;} public bool UpdateDescription {get;set;} public bool UpdateImages {get;set;}
}
public class CatalogProduct {
 public long LocalNumber {get;set;}
 public long BrandNumber {get;set;}
 public long CategoryNumber {get;set;}
 [System.Text.Json.Serialization.JsonIgnore] public string ProductIdLabel => SourceProductId.Length>0?SourceProductId:LocalNumber>0?LocalNumber.ToString():"Aktarımda üretilecek";
 [System.Text.Json.Serialization.JsonIgnore] public string BrandIdLabel => XmlAttributes.GetValueOrDefault("BrandId", BrandNumber>0?BrandNumber.ToString():"");
 [System.Text.Json.Serialization.JsonIgnore] public string CategoryIdLabel => XmlAttributes.GetValueOrDefault("CategoryId1", CategoryNumber>0?CategoryNumber.ToString():"");
 public string SourceProductId {get;set;}="";
 public Dictionary<string,string> XmlAttributes {get;set;}=new();
 public string XmlCategory {get;set;}="";
 public Dictionary<string,XmlChannelPrice> ChannelPrices {get;set;}=new();
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
 /// When the FX rate used for FormulaPriceTry/AppliedTryRate was actually fetched
 /// (distinct from FxRateDate, the rate's own publish/value date) - kept alongside
 /// the applied rate so the pricing snapshot's provenance stays inspectable.
 public DateTimeOffset? FxFetchedUtc {get;set;}
 public bool EtsyCreationAttempted {get;set;}
 public string Id {get;set;}=Guid.NewGuid().ToString("N");public string SourceId {get;set;}="";public string SourceKind {get;set;}="manual";public string PriceSource {get;set;}="manual";public string StockSource {get;set;}="manual";public string MediaSource {get;set;}="manual";public DateTime? SourceUpdatedUtc {get;set;}public string Sku {get;set;}="";public string Barcode {get;set;}="";public string Name {get;set;}="";public string Description {get;set;}="";public string Brand {get;set;}="";public string Category {get;set;}="";public string Currency {get;set;}="USD";public decimal VatRate {get;set;}=20;public string ImageUrls {get;set;}="";
 public decimal Cost {get;set;} public decimal Price {get;set;} public int Stock {get;set;} public bool LockName {get;set;} public bool LockDescription {get;set;} public bool LockPrice {get;set;} public bool LockStock {get;set;} public bool LockImages {get;set;} public string EtsyListingId {get;set;}=""; public DateTime UpdatedUtc {get;set;}
}
public record XmlScan(string ItemPath,IReadOnlyList<string> Paths,Dictionary<string,string> SuggestedFields,int MatchCount,IReadOnlyList<IReadOnlyDictionary<string,string>> Sample);
public record ImportSummary(int Added,int Updated,int Unchanged);
public record CatalogPage(IReadOnlyList<CatalogProduct> Items,int Total,int InStock,int Linked);
public sealed record CatalogUndoReceipt(string Id,IReadOnlyList<CatalogProduct> Before,IReadOnlyList<CatalogProduct> After);




