using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
namespace TrMarketplaceHubDesktop.Catalog;
public enum TaxonomyBatchAction { Activate, Deactivate, Delete, Attach, Repair }

public sealed partial class TaxonomyStore
{
 static string CategoryIdentity(string path)=>string.Join(" > ",path.Split('>').Select(p=>p.Trim()));
 public static bool SameCategory(string path,string other)=>CategoryIdentity(path).Equals(CategoryIdentity(other),StringComparison.OrdinalIgnoreCase);
 public static bool InCategory(string path,string parent){path=CategoryIdentity(path);parent=CategoryIdentity(parent);return path.Equals(parent,StringComparison.OrdinalIgnoreCase)||path.StartsWith(parent+" > ",StringComparison.OrdinalIgnoreCase); }
 public void EnsureCatalogEntries(IEnumerable<CatalogProduct> products)
 {
  using var c=Open();using var tx=c.BeginTransaction();
  foreach(var p in products)
  {
   if(!string.IsNullOrWhiteSpace(p.Brand))EnsureWorkspaceEntry(c,tx,TaxonomyKind.Brand,p.Brand.Trim());
   if(!string.IsNullOrWhiteSpace(p.Category))
   {
    var path=ExcelProductImport.NormalizeCategory(p.Category);var parts=path.Split(" > ");
    for(int i=1;i<=parts.Length;i++)EnsureWorkspaceEntry(c,tx,TaxonomyKind.Category,string.Join(" > ",parts.Take(i)));
   }
  }
  tx.Commit();
 }
 static void EnsureWorkspaceEntry(SqliteConnection c,SqliteTransaction tx,TaxonomyKind kind,string name)
 {
  using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="INSERT INTO TaxonomyEntries(Id,Kind,Name,Value,Active,UpdatedUtc,NormalizedKey) SELECT $id,$kind,$name,'',1,$time,$key WHERE NOT EXISTS(SELECT 1 FROM TaxonomyEntries WHERE Kind=$kind AND NormalizedKey=$key)";
  cmd.Parameters.AddWithValue("$id",Guid.NewGuid().ToString("N"));cmd.Parameters.AddWithValue("$kind",(int)kind);cmd.Parameters.AddWithValue("$name",name);cmd.Parameters.AddWithValue("$time",DateTime.UtcNow.ToString("O"));cmd.Parameters.AddWithValue("$key",NormalizedKey(name,""));cmd.ExecuteNonQuery();
 }
 public TaxonomyEntry SaveWorkspaceEntry(TaxonomyEntry entry,bool requireExisting=false)
 {
  using var c=Open();using var tx=c.BeginTransaction();var saved=SaveWorkspaceEntryCore(c,tx,entry,requireExisting);tx.Commit();return saved;
 }
 static TaxonomyEntry SaveWorkspaceEntryCore(SqliteConnection c,SqliteTransaction tx,TaxonomyEntry entry,bool requireExisting=false)
 {
  Validate(entry);if(entry.Kind==TaxonomyKind.Category)entry.Name=ExcelProductImport.NormalizeCategory(entry.Name);
  if(entry.Kind is not (TaxonomyKind.Category or TaxonomyKind.Brand))throw new InvalidOperationException("Kategori veya marka seçin.");
  var entries=new List<TaxonomyEntry>();
  using(var read=c.CreateCommand()){read.Transaction=tx;read.CommandText="SELECT Id,Kind,Name,Value,Active,UpdatedUtc FROM TaxonomyEntries WHERE Kind=$kind";read.Parameters.AddWithValue("$kind",(int)entry.Kind);using var r=read.ExecuteReader();while(r.Read()){if(!TryReadEntry(r,out var row,out _))throw new InvalidOperationException("Sözlükte bozuk kayıt var.");entries.Add(row!);}}
  var old=entries.SingleOrDefault(e=>e.Id==entry.Id);
  if(requireExisting&&old==null)throw new InvalidOperationException("Kayıt silinmiş; listeyi yenileyin.");
  if(old!=null && old.UpdatedUtc!=entry.UpdatedUtc)throw new InvalidOperationException("Kayıt değişti; listeyi yenileyin.");
  if(old!=null && entry.Kind==TaxonomyKind.Category && entry.Name.StartsWith(old.Name+" > ",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Kategori kendi altına taşınamaz.");
  var renamed=old==null?new List<TaxonomyEntry>():entries.Where(e=>entry.Kind==TaxonomyKind.Category && old.Name!=entry.Name?InCategory(e.Name,old.Name):e.Id==old.Id).ToList();
  var oldName=old?.Name;var changes=new Dictionary<string,string>();
  foreach(var row in renamed){changes[row.Name]=entry.Name+row.Name[oldName!.Length..];}
  if(old==null)renamed.Add(entry);
  foreach(var row in renamed)
  {
   var next=row.Id==entry.Id?entry.Name:changes[row.Name];if(entries.Any(e=>!renamed.Any(a=>a.Id==e.Id)&&NormalizedKey(e.Name,e.Value)==NormalizedKey(next,row.Value)))throw new InvalidOperationException("Hedef ad zaten kullanılıyor.");
   row.Name=next;if(row.Id==entry.Id)row.Active=entry.Active;Validate(row);row.UpdatedUtc=DateTime.UtcNow;
  }
  // Move keys aside so sibling/path renames cannot fail due to update order.
  foreach(var row in renamed){using var key=c.CreateCommand();key.Transaction=tx;key.CommandText="UPDATE TaxonomyEntries SET NormalizedKey=$key WHERE Id=$id";key.Parameters.AddWithValue("$key","rename:"+Guid.NewGuid().ToString("N"));key.Parameters.AddWithValue("$id",row.Id);key.ExecuteNonQuery();}
  foreach(var row in renamed)
  {
   using var put=c.CreateCommand();put.Transaction=tx;put.CommandText="INSERT INTO TaxonomyEntries(Id,Kind,Name,Value,Active,UpdatedUtc,NormalizedKey) VALUES($id,$kind,$name,$value,$active,$time,$key) ON CONFLICT(Id) DO UPDATE SET Name=excluded.Name,Value=excluded.Value,Active=excluded.Active,UpdatedUtc=excluded.UpdatedUtc,NormalizedKey=excluded.NormalizedKey";
   put.Parameters.AddWithValue("$id",row.Id);put.Parameters.AddWithValue("$kind",(int)row.Kind);put.Parameters.AddWithValue("$name",row.Name);put.Parameters.AddWithValue("$value",row.Value);put.Parameters.AddWithValue("$active",row.Active?1:0);put.Parameters.AddWithValue("$time",row.UpdatedUtc.ToString("O"));put.Parameters.AddWithValue("$key",NormalizedKey(row.Name,row.Value));put.ExecuteNonQuery();
  }
  if(entry.Kind==TaxonomyKind.Category){var parts=entry.Name.Split(" > ");for(int i=1;i<parts.Length;i++)EnsureWorkspaceEntry(c,tx,entry.Kind,string.Join(" > ",parts.Take(i)));}
  if(oldName!=null && oldName!=entry.Name)
  {
   var products=new List<CatalogProduct>();using(var read=c.CreateCommand()){read.Transaction=tx;read.CommandText="SELECT Json FROM CatalogProducts";using var r=read.ExecuteReader();while(r.Read()){CatalogProduct p;try{p=JsonSerializer.Deserialize<CatalogProduct>(r.GetString(0))??throw new JsonException();}catch(JsonException){throw new InvalidOperationException("Katalogda bozuk ürün var.");}products.Add(p);}}
   foreach(var p in products)
   {
    var current=entry.Kind==TaxonomyKind.Brand?p.Brand:CategoryIdentity(p.Category);
    if(!(entry.Kind==TaxonomyKind.Category?InCategory(current,oldName):current.Equals(oldName,StringComparison.OrdinalIgnoreCase)))continue;
    var next=entry.Kind==TaxonomyKind.Category?entry.Name+current[oldName.Length..]:entry.Name;
    if(entry.Kind==TaxonomyKind.Brand)p.Brand=next;else p.Category=next;p.UpdatedUtc=DateTime.UtcNow;CatalogStore.ValidateExcelProduct(p);
    using var put=c.CreateCommand();put.Transaction=tx;put.CommandText="UPDATE CatalogProducts SET Json=$json WHERE Id=$id";put.Parameters.AddWithValue("$json",JsonSerializer.Serialize(p));put.Parameters.AddWithValue("$id",p.Id);put.ExecuteNonQuery();
   }
  }
  return renamed.Single(e=>e.Id==entry.Id);
 }

 public void ApplyWorkspaceBatch(TaxonomyKind kind,IReadOnlyList<TaxonomyEntry> selection,TaxonomyBatchAction action,string? parentId=null)
 {
  if(kind is not (TaxonomyKind.Category or TaxonomyKind.Brand)||!Enum.IsDefined(action))throw new InvalidOperationException("İşlem türü geçersiz.");
  if(selection.Count==0||selection.Count>5000||selection.Select(e=>e.Id).Distinct().Count()!=selection.Count)throw new InvalidOperationException("1–5000 farklı kayıt seçin.");
  using var c=Open();using var tx=c.BeginTransaction();var rows=new List<TaxonomyEntry>();
  foreach(var expected in selection)
  {
   using var read=c.CreateCommand();read.Transaction=tx;read.CommandText="SELECT Id,Kind,Name,Value,Active,UpdatedUtc FROM TaxonomyEntries WHERE Id=$id AND Kind=$kind";read.Parameters.AddWithValue("$id",expected.Id);read.Parameters.AddWithValue("$kind",(int)kind);using var r=read.ExecuteReader();
   if(expected.Kind!=kind||!r.Read()||!TryReadEntry(r,out var row,out _)||row!.UpdatedUtc!=expected.UpdatedUtc)throw new InvalidOperationException("Seçilen kayıt değişti veya silindi; listeyi yenileyin.");rows.Add(row!);
  }
  if(action==TaxonomyBatchAction.Attach)
  {
   if(kind!=TaxonomyKind.Category)throw new InvalidOperationException("Yalnız kategoriler üst kategoriye bağlanabilir.");
   string target="";
   if(!string.IsNullOrEmpty(parentId)){using var p=c.CreateCommand();p.Transaction=tx;p.CommandText="SELECT Name FROM TaxonomyEntries WHERE Id=$id AND Kind=0";p.Parameters.AddWithValue("$id",parentId);target=p.ExecuteScalar() as string??throw new InvalidOperationException("Üst kategori bulunamadı.");}
   if(rows.Any(e=>target.Length>0&&InCategory(target,e.Name)))throw new InvalidOperationException("Kategori kendi altına bağlanamaz.");
   var roots=rows.Where(e=>!rows.Any(other=>other.Id!=e.Id&&InCategory(e.Name,other.Name))).ToList();
   foreach(var row in roots){var leaf=CategoryIdentity(row.Name).Split(" > ").Last();row.Name=target.Length==0?leaf:target+" > "+leaf;SaveWorkspaceEntryCore(c,tx,row);}
  }
  else if(action==TaxonomyBatchAction.Delete)
  {
   foreach(var row in rows.OrderByDescending(e=>e.Name.Count(c=>c=='>')))DeleteWorkspaceEntryCore(c,tx,kind,row.Id);
  }
  else
  {
   foreach(var row in rows){if(action!=TaxonomyBatchAction.Repair)row.Active=action==TaxonomyBatchAction.Activate;SaveWorkspaceEntryCore(c,tx,row);}
  }
  tx.Commit();
 }
 public void SaveWorkspaceEntries(IReadOnlyList<TaxonomyEntry> entries)
 {
  if(entries.Count==0||entries.Count>5000||entries.Select(e=>e.Id).Distinct().Count()!=entries.Count)throw new InvalidOperationException("Geçersiz toplu düzenleme.");
  using var c=Open();using var tx=c.BeginTransaction();foreach(var entry in entries)SaveWorkspaceEntryCore(c,tx,entry,true);tx.Commit();
 }
}
