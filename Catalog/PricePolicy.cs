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
 public DateTimeOffset? FxRateObservedUtc {get;set;} }
public sealed record PricePreview(string Sku, decimal Price, string Currency, decimal FormulaPriceTry, decimal CostTry);
public partial class CatalogStore {
 static void InitializePricePolicies(SqliteConnection c){using var x=c.CreateCommand();x.CommandText="CREATE TABLE IF NOT EXISTS PricePolicies(Channel TEXT NOT NULL,Shop TEXT NOT NULL,Json TEXT NOT NULL,PRIMARY KEY(Channel,Shop))";x.ExecuteNonQuery();}
 public PricePolicy? GetPricePolicy(string ch,string shop){var k=PolicyKey(ch,shop);using var c=Open();using var x=c.CreateCommand();x.CommandText="SELECT Json FROM PricePolicies WHERE Channel=$c AND Shop=$s";x.Parameters.AddWithValue("$c",k.Channel);x.Parameters.AddWithValue("$s",k.Shop);return x.ExecuteScalar() is string j?JsonSerializer.Deserialize<PricePolicy>(j):null;}
 public PricePolicy SavePricePolicy(PricePolicy p){var k=PolicyKey(p.Channel,p.Shop);if(p.Formula.Length is 0 or >8192||p.Currency.Length!=3||p.TryPerUnit<=0||p.MinimumPrice<0||p.MinimumMarginTry<0)throw new ArgumentException("Fiyat kuralı geçersiz.");if(p.Currency is not("TRY" or "USD" or "EUR" or "GBP"))throw new ArgumentException("Desteklenmeyen döviz.");_=PriceFormula.Compile(p.Formula);using var c=Open();using var tx=c.BeginTransaction();var old=GetPricePolicy(c,tx,k.Channel,k.Shop);if((old?.Version??0)!=p.Version)throw new InvalidOperationException("Fiyat kuralı başka işlemde değişti.");p.Channel=k.Channel;p.Shop=k.Shop;p.UpdatedUtc=DateTime.UtcNow;p.Version++;using var x=c.CreateCommand();x.Transaction=tx;x.CommandText="INSERT INTO PricePolicies VALUES($c,$s,$j) ON CONFLICT(Channel,Shop) DO UPDATE SET Json=excluded.Json";x.Parameters.AddWithValue("$c",p.Channel);x.Parameters.AddWithValue("$s",p.Shop);x.Parameters.AddWithValue("$j",JsonSerializer.Serialize(p));x.ExecuteNonQuery();tx.Commit();return p;}
 static PricePolicy? GetPricePolicy(SqliteConnection c,SqliteTransaction tx,string ch,string shop){using var x=c.CreateCommand();x.Transaction=tx;x.CommandText="SELECT Json FROM PricePolicies WHERE Channel=$c AND Shop=$s";x.Parameters.AddWithValue("$c",ch);x.Parameters.AddWithValue("$s",shop);return x.ExecuteScalar() is string j?JsonSerializer.Deserialize<PricePolicy>(j):null;}
 public PricePreview PreviewPrice(string ch,string shop,string id){
  var p=GetPricePolicy(ch,shop)??throw new InvalidOperationException("Önce fiyat kuralı kaydedin.");
  if(!p.Enabled)throw new InvalidOperationException("Fiyat kuralı devre dışı.");
  using var c=Open();using var x=c.CreateCommand();x.CommandText="SELECT Json FROM CatalogProducts WHERE Id=$id";x.Parameters.AddWithValue("$id",id);if(x.ExecuteScalar() is not string j)throw new InvalidOperationException("Ürün bulunamadı.");var product=JsonSerializer.Deserialize<CatalogProduct>(j)!;if(!product.Active)throw new InvalidOperationException("Pasif ürün için fiyat üretilemez.");
  // The shared money preflight (#285) treats cost as TRY-denominated; a foreign-currency cost would need its
  // own FX leg the calculator doesn't model, so fail closed rather than silently treat it as TRY.
  if(!product.CostCurrency.Equals("TRY",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException($"Ürün maliyeti {product.CostCurrency} para biriminde; ortak kâr hesaplayıcı yalnız TRY maliyeti destekler.");
  decimal salePrice,formulaPriceTry;string saleCurrency;
  if(!string.IsNullOrWhiteSpace(p.PriceFieldName)){
   var field=product.PriceFields.FirstOrDefault(f=>f.Name.Equals(p.PriceFieldName,StringComparison.OrdinalIgnoreCase))??throw new InvalidOperationException($"'{p.PriceFieldName}' adlı fiyat alanı üründe tanımlı değil.");
   if(!field.Currency.Equals(p.Currency,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException($"'{field.Name}' fiyat alanı {field.Currency} para biriminde; fiyat kuralı {p.Currency} bekliyor.");
   if(field.Value<p.MinimumPrice)throw new InvalidOperationException("Fiyat koruması kuralı reddetti.");
   salePrice=field.Value;saleCurrency=field.Currency;formulaPriceTry=field.Value;
  } else {
   var formula=PriceFormula.Evaluate(p.Formula,product.Cost);if(formula<=0)throw new InvalidOperationException("Formül pozitif fiyat üretmedi.");
   var rate=p.Currency=="TRY"?1:p.TryPerUnit;var result=Math.Round(formula/rate,2,MidpointRounding.AwayFromZero);
   if(result<p.MinimumPrice)throw new InvalidOperationException("Fiyat koruması kuralı reddetti.");
   salePrice=result;saleCurrency=p.Currency;formulaPriceTry=formula;
  }
  // A TRY policy has no FX leg to go stale; a non-TRY policy must carry a real observed-rate timestamp.
  var fxSnapshot=p.Currency=="TRY"?DateTimeOffset.UtcNow:p.FxRateObservedUtc;
  var money=MoneyPriceCalculator.Calculate(new MoneyPriceInput(product.Sku,ch,shop,salePrice,product.Cost,saleCurrency){
   CommissionRatePercent=p.CommissionPercent,EstimatedShipping=p.EstimatedShippingTry,TransactionCost=p.TransactionCostTry,
   VatRatePercent=p.VatRatePercent,VatIncludedInSale=p.VatIncludedInSale,
   FxRateTryPerUnit=p.Currency=="TRY"?null:p.TryPerUnit,FxSnapshotUtc=fxSnapshot});
  PriceDispatchPreflight.EnsureReady(money);
  // The operator's minimum-margin guard (#790). Before #285 it was compared with the naive formula-minus-cost,
  // which ignored every fee; #285 replaced that check with the money gate and dropped the guard entirely. It is
  // enforced here against the real net contribution the gate just computed, so "at least N TRY per sale"
  // means what the operator typed: after commission, shipping, transaction cost and VAT.
  if(money.NetContribution<p.MinimumMarginTry)throw new InvalidOperationException($"Fiyat gönderimi engellendi: net katkı {money.NetContribution.ToString("0.00",System.Globalization.CultureInfo.InvariantCulture)} TRY, asgari kâr {p.MinimumMarginTry.ToString("0.00",System.Globalization.CultureInfo.InvariantCulture)} TRY altında; {money.ChannelShop}.");
  return new(product.Sku,salePrice,saleCurrency,formulaPriceTry,product.Cost);
 }
}
