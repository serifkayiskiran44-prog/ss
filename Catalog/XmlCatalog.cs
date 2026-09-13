using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
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
 public static XmlMappingSnapshot MappingSnapshot(string xml, XmlSource source)
 {
  Validate(source);
  var document = RawDocument(xml);
  var items = document.Descendants().Where(e => LocalPath(e) == NormalizePath(source.ItemPath)).ToList();
  if (items.Count == 0) return new XmlMappingSnapshot(Hash("empty|" + source.ItemPath), 0, RequiredFields(source), ["Ürün yolu kaynakta bulunamadı."]);
  var missing = RequiredFields(source).Where(field => field == "Sku veya Barcode"
   ? !items.Any(item => ResolveValues(item, source.Fields.GetValueOrDefault("Sku", "")).Concat(ResolveValues(item, source.Fields.GetValueOrDefault("Barcode", ""))).Any(v => !string.IsNullOrWhiteSpace(v)))
   : !items.Any(item => ResolveValues(item, source.Fields[field]).Any(v => !string.IsNullOrWhiteSpace(v)))).ToArray();
  var warnings = new List<string>();
  var shape = new StringBuilder();
  shape.Append(source.ItemPath.Trim()).Append('|');
  foreach (var field in source.Fields.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)) shape.Append(field.Key).Append('=').Append(field.Value).Append(';');
  foreach (var item in items.Take(1000))
  {
   foreach (var element in item.DescendantsAndSelf().OrderBy(e => LocalPath(e), StringComparer.Ordinal))
    shape.Append(LocalPath(element)).Append('@').Append(element.Name.NamespaceName).Append('#').Append(element.Elements().Count()).Append(';');
  }
  return new XmlMappingSnapshot(Hash(shape.ToString()), items.Count, missing, warnings);
 }

 public static void EnsureMappingReady(XmlSource source, XmlMappingSnapshot snapshot, bool scheduled)
 {
  if (snapshot.ItemCount == 0) throw new XmlMappingBlockedException("SOURCE_EMPTY", "XML ürün yolu boş veya kaynak ürün içermiyor.");
  if (snapshot.MissingFields.Count > 0) throw new XmlMappingBlockedException("MAPPING_REQUIRED_FIELDS", "Zorunlu XML alanları bulunamadı: " + string.Join(", ", snapshot.MissingFields));
  if (string.IsNullOrWhiteSpace(source.LastMappingShapeFingerprint)) return;
  if (string.Equals(source.LastMappingShapeFingerprint, snapshot.Fingerprint, StringComparison.Ordinal)) return;
  if (!scheduled && source.MappingRevision > source.LastAppliedMappingRevision) return;
  var code = scheduled ? "MAPPING_STALE" : "REMAP_REQUIRED";
  throw new XmlMappingBlockedException(code, scheduled
   ? "XML yapısı veya eşleme değişti; zamanlanmış aktarım güvenlik nedeniyle durduruldu. Önizleme yapıp eşlemeyi yeniden kaydedin."
   : "XML yapısı veya eşleme değişti; yeni eşleme revizyonunu kaydedip tekrar önizleyin.");
 }

 /// <summary>#897: the required mappings (Name, Cost, Stock, and Sku or Barcode) a source does not provide -- empty when the mapping is complete.</summary>
 public static IReadOnlyList<string> MissingRequiredMappings(XmlSource source) => RequiredFields(source);
 static IReadOnlyList<string> RequiredFields(XmlSource source)
 {
  var required = new List<string> { "Name", "Cost", "Stock" };
  if (!source.Fields.ContainsKey("Sku") && !source.Fields.ContainsKey("Barcode")) required.Add("Sku veya Barcode");
  return required.Where(x => x == "Sku veya Barcode" || !source.Fields.ContainsKey(x)).ToArray();
 }
 static XDocument RawDocument(string xml)
 {
  if (Encoding.UTF8.GetByteCount(xml) > 25 * 1024 * 1024) throw new InvalidOperationException("XML 25 MB sınırını aşıyor.");
  try
  {
   using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 25 * 1024 * 1024 });
   return XDocument.Load(reader);
  }
  catch (XmlException) { throw new InvalidOperationException("XML biçimi geçersiz veya belge yarıda kaldı; kaynak düzeltilmeden katalog değiştirilmedi."); }
 }
 static string NormalizePath(string path) => "/" + string.Join("/", path.Trim().Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(x => x.TrimStart('@')));
 static string LocalPath(XElement element) => "/" + string.Join("/", element.AncestorsAndSelf().Reverse().Select(x => x.Name.LocalName));
 static IEnumerable<string> ResolveValues(XElement item, string path)
 {
  if (string.IsNullOrWhiteSpace(path)) yield break;
  foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
  {
   if (part.StartsWith('@')) { var attr = item.Attribute(part[1..]); if (attr is not null) yield return attr.Value; yield break; }
    var next = item.Elements().FirstOrDefault(x => x.Name.LocalName.Equals(part, StringComparison.OrdinalIgnoreCase));
    if (next is null) yield break;
    item = next;
  }
  yield return item.Value;
 }
 static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
 static XDocument Document(string xml) { if(Encoding.UTF8.GetByteCount(xml)>25*1024*1024)throw new InvalidOperationException("XML 25 MB sınırını aşıyor.");try{using var r=XmlReader.Create(new StringReader(xml),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=25*1024*1024});var d=XDocument.Load(r);foreach(var e in d.Descendants()){e.Name=e.Name.LocalName;e.Attributes().Where(a=>a.IsNamespaceDeclaration).Remove();foreach(var a in e.Attributes().Where(a=>a.Name.Namespace!=XNamespace.None).ToList()){if(e.Attribute(a.Name.LocalName)!=null)throw new InvalidOperationException("Çakışan XML alan adları.");e.SetAttributeValue(a.Name.LocalName,a.Value);a.Remove();}}return d;}catch(XmlException){throw new InvalidOperationException("XML biçimi geçersiz veya belge yarıda kaldı; kaynak düzeltilmeden katalog değiştirilmedi.");}catch(ArgumentException){throw new InvalidOperationException("XML kaynağı okunamadı; belge kodlamasını kontrol edin.");} }
 static string PathOf(XElement e)=>"/"+string.Join("/",e.AncestorsAndSelf().Reverse().Select(x=>x.Name.LocalName));
 public static XmlScan Inspect(string xml,string? itemPath=null) {var d=Document(xml);if(string.IsNullOrWhiteSpace(itemPath))itemPath=d.Descendants().Where(e=>e.HasElements&&e.Elements().Any(c=>!c.HasElements)).GroupBy(PathOf).OrderByDescending(g=>g.Count()).ThenBy(g=>g.Key.Length).FirstOrDefault()?.Key??throw new InvalidOperationException("Ürün yolu bulunamadı.");var items=d.XPathSelectElements(itemPath).Take(20).ToList();if(items.Count==0)throw new InvalidOperationException("Ürün yolunda kayıt yok.");var paths=new HashSet<string>();foreach(var item in items)foreach(var e in item.DescendantsAndSelf()){var p=string.Join("/",e.AncestorsAndSelf().TakeWhile(x=>x!=item).Reverse().Select(x=>x.Name.LocalName));if(!e.HasElements&&p!="")paths.Add(p);foreach(var a in e.Attributes())paths.Add((p==""?"":p+"/")+"@"+a.Name.LocalName);}var aliases=new Dictionary<string,string[]>{["Name"]=["name","title","productname","urunadi"],["Sku"]=["sku","code","product_code","stockcode","stokkodu"],["Barcode"]=["barcode","ean","barkod"],["Gtin"]=["gtin","gtin13","gtin14","ean","ean13","upc"],["Cost"]=["cost","price","fiyat"],["Stock"]=["stock","qty","quantity","stok"],["Description"]=["description","aciklama"],["Brand"]=["brand","marka"],["Category"]=["category","kategori"],["ImageUrls"]=["image","url","resim"]};var fields=new Dictionary<string,string>();foreach(var (key,names) in aliases){var found=paths.FirstOrDefault(p=>names.Contains(p.Split('/').Last().TrimStart('@').ToLowerInvariant()));if(found!=null)fields[key]=found;}return new(itemPath,paths.Order().ToList(),fields);}
 public static void Validate(XmlSource s){
  if((s.PriceMode=="Simple"&&(s.ExchangeRate<=0||s.MarkupPercent< -100))||s.MinimumPrice<0||s.SafetyStock<0||s.MinimumStock<0||s.MaximumStock<0||s.MissingSourceGraceMinutes<0||s.IntervalMinutes<1||s.DecimalSeparator is not ("." or ",")||s.Currency.Length!=3)throw new InvalidOperationException("Kur, fiyat, stok, para birimi veya aralık geçersiz.");
  _ = DeterministicNumberParser.Culture(s.NumberCultureName);
  if(!string.IsNullOrWhiteSpace(s.Location)&&Uri.TryCreate(s.Location.Trim(),UriKind.Absolute,out var uri)&&uri.Scheme is not ("http" or "https" or "file"))throw new InvalidOperationException("XML kaynağı yalnızca http/https veya yerel file adresi olabilir.");
  CatalogPricing.ValidateSettings(s);
 }
 public static List<CatalogProduct> Preview(string xml,XmlSource source)=>Preview(xml,source,null);
 // #827: an optional progress sink; the item count is not known until the enumeration ends, so the running events carry a count and no total.
 public static List<CatalogProduct> Preview(string xml,XmlSource source,IProgress<ImportProgressEvent>? progress)
 {
  Validate(source);var calculate=CatalogPricing.Create(source);bool Has(string key)=>source.Fields.TryGetValue(key,out var p)&&!string.IsNullOrWhiteSpace(p);
  if(string.IsNullOrWhiteSpace(source.ItemPath)||!Has("Name")||!Has("Cost")||!Has("Stock")||!(Has("Sku")||Has("Barcode")))throw new InvalidOperationException("Ürün yolu, ad, fiyat, stok ve SKU veya barkod eşleştirmesi gerekli.");
  bool IsImage(string key)=>key is "ImageUrls" or "Image" or "Images";
  IEnumerable<string> Paths(string key)=>IsImage(key)?source.Fields[key].Split('|',StringSplitOptions.TrimEntries):new[]{source.Fields[key]};
  foreach(var p in source.Fields.Keys.Where(Has).SelectMany(Paths))if(!Regex.IsMatch(p,@"^@?[\w-]+(?:/(?:@?[\w-]+))*$"))throw new InvalidOperationException("Alanlar göreli XML yolları olmalı.");
  var result=new List<CatalogProduct>();var seenSku=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var seenBar=new HashSet<string>(StringComparer.OrdinalIgnoreCase);int count=0;
  foreach(var item in Document(xml).XPathSelectElements(source.ItemPath)) {if(++count>100000)throw new InvalidOperationException("100.000 ürün sınırı aşıldı.");if(progress!=null&&count%500==0)progress.Report(new(ImportProgressStage.Preview,ImportProgressStatus.Running,count));string Field(string key){if(!Has(key))return "";return string.Join(" | ",Paths(key).SelectMany(path=>((IEnumerable)item.XPathEvaluate(path)).Cast<object>()).Select(x=>x is XElement e?e.Value:((XAttribute)x).Value).Where(v=>!string.IsNullOrWhiteSpace(v))).Trim();}
   decimal Number(string key){var raw=Field(key);var parsed=DeterministicNumberParser.Decimal(raw,source.NumberCultureName,key);if(!parsed.Success)throw new InvalidOperationException($"Satır {count}: {parsed.Message} ({parsed.Code}).");return parsed.Value;}
   var cost=Number("Cost");var stock=Number("Stock");if(stock>int.MaxValue||stock!=decimal.Truncate(stock))throw new InvalidOperationException("Stok tam sayı olmalı.");var p=new CatalogProduct{SourceId=source.Id,SourceKind="xml",PriceSource="xml",StockSource="xml",MediaSource="xml",SourceUpdatedUtc=DateTime.UtcNow,Name=Field("Name"),Sku=Field("Sku"),Barcode=Field("Barcode"),Gtin=Field("Gtin"),Cost=cost,Description=Field("Description"),Brand=Field("Brand"),Category=Field("Category"),WeightText=Field("Weight"),DimensionsText=Field("Dimensions"),TaxClass=Field("TaxClass"),Currency=source.Currency,ImageUrls=string.Join(" | ",new[]{Field("ImageUrls"),Field("Image"),Field("Images")}.Where(v=>v!="").SelectMany(v=>v.Split(" | ",StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.Ordinal))};
   if(p.Name==""||(p.Sku==""&&p.Barcode==""))throw new InvalidOperationException("Ad ve kimlik boş olamaz.");if((p.Sku!=""&&!seenSku.Add(p.Sku))||(p.Barcode!=""&&!seenBar.Add(p.Barcode)))throw new InvalidOperationException("XML içinde yinelenen SKU veya barkod.");
   var calculation=calculate(cost);p.Price=calculation.FinalPrice;p.CostCurrency=source.CostCurrency;p.FormulaPriceTry=calculation.SourcePrice;p.AppliedTryRate=source.PriceMode=="Formula"?(source.Currency=="TRY"?1:source.TryPerTargetUnit):null;p.FxRateDate=source.PriceMode=="Formula"&&source.AutoFx?source.FxRateDate:null;p.Stock=stock<=source.SafetyStock||stock<source.MinimumStock?0:Math.Min(source.MaximumStock,(int)stock-source.SafetyStock);
   bool Included(string v,string f)=>string.IsNullOrWhiteSpace(f)||f.Split(';',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).Contains(v,StringComparer.OrdinalIgnoreCase);if(Included(p.Brand,source.BrandFilter)&&Included(p.Category,source.CategoryFilter))result.Add(p);
  }if(count==0)throw new InvalidOperationException("Ürün bulunamadı; katalog değiştirilmedi.");return result;
 }
}



