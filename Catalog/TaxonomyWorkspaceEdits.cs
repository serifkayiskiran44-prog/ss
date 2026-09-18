using Microsoft.Data.Sqlite;
namespace TrMarketplaceHubDesktop.Catalog;
public sealed record TaxonomyWorkspaceMappingEdit(string Channel,string ShopId,string Value,IReadOnlyList<TaxonomyMapping> Expected);
public sealed record TaxonomyWorkspaceEdit(TaxonomyEntry Entry,IReadOnlyList<TaxonomyWorkspaceMappingEdit> Mappings);
public sealed partial class TaxonomyStore
{
 public void SaveWorkspaceEdits(IReadOnlyList<TaxonomyWorkspaceEdit> edits)
 {
  if(edits.Count==0||edits.Count>5000||edits.Select(e=>e.Entry.Id).Distinct().Count()!=edits.Count)throw new InvalidOperationException("Geçersiz toplu düzenleme.");
  using var c=Open();using var tx=c.BeginTransaction();
  var depths=new Dictionary<string,int>();foreach(var edit in edits){using var read=c.CreateCommand();read.Transaction=tx;read.CommandText="SELECT Name,UpdatedUtc FROM TaxonomyEntries WHERE Id=$id AND Kind=$kind";read.Parameters.AddWithValue("$id",edit.Entry.Id);read.Parameters.AddWithValue("$kind",(int)edit.Entry.Kind);using var r=read.ExecuteReader();if(!r.Read()||!TryParseUtc(r.GetString(1),out var version)||version!=edit.Entry.UpdatedUtc)throw new InvalidOperationException("Kayıt değişti; listeyi yenileyin.");depths[edit.Entry.Id]=r.GetString(0).Count(ch=>ch=='>');}
  // Descendant edits precede ancestor renames so the rename carries their final state.
  foreach(var edit in edits.OrderByDescending(e=>depths[e.Entry.Id]))
  {
   SaveWorkspaceEntryCore(c,tx,edit.Entry);
   foreach(var change in edit.Mappings)
   {
    var market=change.Channel.Trim().ToLowerInvariant();var shop=change.ShopId.Trim();var current=new List<TaxonomyMapping>();
    using(var read=c.CreateCommand()){read.Transaction=tx;read.CommandText="SELECT ExternalKey,LocalId,Version FROM TaxonomyMappings WHERE Kind=$kind AND Marketplace=$market AND ShopId=$shop";read.Parameters.AddWithValue("$kind",(int)edit.Entry.Kind);read.Parameters.AddWithValue("$market",market);read.Parameters.AddWithValue("$shop",shop);using var r=read.ExecuteReader();while(r.Read())current.Add(new(edit.Entry.Kind,market,shop,r.GetString(0),r.GetString(1),r.GetInt32(2)));}
    var owned=current.Where(m=>m.LocalId==edit.Entry.Id).OrderBy(m=>m.ExternalKey).ToList();
    if(!owned.SequenceEqual(change.Expected.OrderBy(m=>m.ExternalKey)))throw new InvalidOperationException("Pazaryeri eşlemesi değişti; listeyi yenileyin.");
    if(owned.Count>1)throw new InvalidOperationException("Birden fazla karşılık var; Pazaryeri eşleştirmeleri sekmesinde tek tek düzenleyin.");
    var key=change.Value.Trim();if(key.Length>0){ValidateExternalKey(key);if(current.Any(m=>NormalizeExternalKey(m.ExternalKey)==NormalizeExternalKey(key)&&m.LocalId!=edit.Entry.Id))throw new InvalidOperationException("Pazaryeri karşılığı başka bir kayda bağlı: "+key);}
    foreach(var old in owned)UnmapWorkspaceCore(c,tx,edit.Entry.Kind,old.ExternalKey,market,shop);
    if(key.Length>0)MapCore(c,tx,edit.Entry.Kind,key,edit.Entry.Id,market,shop,0);
   }
  }
  tx.Commit();
 }
}
