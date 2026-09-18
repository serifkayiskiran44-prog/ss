using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using System.IO;
namespace TrMarketplaceHubDesktop.Catalog;
public static partial class XmlCatalog
{
 public static void ValidateSource(XmlSource s)=>Validate(s);
 static XDocument Document(string xml) { if(Encoding.UTF8.GetByteCount(xml)>25*1024*1024)throw new InvalidOperationException("XML 25 MB sınırını aşıyor.");try{using var r=XmlReader.Create(new StringReader(xml),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=25*1024*1024});var d=XDocument.Load(r);foreach(var e in d.Descendants()){e.Name=e.Name.LocalName;e.Attributes().Where(a=>a.IsNamespaceDeclaration).Remove();foreach(var a in e.Attributes().Where(a=>a.Name.Namespace!=XNamespace.None).ToList()){if(e.Attribute(a.Name.LocalName)!=null)throw new InvalidOperationException("Çakışan XML alan adları.");e.SetAttributeValue(a.Name.LocalName,a.Value);a.Remove();}}return d;}catch(XmlException){throw new InvalidOperationException("XML biçimi geçersiz veya belge yarıda kaldı; kaynak düzeltilmeden katalog değiştirilmedi.");}catch(ArgumentException){throw new InvalidOperationException("XML kaynağı okunamadı; belge kodlamasını kontrol edin.");} }
 static string PathOf(XElement e)=>"/"+string.Join("/",e.AncestorsAndSelf().Reverse().Select(x=>x.Name.LocalName));
 public static IReadOnlyList<string> ItemPaths(string xml)=>Document(xml).Descendants().Where(e=>e.HasElements).GroupBy(PathOf).OrderByDescending(g=>g.Count()).ThenBy(g=>g.Key.Length).Select(g=>g.Key).ToList();
 // Bounded sample validation: reports the TRUE total match count for the root/item
 // path (not just the capped sample used for field discovery) and a small preview
 // of what the first few matched items actually contain, so a zero-match or
 // wildly-wrong path is visible before any mapping/import is attempted.
 public static XmlScan Inspect(string xml,string? itemPath=null) {
  var d=Document(xml);
  if(string.IsNullOrWhiteSpace(itemPath))itemPath=d.Descendants().Where(e=>e.HasElements&&e.Elements().Any(c=>!c.HasElements)).GroupBy(PathOf).OrderByDescending(g=>g.Count()).ThenBy(g=>g.Key.Length).FirstOrDefault()?.Key??throw new InvalidOperationException("Ürün yolu bulunamadı.");
  var allMatches=d.XPathSelectElements(itemPath).ToList();
  if(allMatches.Count==0)throw new InvalidOperationException("Ürün yolunda kayıt yok.");
  var items=allMatches.Take(20).ToList();
  var paths=new HashSet<string>();foreach(var item in items)foreach(var e in item.DescendantsAndSelf()){var p=string.Join("/",e.AncestorsAndSelf().TakeWhile(x=>x!=item).Reverse().Select(x=>x.Name.LocalName));if(!e.HasElements&&p!="")paths.Add(p);foreach(var a in e.Attributes())paths.Add((p==""?"":p+"/")+"@"+a.Name.LocalName);}
  var aliases=new Dictionary<string,string[]>{["Name"]=["name","title","productname","urunadi"],["Sku"]=["sku","code","product_code","stockcode","stokkodu"],["Barcode"]=["barcode","ean","barkod"],["Gtin"]=["gtin","gtin13","gtin14","ean","ean13","upc"],["Cost"]=["cost","price","fiyat"],["Stock"]=["stock","qty","quantity","stok"],["Description"]=["description","aciklama"],["Brand"]=["brand","marka"],["Category"]=["category","kategori"],["ImageUrls"]=["image","url","resim"]};
  var fields=new Dictionary<string,string>();foreach(var (key,names) in aliases){var found=paths.FirstOrDefault(p=>names.Contains(p.Split('/').Last().TrimStart('@').ToLowerInvariant()));if(found!=null)fields[key]=found;}
  string? SampleValue(XElement item,string relativePath){try{return ((IEnumerable)item.XPathEvaluate(relativePath)).Cast<object>().Select(x=>x is XElement e?e.Value:((XAttribute)x).Value).FirstOrDefault(v=>!string.IsNullOrWhiteSpace(v));}catch{return null;}}
  var sample=items.Take(3).Select(item=>{var row=new Dictionary<string,string>();foreach(var (key,path) in fields){var value=SampleValue(item,path);if(value is not null)row[key]=value.Length>200?value[..200]:value;}return (IReadOnlyDictionary<string,string>)row;}).ToList();
  return new(itemPath,paths.Order().ToList(),fields,allMatches.Count,sample);
 }
 public static void Validate(XmlSource s){
  if((s.PriceMode=="Simple"&&(s.ExchangeRate<=0||s.MarkupPercent< -100))||s.MinimumPrice<0||s.SafetyStock<0||s.MinimumStock<0||s.MaximumStock<0||s.IntervalMinutes<1||s.DecimalSeparator is not ("." or ",")||s.Currency.Length!=3)throw new InvalidOperationException("Kur, fiyat, stok, para birimi veya aralık geçersiz.");
  if(!string.IsNullOrWhiteSpace(s.Location)&&Uri.TryCreate(s.Location.Trim(),UriKind.Absolute,out var uri)&&uri.Scheme is not ("http" or "https" or "file"))throw new InvalidOperationException("XML kaynağı yalnızca http/https veya yerel file adresi olabilir.");
  ValidateDefinition(s);
  CatalogPricing.ValidateSettings(s);
  XmlCategoryRules.Validate(s);
 }
 public static List<CatalogProduct> Preview(string xml,XmlSource source)=>PreviewMapped(xml,source);
}
