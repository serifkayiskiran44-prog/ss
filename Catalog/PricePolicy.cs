using Microsoft.Data.Sqlite;
using System.Text.Json;
namespace TrMarketplaceHubDesktop.Catalog;
public sealed class PricePolicy { string currency="TRY"; public string Channel {get;set;}=""; public string Shop {get;set;}=""; public string Formula {get;set;}="x"; public string Currency {get=>currency;set=>currency=(value??"").Trim().ToUpperInvariant();} public decimal TryPerUnit {get;set;}=1; public decimal MinimumPrice {get;set;} public decimal MinimumMarginTry {get;set;} public bool Enabled {get;set;}=true; public DateTime UpdatedUtc {get;set;} public int Version {get;set;}
 // When set, this channel+shop uses the named independent CatalogPriceField instead of Formula; the two selections coexist per-policy and never both apply.
 public string? PriceFieldName {get;set;}
 // Expense-aware dispatch gate (#285): all null until an operator fills them in, so a policy saved before this
 // field existed blocks (BlockedMissingInput) instead of silently treating unknown commission/shipping/tax as zero.
 public decimal? CommissionPercent {get;set;}
 public decimal? EstimatedShippingTry {get;set;}
 public decimal? TransactionCostTry {get;set;}
 public decimal? VatRatePercent {get;set;}
 public bool VatIncludedInSale {get;set;}
 // When Currency is not TRY, the moment TryPerUnit was last observed; required so a stale manual rate blocks
 // instead of silently being reused. Meaningless (and not required) for a TRY policy, which has no FX step.
 public DateTimeOffset? FxRateObservedUtc {get;set;}
 // #926: the discount stack's inputs -- a supplier discount on the cost, the shop's own discount and the channel's supported discount ("percent:10" or "amount:5") on the sale price; empty means none.
 public decimal? SupplierDiscountPercent {get;set;} public decimal? LocalDiscountPercent {get;set;} public string ChannelDiscount {get;set;}=""; }
public sealed record PricePreview(string Sku, decimal Price, string Currency, decimal FormulaPriceTry, decimal CostTry, string CostOrigin = "", string CommissionOrigin = "", string FxWarning = "", string RoundingOrigin = "", string DiscountOrigin = ""); // #922: the net margin names where its cost came from; #923: and the commission period it used; #924: and a stale-rate warning the operator's policy allowed; #925: and the rounding profile revision; #926: and the discount steps
public partial class CatalogStore {
 static void InitializePricePolicies(SqliteConnection c){using var x=c.CreateCommand();x.CommandText="CREATE TABLE IF NOT EXISTS PricePolicies(Channel TEXT NOT NULL,Shop TEXT NOT NULL,Json TEXT NOT NULL,PRIMARY KEY(Channel,Shop))";x.ExecuteNonQuery();}
 public PricePolicy? GetPricePolicy(string ch,string shop){var k=PolicyKey(ch,shop);using var c=Open();using var x=c.CreateCommand();x.CommandText="SELECT Json FROM PricePolicies WHERE Channel=$c AND Shop=$s";x.Parameters.AddWithValue("$c",k.Channel);x.Parameters.AddWithValue("$s",k.Shop);return x.ExecuteScalar() is string j?JsonSerializer.Deserialize<PricePolicy>(j):null;}
 public PricePolicy SavePricePolicy(PricePolicy p){var k=PolicyKey(p.Channel,p.Shop);if(p.Formula.Length is 0 or >8192||p.Currency.Length!=3||p.TryPerUnit<=0||p.MinimumPrice<0||p.MinimumMarginTry<0)throw new ArgumentException("Fiyat kuralı geçersiz.");if(p.Currency is not("TRY" or "USD" or "EUR" or "GBP"))throw new ArgumentException("Desteklenmeyen döviz.");DiscountStack.Validate(p.SupplierDiscountPercent,p.LocalDiscountPercent); /* #926 */_=PriceFormula.Compile(p.Formula);using var c=Open();using var tx=c.BeginTransaction();var old=GetPricePolicy(c,tx,k.Channel,k.Shop);if((old?.Version??0)!=p.Version)throw new InvalidOperationException("Fiyat kuralı başka işlemde değişti.");p.Channel=k.Channel;p.Shop=k.Shop;p.UpdatedUtc=DateTime.UtcNow;p.Version++;using var x=c.CreateCommand();x.Transaction=tx;x.CommandText="INSERT INTO PricePolicies VALUES($c,$s,$j) ON CONFLICT(Channel,Shop) DO UPDATE SET Json=excluded.Json";x.Parameters.AddWithValue("$c",p.Channel);x.Parameters.AddWithValue("$s",p.Shop);x.Parameters.AddWithValue("$j",JsonSerializer.Serialize(p));x.ExecuteNonQuery();tx.Commit();return p;}
 static PricePolicy? GetPricePolicy(SqliteConnection c,SqliteTransaction tx,string ch,string shop){using var x=c.CreateCommand();x.Transaction=tx;x.CommandText="SELECT Json FROM PricePolicies WHERE Channel=$c AND Shop=$s";x.Parameters.AddWithValue("$c",ch);x.Parameters.AddWithValue("$s",shop);return x.ExecuteScalar() is string j?JsonSerializer.Deserialize<PricePolicy>(j):null;}
 XmlSource? SourceById(string id)=>Sources().FirstOrDefault(s=>s.Id==id); /* #922 */
 public PricePreview PreviewPrice(string ch,string shop,string id,DateTimeOffset? asOfUtc=null,bool automatic=false){ /* #923: a past as-of computes with the commission period in force then; #924: an automatic live write never proceeds on a stale rate */
  var p=GetPricePolicy(ch,shop)??throw new InvalidOperationException("Önce fiyat kuralı kaydedin.");
  if(!p.Enabled)throw new InvalidOperationException("Fiyat kuralı devre dışı.");
  // #923: dated commission periods -- the one in force at the as-of date overrides the rule's flat rate, a gap between periods refuses (never a guessed rate), no period at all keeps the rule's.
  var asOf=asOfUtc??DateTimeOffset.UtcNow;var commission=new CommissionProfileStore(dataDirectory).Resolve(ch,shop,asOf.UtcDateTime);
  if(commission.State==CommissionResolution.Gap)throw new InvalidOperationException($"Fiyat gönderimi engellendi: {commission.Words}; {ch.Trim().ToLowerInvariant()}/{shop.Trim()}.");
  var commissionOrigin=commission.State==CommissionResolution.None?$"kural komisyonu %{p.CommissionPercent?.ToString("0.##",System.Globalization.CultureInfo.InvariantCulture)??"—"} (dönem yok)":commission.Words;
  var rounding=new RoundingProfileStore(dataDirectory).Resolve(ch,shop,asOf.UtcDateTime,p.Currency); /* #925: the shop's rounding profile in force at the as-of date, or the currency's default */
  using var c=Open();using var x=c.CreateCommand();x.CommandText="SELECT Json FROM CatalogProducts WHERE Id=$id";x.Parameters.AddWithValue("$id",id);if(x.ExecuteScalar() is not string j)throw new InvalidOperationException("Ürün bulunamadı.");var product=JsonSerializer.Deserialize<CatalogProduct>(j)!;if(!product.Active)throw new InvalidOperationException("Pasif ürün için fiyat üretilemez.");
  // The shared money preflight (#285) treats cost as TRY-denominated; a foreign-currency cost would need its
  // own FX leg the calculator doesn't model, so fail closed rather than silently treat it as TRY.
  if(!product.CostCurrency.Equals("TRY",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException($"Ürün maliyeti {product.CostCurrency} para biriminde; ortak kâr hesaplayıcı yalnız TRY maliyeti destekler.");
  if(product.Cost<=0)throw new InvalidOperationException("Fiyat gönderimi engellendi: ürün maliyeti girilmemiş; net kâr hesaplanamaz."); /* #922: never a margin against a cost nobody entered */
  decimal formulaPriceTry=0;string saleCurrency=p.Currency;
  // #926: the discount stack owns the order -- the supplier's discount on the cost, the sale price from that cost (a price field or the formula, rounded by the profile), the shop's then the channel's discount on the sale price, the profile's rounding last; an unknown channel discount, a bad number or a price discounted to nothing refuse by name.
  var stack=DiscountStack.Apply(product.Cost,effectiveCost=>{
   if(!string.IsNullOrWhiteSpace(p.PriceFieldName)){
    var field=product.PriceFields.FirstOrDefault(f=>f.Name.Equals(p.PriceFieldName,StringComparison.OrdinalIgnoreCase))??throw new InvalidOperationException($"'{p.PriceFieldName}' adlı fiyat alanı üründe tanımlı değil.");
    if(!field.Currency.Equals(p.Currency,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException($"'{field.Name}' fiyat alanı {field.Currency} para biriminde; fiyat kuralı {p.Currency} bekliyor.");
    formulaPriceTry=field.Value;saleCurrency=field.Currency;return rounding.Apply(field.Value); /* #925 */
   }
   var formula=PriceFormula.Evaluate(p.Formula,effectiveCost);if(formula<=0)throw new InvalidOperationException("Formül pozitif fiyat üretmedi.");
   var rate=p.Currency=="TRY"?1:p.TryPerUnit;formulaPriceTry=formula;saleCurrency=p.Currency;return rounding.Apply(formula/rate); /* #925: the profile decides the decimals and the midpoint, not a fixed two half away from zero */
  },p.SupplierDiscountPercent,p.LocalDiscountPercent,p.ChannelDiscount,rounding.Apply);
  if(stack.Blocks)throw new InvalidOperationException($"Fiyat gönderimi engellendi: {stack.Words}; {ch.Trim().ToLowerInvariant()}/{shop.Trim()}.");
  var salePrice=stack.SalePrice;
  if(salePrice<p.MinimumPrice)throw new InvalidOperationException("Fiyat koruması kuralı reddetti."); /* #926: the guard sees the price after every discount */
  // A TRY policy has no FX leg to go stale; a non-TRY policy must carry a real observed-rate timestamp.
  // #924: the operator's staleness policy decides how old the rate may be -- missing or stale blocks, unless the policy says warn and this is a manual preview; an automatic live write is blocked on a stale rate in every mode.
  var fxPolicy=new FxStalenessPolicyStore(dataDirectory).Get(ch,shop);var fxVerdict=p.Currency=="TRY"?new FxStalenessVerdict(FxStalenessVerdict.Fresh,FxStalenessVerdict.Allow,0,"TRY kural; kur yok"):FxStaleness.Evaluate(fxPolicy,p.FxRateObservedUtc,asOf,automatic);
  if(fxVerdict.Blocks)throw new InvalidOperationException($"Fiyat gönderimi engellendi: {(fxVerdict.State==FxStalenessVerdict.Missing?"BlockedMissingInput":"BlockedStaleFx")}; {fxVerdict.Words}; {ch.Trim().ToLowerInvariant()}/{shop.Trim()}.");
  var fxWarning=fxVerdict.Decision==FxStalenessVerdict.WarnDecision?fxVerdict.Words:"";
  var fxSnapshot=p.Currency=="TRY"?DateTimeOffset.UtcNow:p.FxRateObservedUtc;
  // #909: the tax class reaches the price only here, as the VAT percentage the gate already takes -- a known class supplies it, a missing one leaves the rule's, an unknown one is refused by name instead of blurring into "missing input".
  if(ProductTaxClass.Resolve(product) is {State:TaxClassResolution.Unknown} unknownTax)throw new InvalidOperationException($"Fiyat gönderimi engellendi: {unknownTax.Words}; {ch.Trim().ToLowerInvariant()}/{shop.Trim()}.");
  var money=MoneyPriceCalculator.Calculate(new MoneyPriceInput(product.Sku,ch,shop,salePrice,stack.EffectiveCost,saleCurrency){ /* #926: the cost actually paid after the supplier's discount */
   CommissionRatePercent=commission.Profile?.CommissionPercent??p.CommissionPercent,EstimatedShipping=p.EstimatedShippingTry,TransactionCost=p.TransactionCostTry+(commission.Profile?.FixedFeeTry??0m), /* #923: the period's rate and its fixed fee */
   VatRatePercent=ProductTaxClass.RateFor(product,p.VatRatePercent),VatIncludedInSale=p.VatIncludedInSale,
   FxRateTryPerUnit=p.Currency=="TRY"?null:p.TryPerUnit,FxSnapshotUtc=fxSnapshot,AsOfUtc=asOf,FxStaleAfter=fxVerdict.Decision==FxStalenessVerdict.WarnDecision?TimeSpan.MaxValue:fxPolicy.StaleAfter}); /* #923; #924: the policy's window, or none when the operator accepted the stale rate for this manual preview */
  PriceDispatchPreflight.EnsureReady(money);
  // The operator's minimum-margin guard (#790). Before #285 it was compared with the naive formula-minus-cost,
  // which ignored every fee; #285 replaced that check with the money gate and dropped the guard entirely. It is
  // enforced here against the real net contribution the gate just computed, so "at least N TRY per sale"
  // means what the operator typed: after commission, shipping, transaction cost and VAT.
  if(money.NetContribution<p.MinimumMarginTry)throw new InvalidOperationException($"Fiyat gönderimi engellendi: net katkı {money.NetContribution.ToString("0.00",System.Globalization.CultureInfo.InvariantCulture)} TRY, asgari kâr {p.MinimumMarginTry.ToString("0.00",System.Globalization.CultureInfo.InvariantCulture)} TRY altında; {money.ChannelShop}.");
  return new(product.Sku,salePrice,saleCurrency,formulaPriceTry,product.Cost,CostProvenance.Describe(product,SourceById,DateTime.UtcNow),commissionOrigin,fxWarning,rounding.Words,stack.Words); /* #922; #923; #924; #925; #926 */
 }
}
