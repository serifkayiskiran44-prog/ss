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
  filter??=new(); filter.ValidateRanges();
  foreach(var values in new[]{filter.Brands,filter.Categories,filter.Skus,filter.SourceIds})if(values.Length>100||values.Sum(v=>v.Length)>6000)throw new ArgumentException("Filtre en fazla 100 değer ve 6000 karakter olabilir.");
  using var c=Open();using var tx=c.BeginTransaction(deferred:true);
  // Substring search cannot use a B-tree index; filter/count in SQLite and deserialize only the requested page.
  // json_valid(Json)=1 keeps a corrupt row out of every scan below - sqlite's json1
  // functions throw a runtime error on malformed JSON, which would otherwise crash
  // this whole query for every row, not just the corrupt one; see CorruptProducts().
  var where=" WHERE json_valid(Json)=1 AND ($all=1 OR json_extract(Json,'$.Name') LIKE $q ESCAPE '\\' OR json_extract(Json,'$.Sku') LIKE $q ESCAPE '\\' OR json_extract(Json,'$.Barcode') LIKE $q ESCAPE '\\' OR json_extract(Json,'$.Brand') LIKE $q ESCAPE '\\' OR json_extract(Json,'$.Category') LIKE $q ESCAPE '\\'";
  where += ") AND ($active=-1 OR COALESCE(json_extract(Json,'$.Active'),1)=$active) AND ($description=-1 OR (length(trim(COALESCE(json_extract(Json,'$.Description'),'')))>0)=$description) AND ($image=-1 OR (length(trim(COALESCE(json_extract(Json,'$.ImageUrls'),'')))>0)=$image)";
  var extra=new Dictionary<string,string>();
  foreach(var (field,values) in new[]{("Brand",filter.Brands),("Category",filter.Categories),("Sku",filter.Skus),("Barcode",filter.Barcodes)}){
   if(values.Length==0)continue;
   var names=new List<string>();foreach(var value in values){var clean=value.Trim();if(clean.Length==0)continue;var key="$filter"+extra.Count;extra.Add(key,clean);names.Add(key);}if(names.Count==0)continue;
   where+=$" AND json_extract(Json,'$.{field}') COLLATE NOCASE IN ({string.Join(",",names)})";
  }
  void Numeric(string field, object? value, string comparison){if(value==null)return;var key="$range"+extra.Count;extra.Add(key,Convert.ToString(value,System.Globalization.CultureInfo.InvariantCulture)!);where+=$" AND COALESCE(json_extract(Json,'$.{field}'),0){comparison}CAST({key} AS REAL)";}
  Numeric("Stock",filter.MinimumStock,">=");Numeric("Stock",filter.MaximumStock,"<=");Numeric("Price",filter.MinimumPrice,">=");Numeric("Price",filter.MaximumPrice,"<=");Numeric("LocalNumber",filter.MinimumId,">=");Numeric("LocalNumber",filter.MaximumId,"<=");
  Numeric("LockPrice",filter.PriceLocked.HasValue?filter.PriceLocked.Value?1:0:null,"=");Numeric("LockStock",filter.StockLocked.HasValue?filter.StockLocked.Value?1:0:null,"=");
  if(filter.Currency.Length>0){extra.Add("$currency",filter.Currency);where+=" AND json_extract(Json,'$.Currency') COLLATE NOCASE=$currency";}
  if(filter.CategoryPrefix.Length>0){var prefix=filter.CategoryPrefix.Replace(" > ",">").Trim();extra.Add("$categoryRoot",prefix);extra.Add("$categoryChild",prefix.Replace("\\","\\\\").Replace("%","\\%").Replace("_","\\_")+">%");where+=" AND (replace(json_extract(Json,'$.Category'),' > ','>') COLLATE NOCASE=$categoryRoot OR replace(json_extract(Json,'$.Category'),' > ','>') LIKE $categoryChild ESCAPE '\\')";}
  if(filter.SourceIds.Length>0){
   // A manual product's SourceId is "" (see CreateManual), so a plain non-empty
   // value filter can never select "manual only" - the reserved ManualSource token
   // maps to an explicit empty-SourceId match instead of being treated as blank/skip.
   var sourceClauses=new List<string>();var sourceIds=new List<string>();
   foreach(var value in filter.SourceIds){
    if(value==CatalogFilter.ManualSource){sourceClauses.Add("COALESCE(json_extract(Json,'$.SourceId'),'')=''");continue;}
    var clean=value.Trim();if(clean.Length==0)continue;var key="$filter"+extra.Count;extra.Add(key,clean);sourceIds.Add(key);
   }
   if(sourceIds.Count>0)sourceClauses.Add($"json_extract(Json,'$.SourceId') COLLATE NOCASE IN ({string.Join(",",sourceIds)})");
   if(sourceClauses.Count>0)where+=" AND ("+string.Join(" OR ",sourceClauses)+")";
  }
  if(filter.DuplicateIdentityOnly){
   var duplicateIds=DuplicateIdentityIds(c,tx);
   if(duplicateIds.Count==0)where+=" AND 0";
   else{var keys=new List<string>();foreach(var id in duplicateIds){var key="$dup"+extra.Count;extra.Add(key,id);keys.Add(key);}where+=$" AND Id IN ({string.Join(",",keys)})";}
  }
  search=search.Trim();var pattern="%"+search.Replace("\\","\\\\").Replace("%","\\%").Replace("_","\\_")+"%";
  void Params(SqliteCommand cmd){cmd.Transaction=tx;cmd.Parameters.AddWithValue("$all",search.Length==0?1:0);cmd.Parameters.AddWithValue("$q",pattern);cmd.Parameters.AddWithValue("$active",filter.Active.HasValue?(filter.Active.Value?1:0):-1);cmd.Parameters.AddWithValue("$description",filter.DescriptionPresent.HasValue?(filter.DescriptionPresent.Value?1:0):-1);cmd.Parameters.AddWithValue("$image",filter.ImagePresent.HasValue?(filter.ImagePresent.Value?1:0):-1);foreach(var pair in extra)cmd.Parameters.AddWithValue(pair.Key,pair.Value);}
  int total,inStock,linked;
  using(var count=c.CreateCommand()){cancellationToken.ThrowIfCancellationRequested();Params(count);count.CommandText="SELECT COUNT(*),COALESCE(SUM(CASE WHEN json_extract(Json,'$.Stock')>0 THEN 1 ELSE 0 END),0),COALESCE(SUM(CASE WHEN COALESCE(json_extract(Json,'$.EtsyListingId'),'')<>'' THEN 1 ELSE 0 END),0) FROM CatalogProducts"+where;using var reader=count.ExecuteReader();reader.Read();total=reader.GetInt32(0);inStock=reader.GetInt32(1);linked=reader.GetInt32(2);}
  var items=new List<CatalogProduct>();using(var page=c.CreateCommand()){cancellationToken.ThrowIfCancellationRequested();Params(page);page.CommandText="SELECT Id,Json FROM CatalogProducts"+where+" ORDER BY json_extract(Json,'$.Name') COLLATE NOCASE,Id LIMIT $limit OFFSET $offset";page.Parameters.AddWithValue("$limit",limit);page.Parameters.AddWithValue("$offset",offset);using var reader=page.ExecuteReader();while(reader.Read()){cancellationToken.ThrowIfCancellationRequested();if(TryDeserializeProduct(reader.GetString(0),reader.GetString(1),out var p,out _))items.Add(p!);}}
  tx.Commit();return new(items,total,inStock,linked);
 }
 public CatalogStore(string? directory=null){directory??=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MonoBridgeDesktop");Directory.CreateDirectory(directory);connectionString=new SqliteConnectionStringBuilder{DataSource=System.IO.Path.Combine(directory,"catalog.db"),DefaultTimeout=15,Pooling=true}.ToString();using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="PRAGMA journal_mode=WAL;PRAGMA synchronous=NORMAL;PRAGMA busy_timeout=15000;CREATE TABLE IF NOT EXISTS Sources(Id TEXT PRIMARY KEY, Json TEXT NOT NULL);CREATE TABLE IF NOT EXISTS CatalogProducts(Id TEXT PRIMARY KEY, Json TEXT NOT NULL);CREATE INDEX IF NOT EXISTS IX_CatalogProducts_Sku ON CatalogProducts(json_extract(Json,'$.Sku') COLLATE NOCASE);CREATE INDEX IF NOT EXISTS IX_CatalogProducts_Barcode ON CatalogProducts(json_extract(Json,'$.Barcode') COLLATE NOCASE);CREATE INDEX IF NOT EXISTS IX_CatalogProducts_Brand ON CatalogProducts(json_extract(Json,'$.Brand') COLLATE NOCASE);CREATE INDEX IF NOT EXISTS IX_CatalogProducts_Category ON CatalogProducts(json_extract(Json,'$.Category') COLLATE NOCASE);";cmd.ExecuteNonQuery();InitializeProductSourceBindings(c);InventoryLedger.Initialize(c);InitializeOrderStock(c);InitializeStockPolicies(c);InitializePricePolicies(c);SchemaVersion.Ensure(c);InitializeSequenceIds(c);}
 SqliteConnection Open(){var c=new SqliteConnection(connectionString);c.Open();return c;}
 static List<T> Read<T>(SqliteConnection c,string table,SqliteTransaction? tx=null){using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=$"SELECT Json FROM {table}";using var r=cmd.ExecuteReader();var list=new List<T>();while(r.Read())list.Add(JsonSerializer.Deserialize<T>(r.GetString(0))!);return list;}
 internal static void Put<T>(SqliteConnection c,string table,string id,T value,SqliteTransaction? tx=null){if(table=="CatalogProducts"&&value is CatalogProduct product)AssignSequenceIds(c,tx,product);using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText=$"INSERT INTO {table}(Id,Json) VALUES($id,$json) ON CONFLICT(Id) DO UPDATE SET Json=excluded.Json";cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(value));cmd.ExecuteNonQuery();if(table=="CatalogProducts"&&value is CatalogProduct savedProduct)InventoryLedger.SyncOnlineBalance(c,tx,savedProduct);}
 public List<XmlSource> Sources(){using var c=Open();return Read<XmlSource>(c,"Sources");}
 public void SaveSource(XmlSource source){XmlCatalog.ValidateSource(source);if(string.IsNullOrWhiteSpace(source.Id))throw new InvalidOperationException("Kaynak kimliği boş.");using var c=Open();Put(c,"Sources",source.Id,source);}
 public bool TryRecordSourceRun(string sourceId,DateTime lastRunUtc,string lastStatus){if(string.IsNullOrWhiteSpace(sourceId))throw new ArgumentException("Kaynak kimliği gerekli.",nameof(sourceId));using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="UPDATE Sources SET Json=json_set(Json,'$.LastRunUtc',$lastRun,'$.LastStatus',$status) WHERE Id=$id AND json_valid(Json)=1 AND COALESCE(json_extract(Json,'$.Enabled'),1)=1";cmd.Parameters.AddWithValue("$lastRun",lastRunUtc.ToUniversalTime().ToString("O",System.Globalization.CultureInfo.InvariantCulture));cmd.Parameters.AddWithValue("$status",lastStatus??"");cmd.Parameters.AddWithValue("$id",sourceId);return cmd.ExecuteNonQuery()==1;}
 public static string SourceConfigRevision(XmlSource source)
 {
  ArgumentNullException.ThrowIfNull(source);
  var snapshot=JsonSerializer.Deserialize<XmlSource>(JsonSerializer.Serialize(source))!;
  snapshot.LastRunUtc=null;
  snapshot.LastStatus="";
  snapshot.Fields=snapshot.Fields.OrderBy(pair=>pair.Key,StringComparer.Ordinal).ToDictionary(pair=>pair.Key,pair=>pair.Value,StringComparer.Ordinal);
  snapshot.CategoryRules=snapshot.CategoryRules.Where(rule=>rule.Enabled==false||!string.Equals(rule.XmlCategory.Trim(),rule.TargetCategory.Trim(),StringComparison.OrdinalIgnoreCase)||rule.Prices.Values.Any(formula=>!string.IsNullOrWhiteSpace(formula.SaleFormula)||!string.IsNullOrWhiteSpace(formula.ListFormula))).ToList();
  foreach(var rule in snapshot.CategoryRules)rule.Prices=rule.Prices.OrderBy(pair=>pair.Key,StringComparer.Ordinal).ToDictionary(pair=>pair.Key,pair=>pair.Value,StringComparer.Ordinal);
  if(snapshot.AutoFx)
  {
   snapshot.TryPerTargetUnit=0;
   snapshot.FxRateDate=null;
   snapshot.FxFetchedUtc=null;
  }
  return JsonSerializer.Serialize(snapshot);
 }
 public bool TryRecordAutoFxQuote(string sourceId,string expectedRevision,FxQuote quote)
 {
  if(string.IsNullOrWhiteSpace(sourceId))throw new ArgumentException("Kaynak kimliği gerekli.",nameof(sourceId));
  if(string.IsNullOrWhiteSpace(expectedRevision))throw new ArgumentException("Kaynak revizyonu gerekli.",nameof(expectedRevision));
  ArgumentNullException.ThrowIfNull(quote);
  using var c=Open();using var tx=c.BeginTransaction(System.Data.IsolationLevel.Serializable);
  using var find=c.CreateCommand();find.Transaction=tx;find.CommandText="SELECT Json FROM Sources WHERE Id=$id";find.Parameters.AddWithValue("$id",sourceId);
  var json=find.ExecuteScalar() as string;
  if(json is null)return false;
  XmlSource persisted;
  try{persisted=JsonSerializer.Deserialize<XmlSource>(json)??throw new JsonException();}
  catch(JsonException){return false;}
  if(!persisted.Enabled||!persisted.AutoFx||!string.Equals(persisted.Currency,quote.Currency,StringComparison.OrdinalIgnoreCase)||!string.Equals(persisted.FxKind,quote.Kind,StringComparison.Ordinal)||SourceConfigRevision(persisted)!=expectedRevision)return false;
  using var update=c.CreateCommand();update.Transaction=tx;update.CommandText="UPDATE Sources SET Json=json_set(Json,'$.TryPerTargetUnit',CAST($rate AS REAL),'$.FxRateDate',$rateDate,'$.FxFetchedUtc',$fetched) WHERE Id=$id AND json_valid(Json)=1 AND COALESCE(json_extract(Json,'$.Enabled'),1)=1 AND COALESCE(json_extract(Json,'$.AutoFx'),0)=1";
  update.Parameters.AddWithValue("$rate",quote.TryPerUnit);
  update.Parameters.AddWithValue("$rateDate",quote.RateDate.ToUniversalTime().ToString("O",System.Globalization.CultureInfo.InvariantCulture));
  update.Parameters.AddWithValue("$fetched",quote.FetchedUtc.ToUniversalTime().ToString("O",System.Globalization.CultureInfo.InvariantCulture));
  update.Parameters.AddWithValue("$id",sourceId);
  if(update.ExecuteNonQuery()!=1)return false;
  tx.Commit();
  return true;
 }
 /// Gives a supplier feed a stable, human-readable SKU namespace without changing
 /// the immutable central product Id. The transaction either updates every row of
 /// that source or leaves all identities untouched.
 public int PrefixSourceSkus(string sourceId,string prefix)
 {
  if(string.IsNullOrWhiteSpace(sourceId)||string.IsNullOrWhiteSpace(prefix))throw new ArgumentException("Kaynak ve SKU öneki gerekli.");
  using var c=Open();using var tx=c.BeginTransaction();var (all,corrupt)=ReadProductsSafe(c,tx);if(corrupt.Count>0)throw new InvalidOperationException("Bozuk katalog kaydı varken SKU dönüştürme yapılamaz.");
  var target=all.Where(p=>p.SourceId==sourceId&&!p.Sku.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)).ToList();
  var taken=new HashSet<string>(all.Except(target).Select(p=>NormalizeIdentityKey(p.Sku)).Where(x=>x.Length>0),StringComparer.OrdinalIgnoreCase);
  foreach(var product in target){var next=prefix+product.Sku;if(next.Length>128||!taken.Add(NormalizeIdentityKey(next)))throw new InvalidOperationException("SKU öneki benzersiz kimlik üretemedi; katalog değiştirilmedi.");product.Sku=next;product.UpdatedUtc=DateTime.UtcNow;Put(c,"CatalogProducts",product.Id,product,tx);}
  tx.Commit();return target.Count;
 }
 /// A malformed/truncated/oversized CatalogProducts row is a real possibility (manual
 /// DB edits, disk corruption, a future field a downgraded build can't parse). Products()
 /// and every SQL scan below must keep working on the healthy rows around it rather than
 /// throwing catalog-wide - a corrupt row surfaces only through CorruptProducts(), stays
 /// in the table untouched (detection re-runs from the row's own bytes every time, so the
 /// review state naturally survives restart), and is never silently deleted or overwritten.
 public const int MaxProductJsonBytes=1_000_000;
 public sealed record CorruptProductRow(string Id,string Reason,int PayloadLength,DateTime DetectedUtc);
 static bool TryDeserializeProduct(string id,string json,out CatalogProduct? product,out CorruptProductRow? corrupt)
 {
  product=null;corrupt=null;
  if(json.Length>MaxProductJsonBytes){corrupt=new(id,"Oversized payload",json.Length,DateTime.UtcNow);return false;}
  try{product=JsonSerializer.Deserialize<CatalogProduct>(json);}
  catch(Exception ex)when(ex is JsonException or FormatException or ArgumentException){corrupt=new(id,"Malformed JSON: "+ex.GetType().Name,json.Length,DateTime.UtcNow);return false;}
  if(product is null){corrupt=new(id,"Empty or null document",json.Length,DateTime.UtcNow);return false;}
  if(string.IsNullOrEmpty(product.Id)){corrupt=new(id,"Missing required Id field",json.Length,DateTime.UtcNow);return false;}
  return true;
 }
 static (List<CatalogProduct> Healthy,List<CorruptProductRow> Corrupt) ReadProductsSafe(SqliteConnection c,SqliteTransaction? tx=null)
 {
  using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT Id,Json FROM CatalogProducts";using var r=cmd.ExecuteReader();
  var healthy=new List<CatalogProduct>();var corrupt=new List<CorruptProductRow>();
  while(r.Read()){var id=r.GetString(0);var json=r.GetString(1);if(TryDeserializeProduct(id,json,out var p,out var bad))healthy.Add(p!);else corrupt.Add(bad!);}
  return (healthy,corrupt);
 }
 /// Bounded diagnostics only (row id, a short reason, byte length, detection time) -
 /// never the raw product JSON, so a corrupt row can never leak into routine logs/UI.
 public IReadOnlyList<CorruptProductRow> CorruptProducts(){using var c=Open();return ReadProductsSafe(c).Corrupt;}
 /// Explicit, transactional recovery action: re-validates the row is still corrupt at
 /// delete time (it may have been fixed/replaced since the caller last listed it) so a
 /// row that became healthy in the meantime is never silently discarded.
 public void DeleteCorruptRow(string id)
 {
  using var c=Open();using var tx=c.BeginTransaction();
  using var find=c.CreateCommand();find.Transaction=tx;find.CommandText="SELECT Json FROM CatalogProducts WHERE Id=$id";find.Parameters.AddWithValue("$id",id);
  var json=find.ExecuteScalar() as string??throw new InvalidOperationException("Kayıt bulunamadı.");
  if(TryDeserializeProduct(id,json,out _,out _))throw new InvalidOperationException("Bu kayıt bozuk değil; normal ürün silme akışını kullanın.");
  InventoryLedger.DeleteCatalogProduct(c,tx,id);tx.Commit();
 }
 public List<CatalogProduct> Products(string search=""){using var c=Open();return ReadProductsSafe(c).Healthy.Where(p=>search==""||new[]{p.Name,p.Sku,p.Barcode,p.Brand,p.Category}.Any(v=>v.Contains(search,StringComparison.OrdinalIgnoreCase))).ToList();}
 static void Valid(CatalogProduct p){
  if(p.SourceProductId==null||p.SourceProductId.Length>128||p.XmlAttributes==null||p.XmlAttributes.Count>100||p.XmlAttributes.Any(a=>a.Key.Length>100||a.Value==null||a.Value.Length>4000))throw new InvalidOperationException("XML kimliği veya ek alan sınırı aşıldı.");
  if(p.Mpn==null||p.Mpn.Length>128||p.InvoiceName==null||p.InvoiceName.Length>300||p.Subtitle==null||p.Subtitle.Length>300||p.Shelf==null||p.Shelf.Length>100)throw new InvalidOperationException("MPN en fazla 128, fatura adı/alt başlık 300, raf 100 karakter olabilir.");
  if(p.Name.Length>500||p.Sku.Length>128||p.Barcode.Length>64||p.Brand.Length>200||p.Category.Length>200||p.Description.Length>20000)throw new InvalidOperationException("Ürün metin alanlarından biri izin verilen uzunluğu aşıyor.");
  // Active currency registry: an unrecognized code is rejected explicitly, never
  // silently coerced to TRY or any other default.
  if(!TrMarketplaceHubDesktop.LocaleSettings.SupportedCurrencies.Contains(p.Currency.Trim()))throw new InvalidOperationException($"Desteklenmeyen döviz kodu: {p.Currency}. TRY, USD, EUR veya GBP kullanın.");
  p.Currency=p.Currency.Trim().ToUpperInvariant();
  if(string.IsNullOrWhiteSpace(p.Name)||(string.IsNullOrWhiteSpace(p.Sku)&&string.IsNullOrWhiteSpace(p.Barcode))||p.Price<0||p.Cost<0||p.Stock<0||p.VatRate<0||p.VatRate>100)throw new InvalidOperationException("Ürün adı, kimliği, fiyatı, stoku veya KDV oranı geçersiz.");
 }
 public void SaveProduct(CatalogProduct product){Valid(product);using var c=Open();using var tx=c.BeginTransaction();using var find=c.CreateCommand();find.Transaction=tx;find.CommandText="SELECT Json FROM CatalogProducts WHERE Id=$id";find.Parameters.AddWithValue("$id",product.Id);var json=find.ExecuteScalar() as string??throw new InvalidOperationException("Ürün bulunamadı.");if(!TryDeserializeProduct(product.Id,json,out var old,out _))throw new InvalidOperationException("Bu ürün kaydı bozuk (REVIEW_REQUIRED); önce kurtarma veya silme işlemi yapılmalı.");if(old!.SourceId!=product.SourceId||old.Sku!=product.Sku||old.Barcode!=product.Barcode)throw new InvalidOperationException("Ürün kimliği elle değiştirilemez.");EnsureUniqueIdentity(c,tx,product);if(product.UpdatedUtc!=old.UpdatedUtc)throw new InvalidOperationException("Ürün başka bir işlemde güncellendi. Yenileyip tekrar düzenleyin.");if(product.Price!=old.Price||product.Currency!=old.Currency){product.FormulaPriceTry=null;product.AppliedTryRate=null;product.FxRateDate=null;}product.UpdatedUtc=DateTime.UtcNow;Put(c,"CatalogProducts",product.Id,product,tx);tx.Commit();}
 public CatalogUndoReceipt ApplyExcelUpdatesWithUndo(IReadOnlyList<CatalogProduct> rows,IReadOnlyCollection<string>? selectedFields=null)
 {
  if(rows.Count==0)throw new InvalidOperationException("Uygulanacak Excel satırı yok.");
  var before=Products().Select(CloneProduct).ToList();using var c=Open();using var tx=c.BeginTransaction();var (all,corrupt)=ReadProductsSafe(c,tx);if(corrupt.Count>0)throw new InvalidOperationException("Bozuk katalog kaydı varken Excel güncellemesi yapılamaz.");var byId=all.ToDictionary(p=>p.Id,StringComparer.OrdinalIgnoreCase);var used=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  foreach(var row in rows){if(string.IsNullOrWhiteSpace(row.Id)||!used.Add(row.Id)||!byId.TryGetValue(row.Id,out var old))throw new InvalidOperationException("Excel satırlarında geçerli ve benzersiz Ürün ID zorunludur.");if(!string.Equals(old.Sku,row.Sku,StringComparison.OrdinalIgnoreCase)||!string.Equals(old.Barcode,row.Barcode,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Excel SKU veya barkod değişikliği yapamaz; ürün kimliği korunur.");var proposed=selectedFields is null?CloneProduct(row):ExcelFieldUpdatePolicy.Merge(old,row,selectedFields);proposed.Id=old.Id;proposed.Sku=old.Sku;proposed.Barcode=old.Barcode;proposed.PriceSource="excel";proposed.StockSource="excel";proposed.MediaSource="excel";proposed.UpdatedUtc=DateTime.UtcNow;Valid(proposed);Put(c,"CatalogProducts",proposed.Id,proposed,tx);}
  tx.Commit();var after=Products().Select(CloneProduct).ToList();return new(Guid.NewGuid().ToString("N"),before,after);
 }
 static CatalogProduct CloneProduct(CatalogProduct product)=>JsonSerializer.Deserialize<CatalogProduct>(JsonSerializer.Serialize(product))!;
 // SQLite's default text comparison (and its ASCII-only lower()/NOCASE) misses
 // non-ASCII case collisions (e.g. Turkish "Ş" vs "ş") and whitespace-only
 // differences, so identity/duplicate detection here compares in .NET on a
 // normalized key instead of matching raw JSON text in SQL.
 public static string NormalizeIdentityKey(string value)=>(value??"").Trim().ToUpperInvariant();
 /// Ids of every product whose normalized SKU or barcode collides with at least one
 /// other product's - a read-only review set, never used to auto-merge/delete.
 static HashSet<string> DuplicateIdentityIds(SqliteConnection c,SqliteTransaction tx){
  using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT Id,json_extract(Json,'$.Sku'),json_extract(Json,'$.Barcode') FROM CatalogProducts WHERE json_valid(Json)=1";
  using var r=cmd.ExecuteReader();
  var skuGroups=new Dictionary<string,List<string>>();var barcodeGroups=new Dictionary<string,List<string>>();
  while(r.Read()){
   var id=r.GetString(0);var sku=NormalizeIdentityKey(r.IsDBNull(1)?"":r.GetString(1));var barcode=NormalizeIdentityKey(r.IsDBNull(2)?"":r.GetString(2));
   if(sku.Length>0){if(!skuGroups.TryGetValue(sku,out var list))skuGroups[sku]=list=new();list.Add(id);}
   if(barcode.Length>0){if(!barcodeGroups.TryGetValue(barcode,out var list2))barcodeGroups[barcode]=list2=new();list2.Add(id);}
  }
  var result=new HashSet<string>();
  foreach(var group in skuGroups.Values)if(group.Count>1)foreach(var id in group)result.Add(id);
  foreach(var group in barcodeGroups.Values)if(group.Count>1)foreach(var id in group)result.Add(id);
  return result;
 }
 static void EnsureUniqueIdentity(SqliteConnection c,SqliteTransaction tx,CatalogProduct product){
  var sku=NormalizeIdentityKey(product.Sku);var barcode=NormalizeIdentityKey(product.Barcode);
  if(sku.Length==0&&barcode.Length==0)return;
  using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT Id,json_extract(Json,'$.Sku'),json_extract(Json,'$.Barcode') FROM CatalogProducts WHERE Id<>$id AND json_valid(Json)=1";cmd.Parameters.AddWithValue("$id",product.Id);
  using var r=cmd.ExecuteReader();
  while(r.Read()){
   var existingSku=NormalizeIdentityKey(r.IsDBNull(1)?"":r.GetString(1));var existingBarcode=NormalizeIdentityKey(r.IsDBNull(2)?"":r.GetString(2));
   if((sku.Length>0&&sku==existingSku)||(barcode.Length>0&&barcode==existingBarcode))throw new InvalidOperationException("SKU veya barkod (büyük/küçük harf ve boşluk farkı gözetmeksizin) başka bir üründe zaten kayıtlı.");
  }
 }
 public sealed record ManualProductCollision(string ExistingId,string ExistingSku,string ExistingBarcode,string Field);
 /// Read-only collision check callers can run before CreateManual, so the user sees
 /// which existing product a normalized SKU/barcode would collide with (case,
 /// whitespace, or exact) without committing anything or destructively rewriting
 /// their input (Turkish characters and leading zeros are preserved as typed).
 public ManualProductCollision? PreviewIdentityCollision(string sku,string barcode){
  var skuKey=NormalizeIdentityKey(sku);var barcodeKey=NormalizeIdentityKey(barcode);
  if(skuKey.Length==0&&barcodeKey.Length==0)return null;
  foreach(var p in Products()){
   var existingSku=NormalizeIdentityKey(p.Sku);var existingBarcode=NormalizeIdentityKey(p.Barcode);
   if(skuKey.Length>0&&skuKey==existingSku)return new(p.Id,p.Sku,p.Barcode,"Sku");
   if(barcodeKey.Length>0&&barcodeKey==existingBarcode)return new(p.Id,p.Sku,p.Barcode,"Barcode");
  }
  return null;
 }
 /// Transactional create entry point independent of XML import: assigns a fresh Id
 /// and MANUAL provenance itself (callers never set product identity), validates
 /// and rejects a normalized-key collision before insert - nothing is silently
 /// auto-corrected (SKU/barcode text is stored exactly as given, only trimmed).
 public CatalogProduct CreateManual(CatalogProduct product){
  if(!string.IsNullOrWhiteSpace(product.Sku)&&product.Sku.Any(char.IsControl))throw new InvalidOperationException("SKU kontrol karakteri içeremez.");
  if(!string.IsNullOrWhiteSpace(product.Barcode)&&product.Barcode.Any(char.IsControl))throw new InvalidOperationException("Barkod kontrol karakteri içeremez.");
  product.Sku=product.Sku.Trim();product.Barcode=product.Barcode.Trim();
  Valid(product);
  product.Id=Guid.NewGuid().ToString("N");product.SourceId="";product.SourceKind="manual";product.PriceSource="manual";product.StockSource="manual";product.MediaSource="manual";product.SourceUpdatedUtc=null;product.UpdatedUtc=DateTime.UtcNow;
  using var c=Open();using var tx=c.BeginTransaction();
  EnsureUniqueIdentity(c,tx,product);
  Put(c,"CatalogProducts",product.Id,product,tx);
  tx.Commit();
  return product;
 }
 public void DeleteProduct(CatalogProduct product){using var c=Open();using var tx=c.BeginTransaction();using var find=c.CreateCommand();find.Transaction=tx;find.CommandText="SELECT Json FROM CatalogProducts WHERE Id=$id";find.Parameters.AddWithValue("$id",product.Id);var json=find.ExecuteScalar() as string??throw new InvalidOperationException("Ürün bulunamadı.");if(!TryDeserializeProduct(product.Id,json,out var old,out _))throw new InvalidOperationException("Bu ürün kaydı bozuk (REVIEW_REQUIRED); silmek için kurtarma akışını kullanın.");if(old!.UpdatedUtc!=product.UpdatedUtc)throw new InvalidOperationException("Ürün değişti; yenileyip tekrar deneyin.");if(old.EtsyCreationAttempted||!string.IsNullOrEmpty(old.EtsyListingId))throw new InvalidOperationException("Etsy bağlantısı veya gönderim kaydı olan ürünü silmek yerine pasife alın.");InventoryLedger.DeleteCatalogProduct(c,tx,product.Id);ProductAssetCache.QueueDeletion(c,tx,product.Id);tx.Commit();new ProductAssetCache(Path.GetDirectoryName(c.DataSource)).CleanupDeletedProducts();}
 static void Index(Dictionary<string,List<CatalogProduct>> index,string key,CatalogProduct product){if(key=="")return;if(!index.TryGetValue(key,out var values))index[key]=values=new();if(!values.Any(p=>p.Id==product.Id))values.Add(product);}
 public ImportSummary Import(XmlSource source,IReadOnlyList<CatalogProduct> incoming)
 {
  ImportSummary? result=null;
  XmlSourceExecutionGate.RunAsync(source.Id,()=>{result=ImportCore(source,incoming);return Task.CompletedTask;}).GetAwaiter().GetResult();
  return result!;
 }
 public ImportSummary ImportIfSourceCurrent(XmlSource source,string expectedRevision,IReadOnlyList<CatalogProduct> incoming)
 {
  if(string.IsNullOrWhiteSpace(expectedRevision))throw new InvalidOperationException("XML önizleme revizyonu eksik; önizlemeyi yeniden hesaplayın.");
  ImportSummary? result=null;
  XmlSourceExecutionGate.RunAsync(source.Id,()=>{result=ImportIfSourceCurrentCore(source,expectedRevision,incoming);return Task.CompletedTask;}).GetAwaiter().GetResult();
  return result!;
 }
 ImportSummary ImportIfSourceCurrentCore(XmlSource source,string expectedRevision,IReadOnlyList<CatalogProduct> incoming)
 {
  XmlCatalog.ValidateSource(source);
  using var c=Open();using var tx=c.BeginTransaction(System.Data.IsolationLevel.Serializable);
  using var find=c.CreateCommand();find.Transaction=tx;find.CommandText="SELECT Json FROM Sources WHERE Id=$id";find.Parameters.AddWithValue("$id",source.Id);
  var json=find.ExecuteScalar() as string;
  if(json is null)throw new InvalidOperationException("XML kaynağı silinmiş; önizlemeyi yeniden hesaplayın.");
  XmlSource persisted;
  try{persisted=JsonSerializer.Deserialize<XmlSource>(json)??throw new JsonException();}
  catch(JsonException){throw new InvalidOperationException("XML kaynağı ayarları okunamadı; önizlemeyi yeniden hesaplayın.");}
  if(!persisted.Enabled)throw new InvalidOperationException("XML kaynağı devre dışı; önizlemeyi yeniden hesaplayın.");
  if(SourceConfigRevision(persisted)!=expectedRevision)throw new InvalidOperationException("XML kaynağı ayarları değişti; önizlemeyi yeniden hesaplayın.");
  var result=ImportCore(source,incoming,c,tx);
  tx.Commit();
  return result;
 }
 internal ImportSummary ImportCore(XmlSource source,IReadOnlyList<CatalogProduct> incoming)
 {
  using var c=Open();using var tx=c.BeginTransaction();
  var result=ImportCore(source,incoming,c,tx);
  tx.Commit();
  return result;
 }
 ImportSummary ImportCore(XmlSource source,IReadOnlyList<CatalogProduct> incoming,SqliteConnection c,SqliteTransaction tx)
 {
  XmlCatalog.ValidateSource(source);var products=ReadProductsSafe(c,tx).Healthy;int added=0,updated=0,unchanged=0;var skus=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var bars=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var touched=new HashSet<string>();var sourceProductIndex=new Dictionary<string,List<CatalogProduct>>(StringComparer.OrdinalIgnoreCase);var skuIndex=new Dictionary<string,List<CatalogProduct>>(StringComparer.OrdinalIgnoreCase);var barcodeIndex=new Dictionary<string,List<CatalogProduct>>(StringComparer.OrdinalIgnoreCase);foreach(var p in products.Where(p=>p.SourceId==source.Id)){Index(sourceProductIndex,p.SourceProductId,p);Index(skuIndex,p.Sku,p);Index(barcodeIndex,p.Barcode,p);}
  foreach(var row in incoming){Valid(row);if(row.SourceId!=source.Id)throw new InvalidOperationException("Kaynak kimliği uyuşmuyor.");if((row.Sku!=""&&!skus.Add(row.Sku))||(row.Barcode!=""&&!bars.Add(row.Barcode)))throw new InvalidOperationException("Yinelenen SKU veya barkod; aktarım iptal edildi.");var matches=skuIndex.GetValueOrDefault(row.Sku)??new List<CatalogProduct>();if(matches.Count==0&&row.Barcode!="")matches=barcodeIndex.GetValueOrDefault(row.Barcode)??new List<CatalogProduct>();var barcodeMatches=barcodeIndex.GetValueOrDefault(row.Barcode)??new List<CatalogProduct>();if(matches.Count==1&&barcodeMatches.Any(p=>p.Id!=matches[0].Id))throw new InvalidOperationException("SKU ve barkod farklı ürünlerle eşleşiyor.");if(matches.Count>1)throw new InvalidOperationException("Barkod veya SKU birden fazla ürünle eşleşiyor.");
   if(row.SourceProductId.Length>0){var idMatches=sourceProductIndex.GetValueOrDefault(row.SourceProductId)??new List<CatalogProduct>();if(idMatches.Count>1)throw new InvalidOperationException("XML ürün ID birden fazla kayıtla eşleşiyor.");if(idMatches.Count==1){if(matches.Any(p=>p.Id!=idMatches[0].Id))throw new InvalidOperationException("XML ürün ID ve SKU/barkod farklı ürünlerle eşleşiyor.");matches=idMatches;}else if(matches.Any(p=>p.SourceProductId.Length>0&&!string.Equals(p.SourceProductId,row.SourceProductId,StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("Bu SKU farklı bir XML ürün ID ile kayıtlı.");}
   var old=matches.SingleOrDefault();if(old==null){var p=JsonSerializer.Deserialize<CatalogProduct>(JsonSerializer.Serialize(row))!;p.Id=Guid.NewGuid().ToString("N");p.UpdatedUtc=DateTime.UtcNow;products.Add(p);Index(sourceProductIndex,p.SourceProductId,p);Index(skuIndex,p.Sku,p);Index(barcodeIndex,p.Barcode,p);Put(c,"CatalogProducts",p.Id,p,tx);touched.Add(p.Id);added++;continue;}
   if(!touched.Add(old.Id))throw new InvalidOperationException("Birden fazla satır aynı ürüne eşleşiyor.");var before=JsonSerializer.Serialize(old);old.SourceKind="xml";old.SourceId=source.Id;old.SourceUpdatedUtc=DateTime.UtcNow;if(source.Fields.ContainsKey("Gtin"))old.Gtin=row.Gtin;if(source.Fields.ContainsKey("Mpn"))old.Mpn=row.Mpn;if(source.Fields.ContainsKey("VatRate"))old.VatRate=row.VatRate;old.Cost=row.Cost;old.CostCurrency=row.CostCurrency;if(!old.LockPrice){old.Price=row.Price;old.Currency=row.Currency;old.PriceSource="xml";old.FormulaPriceTry=row.FormulaPriceTry;old.AppliedTryRate=row.AppliedTryRate;old.FxRateDate=row.FxRateDate;}if(!old.LockStock){old.Stock=row.Stock;old.StockSource="xml";}if(source.UpdateName&&!old.LockName)old.Name=row.Name;if(source.UpdateDescription&&!old.LockDescription)old.Description=row.Description;if(source.UpdateImages&&!old.LockImages){old.ImageUrls=row.ImageUrls;old.MediaSource="xml";}
   if(row.SourceProductId.Length>0){if(skuIndex.TryGetValue(old.Sku,out var oldSkus))oldSkus.RemoveAll(p=>p.Id==old.Id);if(barcodeIndex.TryGetValue(old.Barcode,out var oldBars))oldBars.RemoveAll(p=>p.Id==old.Id);old.SourceProductId=row.SourceProductId;old.Sku=row.Sku;old.Barcode=row.Barcode;Index(sourceProductIndex,old.SourceProductId,old);Index(skuIndex,old.Sku,old);Index(barcodeIndex,old.Barcode,old);}
   if(source.UpdateDetails){old.Brand=row.Brand;old.Category=row.Category;old.VatRate=row.VatRate;old.InvoiceName=row.InvoiceName;old.Subtitle=row.Subtitle;old.Shelf=row.Shelf;old.ExpiresOn=row.ExpiresOn;old.XmlAttributes=row.XmlAttributes;}
   else {if(old.InvoiceName.Length==0)old.InvoiceName=row.InvoiceName;if(old.Subtitle.Length==0)old.Subtitle=row.Subtitle;if(old.Shelf.Length==0)old.Shelf=row.Shelf;if(old.ExpiresOn==null)old.ExpiresOn=row.ExpiresOn;foreach(var pair in row.XmlAttributes)old.XmlAttributes.TryAdd(pair.Key,pair.Value);}
   old.XmlCategory=row.XmlCategory;
   if(source.FixedCategory.Length>0||source.CategoryRules.Any(r=>string.Equals(r.XmlCategory,row.XmlCategory,StringComparison.OrdinalIgnoreCase))){old.Category=row.Category;old.Active=row.Active;}
   if(!old.LockPrice)old.ChannelPrices=row.ChannelPrices;
   if(before==JsonSerializer.Serialize(old)){unchanged++;continue;}old.UpdatedUtc=DateTime.UtcNow;Put(c,"CatalogProducts",old.Id,old,tx);updated++;
  }return new(added,updated,unchanged);
 }
 public CatalogUndoReceipt ImportWithUndo(XmlSource source,IReadOnlyList<CatalogProduct> incoming){var before=Products().Select(p=>JsonSerializer.Deserialize<CatalogProduct>(JsonSerializer.Serialize(p))!).ToList();ImportSummary summary=Import(source,incoming);var after=Products().Select(p=>JsonSerializer.Deserialize<CatalogProduct>(JsonSerializer.Serialize(p))!).ToList();return new(Guid.NewGuid().ToString("N"),before,after);}
 public void Undo(CatalogUndoReceipt receipt){using var c=Open();using var tx=c.BeginTransaction();var(current,corrupt)=ReadProductsSafe(c,tx);if(corrupt.Count>0)throw new InvalidOperationException("Kataloğ bozuk kayıt(lar) içeriyor; geri alma güvenlik nedeniyle durduruldu. Önce kurtarma/silme yapın.");var expected=JsonSerializer.Serialize(receipt.After.OrderBy(p=>p.Id));var actual=JsonSerializer.Serialize(current.OrderBy(p=>p.Id));if(expected!=actual)throw new InvalidOperationException("Katalog bu içe aktarmadan sonra değişti; geri alma güvenlik nedeniyle durduruldu.");var beforeIds=receipt.Before.Select(p=>p.Id).ToHashSet(StringComparer.Ordinal);foreach(var removed in current.Where(p=>!beforeIds.Contains(p.Id)))InventoryLedger.DeleteCatalogProduct(c,tx,removed.Id);foreach(var p in receipt.Before)Put(c,"CatalogProducts",p.Id,p,tx);tx.Commit();}
}











