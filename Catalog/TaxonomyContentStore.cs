using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
namespace TrMarketplaceHubDesktop.Catalog;
public sealed class TaxonomyContentTemplate
{
 public string EntryId{get;set;}="";
 public string Channel{get;set;}="local";
 public string ShopId{get;set;}="default";
 public string NamePattern{get;set;}="{Name}";
 public string DescriptionPattern{get;set;}="{Description}";
 public string BrandName{get;set;}="";
 public Dictionary<string,string> Attributes{get;set;}=new();
 public List<string> RequiredAttributes{get;set;}=new();
 public int Version{get;set;}
}
public sealed record TaxonomyContentPreview(string Sku,string Name,string Description,string Brand,IReadOnlyDictionary<string,string> Attributes);
public sealed class TaxonomyContentStore
{
 readonly string connection;
 public TaxonomyContentStore(string? directory=null)
 {
  directory??=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MonoBridgeDesktop");Directory.CreateDirectory(directory);
  connection=new SqliteConnectionStringBuilder{DataSource=Path.Combine(directory,"catalog.db")}.ToString();using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="CREATE TABLE IF NOT EXISTS TaxonomyContentTemplates(EntryId TEXT NOT NULL,Channel TEXT NOT NULL,Shop TEXT NOT NULL,Json TEXT NOT NULL,Version INTEGER NOT NULL,PRIMARY KEY(EntryId,Channel,Shop))";cmd.ExecuteNonQuery();
 }
 SqliteConnection Open(){var c=new SqliteConnection(connection);c.Open();return c;}
 public TaxonomyContentTemplate? Get(string entry,string channel,string shop)
 {
  using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT Json FROM TaxonomyContentTemplates WHERE EntryId=$e AND Channel=$c AND Shop=$s";cmd.Parameters.AddWithValue("$e",entry);cmd.Parameters.AddWithValue("$c",channel.Trim().ToLowerInvariant());cmd.Parameters.AddWithValue("$s",shop.Trim());var json=cmd.ExecuteScalar() as string;return json==null?null:JsonSerializer.Deserialize<TaxonomyContentTemplate>(json);
 }
 static void Validate(TaxonomyContentTemplate t)
 {
  if(string.IsNullOrWhiteSpace(t.EntryId)||string.IsNullOrWhiteSpace(t.Channel)||string.IsNullOrWhiteSpace(t.ShopId))throw new InvalidOperationException("Kayıt, pazaryeri ve mağaza gerekli.");
  if(t.NamePattern.Length>1000||t.DescriptionPattern.Length>30000||t.BrandName.Length>200||t.Attributes.Count>100)throw new InvalidOperationException("Şablon sınırı aşıldı.");
  foreach(var key in t.RequiredAttributes)if(!t.Attributes.TryGetValue(key,out var value)||string.IsNullOrWhiteSpace(value))throw new InvalidOperationException("Zorunlu özellik eksik: "+key);
  if(t.Attributes.Any(a=>string.IsNullOrWhiteSpace(a.Key)||a.Key.Length>200||a.Value.Length>2000))throw new InvalidOperationException("Özellik adı/değeri geçersiz.");
 }
 public TaxonomyContentTemplate Save(TaxonomyContentTemplate t)
 {
  using var c=Open();using var tx=c.BeginTransaction();var saved=SaveCore(c,tx,t);tx.Commit();return saved;
 }
 public void SaveBatch(IReadOnlyList<TaxonomyContentTemplate> items)
 {
  if(items.Count==0||items.Count>5000)throw new InvalidOperationException("1–5000 şablon seçin.");
  using var c=Open();using var tx=c.BeginTransaction();foreach(var item in items)SaveCore(c,tx,System.Text.Json.JsonSerializer.Deserialize<TaxonomyContentTemplate>(System.Text.Json.JsonSerializer.Serialize(item))!);tx.Commit();
 }
 static TaxonomyContentTemplate SaveCore(SqliteConnection c,SqliteTransaction tx,TaxonomyContentTemplate t)
 {
  Validate(t);using(var exists=c.CreateCommand()){exists.Transaction=tx;exists.CommandText="SELECT 1 FROM TaxonomyEntries WHERE Id=$id";exists.Parameters.AddWithValue("$id",t.EntryId);if(exists.ExecuteScalar()==null)throw new InvalidOperationException("Kategori/marka bulunamadı.");}
  using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT Version FROM TaxonomyContentTemplates WHERE EntryId=$e AND Channel=$c AND Shop=$s";cmd.Parameters.AddWithValue("$e",t.EntryId);cmd.Parameters.AddWithValue("$c",t.Channel.Trim().ToLowerInvariant());cmd.Parameters.AddWithValue("$s",t.ShopId.Trim());var version=Convert.ToInt32(cmd.ExecuteScalar()??0);if(version!=t.Version)throw new InvalidOperationException("Şablon değişti; tekrar yükleyin.");t.Version=version+1;
  cmd.CommandText="INSERT INTO TaxonomyContentTemplates(EntryId,Channel,Shop,Json,Version) VALUES($e,$c,$s,$j,$v) ON CONFLICT(EntryId,Channel,Shop) DO UPDATE SET Json=excluded.Json,Version=excluded.Version";cmd.Parameters.AddWithValue("$j",JsonSerializer.Serialize(t));cmd.Parameters.AddWithValue("$v",t.Version);cmd.ExecuteNonQuery();return t;
 }
 public TaxonomyContentPreview Preview(TaxonomyContentTemplate t,CatalogProduct p)
 {
  Validate(t);string Render(string pattern)=>pattern.Replace("{Name}",p.Name).Replace("{Description}",p.Description).Replace("{Description2}",p.XmlAttributes.GetValueOrDefault("Description2","")).Replace("{Description3}",p.XmlAttributes.GetValueOrDefault("Description3","")).Replace("{Sku}",p.Sku).Replace("{Brand}",p.Brand);
  return new(p.Sku,p.LockName?p.Name:Render(t.NamePattern),p.LockDescription?p.Description:Render(t.DescriptionPattern),t.BrandName.Length==0?p.Brand:t.BrandName,new Dictionary<string,string>(t.Attributes));
 }
}
