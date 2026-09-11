using Microsoft.Data.Sqlite;
using System.IO;
using System.Text.Json;
namespace TrMarketplaceHubDesktop.Catalog;
public partial class CatalogStore
{
 readonly string connectionString;
 public CatalogPage Search(string search,int offset=0,int limit=200,CatalogFilter? filter=null, CancellationToken cancellationToken=default)
 {
  cancellationToken.ThrowIfCancellationRequested();
  if(offset<0||limit<1||limit>1000)throw new ArgumentOutOfRangeException(nameof(limit),"Sayfa boyutu 1–1000, başlangıç sıfır veya daha büyük olmalı.");
  filter??=new();
  foreach(var values in new[]{filter.Brands,filter.Categories,filter.Skus,filter.SourceIds})if(values.Length>100||values.Sum(v=>v.Length)>6000)throw new ArgumentException("Filtre en fazla 100 değer ve 6000 karakter olabilir.");
  using var c=Open();using var tx=c.BeginTransaction(deferred:true);
  // Substring search cannot use a B-tree index; filter/count in SQLite and deserialize only the requested page.
  var where=" WHERE ($all=1 OR json_extract(Json,'$.Name') LIKE $q ESCAPE '\\' OR json_extract(Json,'$.Sku') LIKE $q ESCAPE '\\' OR json_extract(Json,'$.Barcode') LIKE $q ESCAPE '\\' OR json_extract(Json,'$.Brand') LIKE $q ESCAPE '\\' OR json_extract(Json,'$.Category') LIKE $q ESCAPE '\\'";
  where += ") AND ($active=-1 OR COALESCE(json_extract(Json,'$.Active'),1)=$active) AND ($description=-1 OR (length(trim(COALESCE(json_extract(Json,'$.Description'),'')))>0)=$description) AND ($image=-1 OR (length(trim(COALESCE(json_extract(Json,'$.ImageUrls'),'')))>0)=$image)";
  var extra=new Dictionary<string,string>();
  foreach(var (field,values) in new[]{("Brand",filter.Brands),("Category",filter.Categories),("Sku",filter.Skus),("SourceId",filter.SourceIds)}){
   if(values.Length==0)continue;
   var names=new List<string>();foreach(var value in values){var clean=value.Trim();if(clean.Length==0)continue;var key="$filter"+extra.Count;extra.Add(key,clean);names.Add(key);}if(names.Count==0)continue;
   where+=$" AND json_extract(Json,'$.{field}') COLLATE NOCASE IN ({string.Join(",",names)})";
  }
  search=search.Trim();var pattern="%"+search.Replace("\\","\\\\").Replace("%","\\%").Replace("_","\\_")+"%";
  void Params(SqliteCommand cmd){cmd.Transaction=tx;cmd.Parameters.AddWithValue("$all",search.Length==0?1:0);cmd.Parameters.AddWithValue("$q",pattern);cmd.Parameters.AddWithValue("$active",filter.Active.HasValue?(filter.Active.Value?1:0):-1);cmd.Parameters.AddWithValue("$description",filter.DescriptionPresent.HasValue?(filter.DescriptionPresent.Value?1:0):-1);cmd.Parameters.AddWithValue("$image",filter.ImagePresent.HasValue?(filter.ImagePresent.Value?1:0):-1);foreach(var pair in extra)cmd.Parameters.AddWithValue(pair.Key,pair.Value);}
  int total,inStock,linked;
  using(var count=c.CreateCommand()){cancellationToken.ThrowIfCancellationRequested();Params(count);count.CommandText="SELECT COUNT(*),COALESCE(SUM(CASE WHEN json_extract(Json,'$.Stock')>0 THEN 1 ELSE 0 END),0),COALESCE(SUM(CASE WHEN COALESCE(json_extract(Json,'$.EtsyListingId'),'')<>'' THEN 1 ELSE 0 END),0) FROM CatalogProducts"+where;using var reader=count.ExecuteReader();reader.Read();total=reader.GetInt32(0);inStock=reader.GetInt32(1);linked=reader.GetInt32(2);}
  var items=new List<CatalogProduct>();using(var page=c.CreateCommand()){cancellationToken.ThrowIfCancellationRequested();Params(page);page.CommandText="SELECT Json FROM CatalogProducts"+where+" ORDER BY json_extract(Json,'$.Name') COLLATE NOCASE,Id LIMIT $limit OFFSET $offset";page.Parameters.AddWithValue("$limit",limit);page.Parameters.AddWithValue("$offset",offset);using var reader=page.ExecuteReader();while(reader.Read()){cancellationToken.ThrowIfCancellationRequested();items.Add(JsonSerializer.Deserialize<CatalogProduct>(reader.GetString(0))!);}}
  tx.Commit();return new(items,total,inStock,linked);
 }
 public CatalogStore(string? directory=null){directory??=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MonoBridgeDesktop");Directory.CreateDirectory(directory);connectionString=new SqliteConnectionStringBuilder{DataSource=System.IO.Path.Combine(directory,"catalog.db"),DefaultTimeout=15,Pooling=true}.ToString();using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="PRAGMA journal_mode=WAL;PRAGMA synchronous=NORMAL;PRAGMA busy_timeout=15000;CREATE TABLE IF NOT EXISTS Sources(Id TEXT PRIMARY KEY, Json TEXT NOT NULL);CREATE TABLE IF NOT EXISTS CatalogProducts(Id TEXT PRIMARY KEY, Json TEXT NOT NULL);CREATE INDEX IF NOT EXISTS IX_CatalogProducts_Sku ON CatalogProducts(json_extract(Json,'$.Sku') COLLATE NOCASE);CREATE INDEX IF NOT EXISTS IX_CatalogProducts_Barcode ON CatalogProducts(json_extract(Json,'$.Barcode') COLLATE NOCASE);CREATE INDEX IF NOT EXISTS IX_CatalogProducts_Brand ON CatalogProducts(json_extract(Json,'$.Brand') COLLATE NOCASE);CREATE INDEX IF NOT EXISTS IX_CatalogProducts_Category ON CatalogProducts(json_extract(Json,'$.Category') COLLATE NOCASE);";cmd.ExecuteNonQuery();InitializeOrderStock(c);InitializeStockPolicies(c);InitializePricePolicies(c);SchemaVersion.Ensure(c);}
 SqliteConnection Open(){var c=new SqliteConnection(connectionString);c.Open();return c;}
 static List<T> Read<T>(SqliteConnection c,string table,SqliteTransaction? tx=null){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=$"SELECT Json FROM {table}";using var r=cmd.ExecuteReader();var list=new List<T>();while(r.Read())list.Add(JsonSerializer.Deserialize<T>(r.GetString(0))!);return list;}
 static void Put<T>(SqliteConnection c,string table,string id,T value,SqliteTransaction? tx=null){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=$"INSERT INTO {table}(Id,Json) VALUES($id,$json) ON CONFLICT(Id) DO UPDATE SET Json=excluded.Json";cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(value));cmd.ExecuteNonQuery();}
 public List<XmlSource> Sources(){using var c=Open();return Read<XmlSource>(c,"Sources");}
 public void SaveSource(XmlSource source){XmlCatalog.ValidateSource(source);if(string.IsNullOrWhiteSpace(source.Id))throw new InvalidOperationException("Kaynak kimliği boş.");using var c=Open();Put(c,"Sources",source.Id,source);}
 public List<CatalogProduct> Products(string search=""){using var c=Open();return Read<CatalogProduct>(c,"CatalogProducts").Where(p=>search==""||new[]{p.Name,p.Sku,p.Barcode,p.Brand,p.Category}.Any(v=>v.Contains(search,StringComparison.OrdinalIgnoreCase))).ToList();}
 static void Valid(CatalogProduct p){
  if(p.Mpn==null||p.Mpn.Length>128||p.InvoiceName==null||p.InvoiceName.Length>300||p.Subtitle==null||p.Subtitle.Length>300||p.Shelf==null||p.Shelf.Length>100)throw new InvalidOperationException("MPN en fazla 128, fatura adı/alt başlık 300, raf 100 karakter olabilir.");
  if(p.Name.Length>500||p.Sku.Length>128||p.Barcode.Length>64||p.Brand.Length>200||p.Category.Length>200||p.Description.Length>20000||p.Currency.Length is < 3 or > 3)throw new InvalidOperationException("Ürün metin alanlarından biri izin verilen uzunluğu aşıyor veya döviz kodu 3 karakter değil.");
  if(string.IsNullOrWhiteSpace(p.Name)||(string.IsNullOrWhiteSpace(p.Sku)&&string.IsNullOrWhiteSpace(p.Barcode))||p.Price<0||p.Cost<0||p.Stock<0||p.VatRate<0||p.VatRate>100)throw new InvalidOperationException("Ürün adı, kimliği, fiyatı, stoku veya KDV oranı geçersiz.");
 }
 public void SaveProduct(CatalogProduct product){Valid(product);using var c=Open();using var tx=c.BeginTransaction();using var find=c.CreateCommand();find.Transaction=tx;find.CommandText="SELECT Json FROM CatalogProducts WHERE Id=$id";find.Parameters.AddWithValue("$id",product.Id);var json=find.ExecuteScalar() as string??throw new InvalidOperationException("Ürün bulunamadı.");var old=JsonSerializer.Deserialize<CatalogProduct>(json)!;if(old.SourceId!=product.SourceId||old.Sku!=product.Sku||old.Barcode!=product.Barcode)throw new InvalidOperationException("Ürün kimliği elle değiştirilemez.");EnsureUniqueIdentity(c,tx,product);if(product.UpdatedUtc!=old.UpdatedUtc)throw new InvalidOperationException("Ürün başka bir işlemde güncellendi. Yenileyip tekrar düzenleyin.");if(product.Price!=old.Price||product.Currency!=old.Currency){product.FormulaPriceTry=null;product.AppliedTryRate=null;product.FxRateDate=null;}product.UpdatedUtc=DateTime.UtcNow;Put(c,"CatalogProducts",product.Id,product,tx);tx.Commit();}
 static void EnsureUniqueIdentity(SqliteConnection c,SqliteTransaction tx,CatalogProduct product){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT Json FROM CatalogProducts WHERE Id<>$id AND ((json_extract(Json,'$.Sku')=$sku AND $sku<>'') OR (json_extract(Json,'$.Barcode')=$barcode AND $barcode<>'')) LIMIT 1";cmd.Parameters.AddWithValue("$id",product.Id);cmd.Parameters.AddWithValue("$sku",product.Sku.Trim());cmd.Parameters.AddWithValue("$barcode",product.Barcode.Trim());if(cmd.ExecuteScalar() is string)throw new InvalidOperationException("SKU veya barkod başka bir üründe zaten kayıtlı.");}
 public void DeleteProduct(CatalogProduct product){using var c=Open();using var tx=c.BeginTransaction();using var find=c.CreateCommand();find.Transaction=tx;find.CommandText="SELECT Json FROM CatalogProducts WHERE Id=$id";find.Parameters.AddWithValue("$id",product.Id);var json=find.ExecuteScalar() as string??throw new InvalidOperationException("Ürün bulunamadı.");var old=JsonSerializer.Deserialize<CatalogProduct>(json)!;if(old.UpdatedUtc!=product.UpdatedUtc)throw new InvalidOperationException("Ürün değişti; yenileyip tekrar deneyin.");if(old.EtsyCreationAttempted||!string.IsNullOrEmpty(old.EtsyListingId))throw new InvalidOperationException("Etsy bağlantısı veya gönderim kaydı olan ürünü silmek yerine pasife alın.");using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="DELETE FROM CatalogProducts WHERE Id=$id";cmd.Parameters.AddWithValue("$id",product.Id);cmd.ExecuteNonQuery();tx.Commit();}
 static void Index(Dictionary<string,List<CatalogProduct>> index,string key,CatalogProduct product){if(key=="")return;if(!index.TryGetValue(key,out var values))index[key]=values=new();values.Add(product);}
 public ImportSummary Import(XmlSource source,IReadOnlyList<CatalogProduct> incoming)
 {
  ImportSummary? result=null;
  XmlSourceExecutionGate.RunAsync(source.Id,()=>{result=ImportCore(source,incoming);return Task.CompletedTask;}).GetAwaiter().GetResult();
  return result!;
 }
 internal ImportSummary ImportCore(XmlSource source,IReadOnlyList<CatalogProduct> incoming)
 {
  XmlCatalog.ValidateSource(source);using var c=Open();using var tx=c.BeginTransaction();var products=Read<CatalogProduct>(c,"CatalogProducts",tx);int added=0,updated=0,unchanged=0;var skus=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var bars=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var touched=new HashSet<string>();var skuIndex=new Dictionary<string,List<CatalogProduct>>(StringComparer.OrdinalIgnoreCase);var barcodeIndex=new Dictionary<string,List<CatalogProduct>>(StringComparer.OrdinalIgnoreCase);foreach(var p in products.Where(p=>p.SourceId==source.Id)){Index(skuIndex,p.Sku,p);Index(barcodeIndex,p.Barcode,p);}
  foreach(var row in incoming){Valid(row);if(row.SourceId!=source.Id)throw new InvalidOperationException("Kaynak kimliği uyuşmuyor.");if((row.Sku!=""&&!skus.Add(row.Sku))||(row.Barcode!=""&&!bars.Add(row.Barcode)))throw new InvalidOperationException("Yinelenen SKU veya barkod; aktarım iptal edildi.");var matches=skuIndex.GetValueOrDefault(row.Sku)??new List<CatalogProduct>();if(matches.Count==0&&row.Barcode!="")matches=barcodeIndex.GetValueOrDefault(row.Barcode)??new List<CatalogProduct>();var barcodeMatches=barcodeIndex.GetValueOrDefault(row.Barcode)??new List<CatalogProduct>();if(matches.Count==1&&barcodeMatches.Any(p=>p.Id!=matches[0].Id))throw new InvalidOperationException("SKU ve barkod farklı ürünlerle eşleşiyor.");if(matches.Count>1)throw new InvalidOperationException("Barkod veya SKU birden fazla ürünle eşleşiyor.");
   var old=matches.SingleOrDefault();if(old==null){var p=JsonSerializer.Deserialize<CatalogProduct>(JsonSerializer.Serialize(row))!;p.Id=Guid.NewGuid().ToString("N");p.UpdatedUtc=DateTime.UtcNow;products.Add(p);Index(skuIndex,p.Sku,p);Index(barcodeIndex,p.Barcode,p);Put(c,"CatalogProducts",p.Id,p,tx);touched.Add(p.Id);added++;continue;}
   if(!touched.Add(old.Id))throw new InvalidOperationException("Birden fazla satır aynı ürüne eşleşiyor.");var before=JsonSerializer.Serialize(old);old.SourceKind="xml";old.SourceId=source.Id;old.SourceUpdatedUtc=DateTime.UtcNow;if(source.Fields.ContainsKey("Gtin"))old.Gtin=row.Gtin;old.Cost=row.Cost;old.CostCurrency=row.CostCurrency;if(!old.LockPrice){old.Price=row.Price;old.Currency=row.Currency;old.PriceSource="xml";old.FormulaPriceTry=row.FormulaPriceTry;old.AppliedTryRate=row.AppliedTryRate;old.FxRateDate=row.FxRateDate;}if(!old.LockStock){old.Stock=row.Stock;old.StockSource="xml";}if(source.UpdateName&&!old.LockName)old.Name=row.Name;if(source.UpdateDescription&&!old.LockDescription)old.Description=row.Description;if(source.UpdateImages&&!old.LockImages){old.ImageUrls=row.ImageUrls;old.MediaSource="xml";}
   if(before==JsonSerializer.Serialize(old)){unchanged++;continue;}old.UpdatedUtc=DateTime.UtcNow;Put(c,"CatalogProducts",old.Id,old,tx);updated++;
  }tx.Commit();return new(added,updated,unchanged);
 }
 public CatalogUndoReceipt ImportWithUndo(XmlSource source,IReadOnlyList<CatalogProduct> incoming){var before=Products().Select(p=>JsonSerializer.Deserialize<CatalogProduct>(JsonSerializer.Serialize(p))!).ToList();ImportSummary summary=Import(source,incoming);var after=Products().Select(p=>JsonSerializer.Deserialize<CatalogProduct>(JsonSerializer.Serialize(p))!).ToList();return new(Guid.NewGuid().ToString("N"),before,after);}
 public void Undo(CatalogUndoReceipt receipt){using var c=Open();using var tx=c.BeginTransaction();var current=Read<CatalogProduct>(c,"CatalogProducts",tx);var expected=JsonSerializer.Serialize(receipt.After.OrderBy(p=>p.Id));var actual=JsonSerializer.Serialize(current.OrderBy(p=>p.Id));if(expected!=actual)throw new InvalidOperationException("Katalog bu içe aktarmadan sonra değişti; geri alma güvenlik nedeniyle durduruldu.");using(var delete=c.CreateCommand()){delete.Transaction=tx;delete.CommandText="DELETE FROM CatalogProducts";delete.ExecuteNonQuery();}foreach(var p in receipt.Before)Put(c,"CatalogProducts",p.Id,p,tx);tx.Commit();}
}











