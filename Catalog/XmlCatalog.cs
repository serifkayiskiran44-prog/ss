using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using System.IO;
namespace TrMarketplaceHubDesktop.Catalog;
public static class XmlCatalog
{
 public static void ValidateSource(XmlSource s)=>Validate(s);
 static XDocument Document(string xml) { if(Encoding.UTF8.GetByteCount(xml)>25*1024*1024)throw new InvalidOperationException("XML 25 MB sınırını aşıyor.");using var r=XmlReader.Create(new StringReader(xml),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=25*1024*1024});var d=XDocument.Load(r);foreach(var e in d.Descendants()){e.Name=e.Name.LocalName;e.Attributes().Where(a=>a.IsNamespaceDeclaration).Remove();foreach(var a in e.Attributes().Where(a=>a.Name.Namespace!=XNamespace.None).ToList()){if(e.Attribute(a.Name.LocalName)!=null)throw new InvalidOperationException("Çakışan XML alan adları.");e.SetAttributeValue(a.Name.LocalName,a.Value);a.Remove();}}return d; }
 static string PathOf(XElement e)=>"/"+string.Join("/",e.AncestorsAndSelf().Reverse().Select(x=>x.Name.LocalName));
 public static XmlScan Inspect(string xml,string? itemPath=null) {var d=Document(xml);if(string.IsNullOrWhiteSpace(itemPath))itemPath=d.Descendants().Where(e=>e.HasElements&&e.Elements().Any(c=>!c.HasElements)).GroupBy(PathOf).OrderByDescending(g=>g.Count()).ThenBy(g=>g.Key.Length).FirstOrDefault()?.Key??throw new InvalidOperationException("Ürün yolu bulunamadı.");var items=d.XPathSelectElements(itemPath).Take(20).ToList();if(items.Count==0)throw new InvalidOperationException("Ürün yolunda kayıt yok.");var paths=new HashSet<string>();foreach(var item in items)foreach(var e in item.DescendantsAndSelf()){var p=string.Join("/",e.AncestorsAndSelf().TakeWhile(x=>x!=item).Reverse().Select(x=>x.Name.LocalName));if(!e.HasElements&&p!="")paths.Add(p);foreach(var a in e.Attributes())paths.Add((p==""?"":p+"/")+"@"+a.Name.LocalName);}var aliases=new Dictionary<string,string[]>{["Name"]=["name","title","productname","urunadi"],["Sku"]=["sku","code","product_code","stockcode","stokkodu"],["Barcode"]=["barcode","ean","barkod"],["Gtin"]=["gtin","gtin13","gtin14","ean","ean13","upc"],["Cost"]=["cost","price","fiyat"],["Stock"]=["stock","qty","quantity","stok"],["Description"]=["description","aciklama"],["Brand"]=["brand","marka"],["Category"]=["category","kategori"],["ImageUrls"]=["image","url","resim"]};var fields=new Dictionary<string,string>();foreach(var (key,names) in aliases){var found=paths.FirstOrDefault(p=>names.Contains(p.Split('/').Last().TrimStart('@').ToLowerInvariant()));if(found!=null)fields[key]=found;}return new(itemPath,paths.Order().ToList(),fields);}
 public static void Validate(XmlSource s){
  if((s.PriceMode=="Simple"&&(s.ExchangeRate<=0||s.MarkupPercent< -100))||s.MinimumPrice<0||s.SafetyStock<0||s.MinimumStock<0||s.MaximumStock<0||s.IntervalMinutes<1||s.DecimalSeparator is not ("." or ",")||s.Currency.Length!=3)throw new InvalidOperationException("Kur, fiyat, stok, para birimi veya aralık geçersiz.");
  if(!string.IsNullOrWhiteSpace(s.Location)&&Uri.TryCreate(s.Location.Trim(),UriKind.Absolute,out var uri)&&uri.Scheme is not ("http" or "https" or "file"))throw new InvalidOperationException("XML kaynağı yalnızca http/https veya yerel file adresi olabilir.");
  CatalogPricing.ValidateSettings(s);
 }
 public static List<CatalogProduct> Preview(string xml,XmlSource source)
 {
  Validate(source);var calculate=CatalogPricing.Create(source);bool Has(string key)=>source.Fields.TryGetValue(key,out var p)&&!string.IsNullOrWhiteSpace(p);
  if(string.IsNullOrWhiteSpace(source.ItemPath)||!Has("Name")||!Has("Cost")||!Has("Stock")||!(Has("Sku")||Has("Barcode")))throw new InvalidOperationException("Ürün yolu, ad, fiyat, stok ve SKU veya barkod eşleştirmesi gerekli.");
  bool IsImage(string key)=>key is "ImageUrls" or "Image" or "Images";
  IEnumerable<string> Paths(string key)=>IsImage(key)?source.Fields[key].Split('|',StringSplitOptions.TrimEntries):new[]{source.Fields[key]};
  foreach(var p in source.Fields.Keys.Where(Has).SelectMany(Paths))if(!Regex.IsMatch(p,@"^@?[\w-]+(?:/(?:@?[\w-]+))*$"))throw new InvalidOperationException("Alanlar göreli XML yolları olmalı.");
  var result=new List<CatalogProduct>();var seenSku=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var seenBar=new HashSet<string>(StringComparer.OrdinalIgnoreCase);int count=0;
  foreach(var item in Document(xml).XPathSelectElements(source.ItemPath)) {if(++count>100000)throw new InvalidOperationException("100.000 ürün sınırı aşıldı.");string Field(string key){if(!Has(key))return "";return string.Join(" | ",Paths(key).SelectMany(path=>((IEnumerable)item.XPathEvaluate(path)).Cast<object>()).Select(x=>x is XElement e?e.Value:((XAttribute)x).Value).Where(v=>!string.IsNullOrWhiteSpace(v))).Trim();}
   decimal Number(string key){var raw=Field(key);var sep=Regex.Escape(source.DecimalSeparator);if(!Regex.IsMatch(raw,@"^\d+(?:"+sep+@"\d+)?$")||!decimal.TryParse(raw.Replace(',','.'),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var n))throw new InvalidOperationException($"Satır {count}: {key} geçersiz sayı.");return n;}
   var cost=Number("Cost");var stock=Number("Stock");if(stock>int.MaxValue||stock!=decimal.Truncate(stock))throw new InvalidOperationException("Stok tam sayı olmalı.");var p=new CatalogProduct{SourceId=source.Id,Name=Field("Name"),Sku=Field("Sku"),Barcode=Field("Barcode"),Gtin=Field("Gtin"),Cost=cost,Description=Field("Description"),Brand=Field("Brand"),Category=Field("Category"),Currency=source.Currency,ImageUrls=string.Join(" | ",new[]{Field("ImageUrls"),Field("Image"),Field("Images")}.Where(v=>v!="").SelectMany(v=>v.Split(" | ",StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.Ordinal))};
   if(p.Name==""||(p.Sku==""&&p.Barcode==""))throw new InvalidOperationException("Ad ve kimlik boş olamaz.");if((p.Sku!=""&&!seenSku.Add(p.Sku))||(p.Barcode!=""&&!seenBar.Add(p.Barcode)))throw new InvalidOperationException("XML içinde yinelenen SKU veya barkod.");
   var calculation=calculate(cost);p.Price=calculation.FinalPrice;p.CostCurrency=source.CostCurrency;p.FormulaPriceTry=calculation.SourcePrice;p.AppliedTryRate=source.PriceMode=="Formula"?(source.Currency=="TRY"?1:source.TryPerTargetUnit):null;p.FxRateDate=source.PriceMode=="Formula"&&source.AutoFx?source.FxRateDate:null;p.Stock=stock<=source.SafetyStock||stock<source.MinimumStock?0:Math.Min(source.MaximumStock,(int)stock-source.SafetyStock);
   bool Included(string v,string f)=>string.IsNullOrWhiteSpace(f)||f.Split(';',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).Contains(v,StringComparer.OrdinalIgnoreCase);if(Included(p.Brand,source.BrandFilter)&&Included(p.Category,source.CategoryFilter))result.Add(p);
  }if(count==0)throw new InvalidOperationException("Ürün bulunamadı; katalog değiştirilmedi.");return result;
 }
}



