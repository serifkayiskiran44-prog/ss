namespace TrMarketplaceHubDesktop.Catalog;
public record PriceCalculation(decimal? SourcePrice,decimal FinalPrice);
public static class CatalogPricing
{
 public static void ValidateSettings(XmlSource s)
 {
  if(s.PriceMode is not ("Simple" or "Formula"))throw new InvalidOperationException("Fiyat modeli geçersiz.");
  if(s.PriceMode=="Formula"){
   if(s.CostCurrency!="TRY")throw new InvalidOperationException("Formül modeli TL alış fiyatı bekler. Kaynak dövizi TRY olmalı.");
   if(s.FxKind is not ("ForexSelling" or "ForexBuying")||s.TryPerTargetUnit<0)throw new InvalidOperationException("Kur türü veya TL karşılığı geçersiz.");
   try{_=PriceFormula.Compile(s.Formula);}catch(FormatException e){throw new InvalidOperationException(e.Message);}
  }
 }
 public static void ValidateRate(XmlSource s)
 {
  if(s.PriceMode!="Formula"||s.Currency=="TRY")return;
  if(s.TryPerTargetUnit<=0)throw new InvalidOperationException("1 döviz biriminin TL karşılığı gerekli. Otomatik kuru yenile veya manuel kur gir.");
  if(s.AutoFx&&(!s.FxRateDate.HasValue||!s.FxFetchedUtc.HasValue||s.FxRateDate.Value.Date<DateTime.UtcNow.Date.AddDays(-7)||s.FxRateDate.Value.Date>DateTime.UtcNow.Date.AddDays(1)||s.FxFetchedUtc<DateTimeOffset.UtcNow.AddHours(-24)||s.FxFetchedUtc>DateTimeOffset.UtcNow.AddMinutes(5)))throw new InvalidOperationException("Otomatik kur kaydı eksik veya eski. Önizlemeyi yeni kurla tekrar hesapla.");
 }
 public static Func<decimal,PriceCalculation> Create(XmlSource s)
 {
  ValidateSettings(s);ValidateRate(s);
  var compiled=s.PriceMode=="Formula"?PriceFormula.Compile(s.Formula):null;
  var rate=s.Currency=="TRY"?1:s.TryPerTargetUnit;var minimum=s.MinimumPrice;
  var exchange=s.ExchangeRate;var markup=s.MarkupPercent;var fixedAmount=s.FixedAmount;
  return cost=>{
   if(cost<0)throw new InvalidOperationException("Alış fiyatı negatif olamaz.");
   try{
    decimal? sourceValue=compiled?.Evaluate(cost);
    if(sourceValue<0)throw new InvalidOperationException("Formül negatif satış fiyatı üretti; ürün fiyatı değiştirilmedi.");
    var result=sourceValue.HasValue?sourceValue.Value/rate:cost*exchange*(1+markup/100)+fixedAmount;
    return new(sourceValue,Math.Round(Math.Max(minimum,result),2,MidpointRounding.AwayFromZero));
   }catch(OverflowException){throw new InvalidOperationException("Fiyat hesaplaması sayı sınırını aşıyor.");}
  };
 }
 public static PriceCalculation Calculate(decimal cost,XmlSource source)=>Create(source)(cost);
}
