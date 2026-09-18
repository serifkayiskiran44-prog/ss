using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Microsoft.Data.Sqlite;
namespace TrMarketplaceHubDesktop.Catalog;

public sealed class ProductAssetCache
{
 readonly string directory;readonly string connection;public string RootPath{get;}
 public ProductAssetCache(string? directory=null)
 {
  this.directory=Path.GetFullPath(directory??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MonoBridgeDesktop"));
  RootPath=Path.Combine(this.directory,"ProductImages");connection=new SqliteConnectionStringBuilder{DataSource=Path.Combine(this.directory,"catalog.db"),DefaultTimeout=15}.ToString();
  using var c=Open();EnsureSchema(c,null);
 }
 SqliteConnection Open(){var c=new SqliteConnection(connection);c.Open();return c;}
 internal static void EnsureSchema(SqliteConnection c,SqliteTransaction? tx)
 {
  using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="CREATE TABLE IF NOT EXISTS ProductImageCopies(ProductId TEXT NOT NULL,Url TEXT NOT NULL,Hash TEXT NOT NULL,PRIMARY KEY(ProductId,Url));CREATE TABLE IF NOT EXISTS PendingProductMediaDeletes(ProductId TEXT PRIMARY KEY)";cmd.ExecuteNonQuery();
 }
 static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
 string Folder(string id)=>Path.Combine(RootPath,Hash(id));
 string FilePath(string id,string url)=>Path.Combine(Folder(id),Hash(url)+".image");
 static void CheckPath(string path)
 {
  var full=Path.GetFullPath(path);var current=full;
  while(current!=null){if((Directory.Exists(current)||File.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new InvalidOperationException("Görsel klasöründe bağlantı/yönlendirme kullanılamaz.");current=Path.GetDirectoryName(current);}
 }
 static void ValidateBytes(byte[] bytes)
 {
  if(bytes.Length==0||bytes.Length>MediaValidationService.MaxBytes)throw new InvalidOperationException("Görsel boş veya 20 MB sınırını aşıyor.");
  try{using var stream=new MemoryStream(bytes);var decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);if(decoder.Frames.Count==0||decoder.Frames.Any(f=>(long)f.PixelWidth*f.PixelHeight>40000000))throw new InvalidOperationException("Görsel boyut sınırı aşıldı.");}
  catch(Exception ex)when(ex is not InvalidOperationException){throw new InvalidOperationException("Dosya geçerli bir görsel değil.",ex);}
 }
 public string StoreBytes(string productId,string url,byte[] bytes)
 {
  ValidateBytes(bytes);using var c=Open();using var tx=c.BeginTransaction();using var find=c.CreateCommand();find.Transaction=tx;find.CommandText="SELECT Json FROM CatalogProducts WHERE Id=$id";find.Parameters.AddWithValue("$id",productId);var json=find.ExecuteScalar() as string;
  var product=json==null?null:JsonSerializer.Deserialize<CatalogProduct>(json);
  if(product==null||!Urls(product).Contains(url,StringComparer.Ordinal))throw new InvalidOperationException("Ürün veya görsel bağlantısı artık mevcut değil.");
  var path=FilePath(productId,url);CheckPath(path);Directory.CreateDirectory(Folder(productId));var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
  try{File.WriteAllBytes(temporary,bytes);CheckPath(path);File.Move(temporary,path,true);}finally{if(File.Exists(temporary))File.Delete(temporary);}
  using var save=c.CreateCommand();save.Transaction=tx;save.CommandText="INSERT INTO ProductImageCopies(ProductId,Url,Hash) VALUES($p,$u,$h) ON CONFLICT(ProductId,Url) DO UPDATE SET Hash=excluded.Hash";save.Parameters.AddWithValue("$p",productId);save.Parameters.AddWithValue("$u",url);save.Parameters.AddWithValue("$h",Convert.ToHexString(SHA256.HashData(bytes)));save.ExecuteNonQuery();tx.Commit();return path;
 }
 public string? Find(string productId,string url){var path=FilePath(productId,url);CheckPath(path);return File.Exists(path)?path:null;}
 public static IEnumerable<string> Urls(CatalogProduct p)=>p.ImageUrls.Split(new[]{'|','\r','\n'},StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal);
 public async Task<string> EnsureAsync(CatalogProduct product,string url,HttpClient http,CancellationToken token)
 {
  var existing=Find(product.Id,url);if(existing!=null)return existing;byte[] bytes;
  if(Uri.TryCreate(url,UriKind.Absolute,out var uri)&&uri.Scheme=="file")
  {
   if(!MediaFileAccessPolicy.TryResolveApprovedFile(uri.LocalPath,MediaFileAccessPolicy.DefaultApprovedRoots,out var source,out var error))throw new InvalidOperationException(error);
   if(new FileInfo(source).Length>MediaValidationService.MaxBytes)throw new InvalidOperationException("Görsel 20 MB sınırını aşıyor.");bytes=await File.ReadAllBytesAsync(source,token);
  }
  else bytes=await XmlThumbnailLoader.ReadAsync(http,url,token);
  token.ThrowIfCancellationRequested();return StoreBytes(product.Id,url,bytes);
 }
 internal static void QueueDeletion(SqliteConnection c,SqliteTransaction tx,string id)
 {
  EnsureSchema(c,tx);using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="DELETE FROM ProductImageCopies WHERE ProductId=$id;INSERT OR IGNORE INTO PendingProductMediaDeletes(ProductId) VALUES($id)";cmd.Parameters.AddWithValue("$id",id);cmd.ExecuteNonQuery();
 }
 // A durable queue bridges the catalog transaction, the separate media database and disk.
 // Cleanup is retried after restart; it never touches source URLs/files.
 public int CleanupDeletedProducts()
 {
  using var c=Open();using var tx=c.BeginTransaction();var ids=new List<string>();
  using(var orphans=c.CreateCommand()){orphans.Transaction=tx;orphans.CommandText="INSERT OR IGNORE INTO PendingProductMediaDeletes(ProductId) SELECT DISTINCT ProductId FROM ProductImageCopies WHERE NOT EXISTS(SELECT 1 FROM CatalogProducts WHERE Id=ProductId);DELETE FROM ProductImageCopies WHERE NOT EXISTS(SELECT 1 FROM CatalogProducts WHERE Id=ProductId)";orphans.ExecuteNonQuery();}
  var existingMedia=Path.Combine(directory,"media.db");
  if(File.Exists(existingMedia))
  {
   using var media=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=existingMedia}.ToString());media.Open();using var readMedia=media.CreateCommand();readMedia.CommandText="SELECT DISTINCT ProductId FROM ProductMedia";using var reader=readMedia.ExecuteReader();
   while(reader.Read()){using var queue=c.CreateCommand();queue.Transaction=tx;queue.CommandText="INSERT OR IGNORE INTO PendingProductMediaDeletes(ProductId) SELECT $id WHERE NOT EXISTS(SELECT 1 FROM CatalogProducts WHERE Id=$id)";queue.Parameters.AddWithValue("$id",reader.GetString(0));queue.ExecuteNonQuery();}
  }
  using(var read=c.CreateCommand()){read.Transaction=tx;read.CommandText="SELECT ProductId FROM PendingProductMediaDeletes";using var r=read.ExecuteReader();while(r.Read())ids.Add(r.GetString(0));}
  var completed=0;
  foreach(var id in ids)
  {
   try
   {
    using(var exists=c.CreateCommand()){exists.Transaction=tx;exists.CommandText="SELECT 1 FROM CatalogProducts WHERE Id=$id";exists.Parameters.AddWithValue("$id",id);if(exists.ExecuteScalar()!=null)continue;}
    var folder=Folder(id);CheckPath(folder);
    if(Directory.Exists(folder))
    {
     // Only generated direct files are owned; unexpected children abort cleanup.
     if(Directory.EnumerateDirectories(folder).Any())throw new InvalidOperationException("Beklenmeyen görsel alt klasörü.");
     foreach(var file in Directory.EnumerateFiles(folder)){CheckPath(file);File.Delete(file);}
     Directory.Delete(folder,false);
    }
    var mediaPath=Path.Combine(directory,"media.db");
    if(File.Exists(mediaPath)){using var media=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=mediaPath}.ToString());media.Open();using var del=media.CreateCommand();del.CommandText="DELETE FROM ProductMedia WHERE ProductId=$id";del.Parameters.AddWithValue("$id",id);del.ExecuteNonQuery();}
    using var done=c.CreateCommand();done.Transaction=tx;done.CommandText="DELETE FROM PendingProductMediaDeletes WHERE ProductId=$id";done.Parameters.AddWithValue("$id",id);done.ExecuteNonQuery();completed++;
   }
   catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or InvalidOperationException or SqliteException){/* Keep durable queue for next attempt. */}
  }
  tx.Commit();return completed;
 }
 public int PendingCleanupCount(){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT COUNT(*) FROM PendingProductMediaDeletes";return Convert.ToInt32(cmd.ExecuteScalar());}
}
