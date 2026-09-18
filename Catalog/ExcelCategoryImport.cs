using System.Text.Json;
using System.IO;
using Microsoft.Data.Sqlite;
namespace TrMarketplaceHubDesktop.Catalog;
public static class ExcelCategoryImport
{
    public static ExcelAuxiliaryPlan Preview(TaxonomyStore store,string path,ExcelImportProfile profile)
    {
        using var data=new ExcelWorkbookData(path,profile);if(!data.Mapped("Category"))throw new InvalidOperationException("Kategori sütununu eşleyin.");
        var snapshot=store.ExcelCategorySnapshot();var existing=JsonSerializer.Deserialize<List<TaxonomyEntry>>(snapshot.Json)!;
        var keys=existing.Select(e=>e.Name.Trim().ToUpperInvariant()).ToHashSet();var planned=new HashSet<string>(keys);var rows=new List<ExcelAuxiliaryRow>();var changes=new List<ExcelAuxiliaryChange>();
        foreach(var row in data.Rows)
        {
            string name="";try
            {
                name=ExcelProductImport.NormalizeCategory(data.Text(row,"Category"));var parts=name.Split(" > ");var nodes=Enumerable.Range(1,parts.Length).Select(n=>string.Join(" > ",parts.Take(n))).ToArray();
                var additions=nodes.Where(n=>!planned.Contains(n.ToUpperInvariant())).ToList();var action=additions.Count==0?"SKIP":"CREATE";
                rows.Add(new(row.RowNumber(),action,name,name,action=="CREATE"?$"{additions.Count} kategori kırılımı eklenecek.":"Kategori ağacı zaten mevcut veya önceki satırda planlandı."));
                if(action=="CREATE"){changes.Add(new(name,JsonSerializer.Serialize(nodes),new[]{row.RowNumber()}));foreach(var node in nodes)planned.Add(node.ToUpperInvariant());}
            }
            catch(InvalidOperationException ex){rows.Add(new(row.RowNumber(),"ERROR",name,name,ex.Message));}
        }
        return new(rows,changes,data.FileHash,ExcelWorkbookData.Signature(profile),snapshot.Identity,snapshot.Json,"categories");
    }
    public static string Apply(TaxonomyStore store,string path,ExcelImportProfile profile,ExcelAuxiliaryPlan? plan,IReadOnlyCollection<int> selected)
    {if(plan is null)throw new InvalidOperationException("Önce kategori önizlemesi oluşturun.");plan.Validate(path,profile,selected,"categories");return store.ApplyExcelCategories(plan,selected);}
}
public sealed partial class TaxonomyStore
{
    static string CategorySnapshot(SqliteConnection c,SqliteTransaction tx)
    {
        using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT Id,Kind,Name,Value,Active,UpdatedUtc FROM TaxonomyEntries WHERE Kind=0 ORDER BY Id";using var reader=cmd.ExecuteReader();var entries=new List<TaxonomyEntry>();
        while(reader.Read()){if(!TryReadEntry(reader,out var entry,out _))throw new InvalidOperationException("Kategori kaydı bozuk; işlem durduruldu.");entries.Add(entry!);}return JsonSerializer.Serialize(entries);
    }
    internal (string Json,string Identity) ExcelCategorySnapshot(){using var c=Open();using var tx=c.BeginTransaction(deferred:true);return(CategorySnapshot(c,tx),Path.GetFullPath(c.DataSource));}
    internal string ApplyExcelCategories(ExcelAuxiliaryPlan plan,IReadOnlyCollection<int> selected)
    {
        using var c=Open();using var tx=c.BeginTransaction();var before=CategorySnapshot(c,tx);
        if(!Path.GetFullPath(c.DataSource).Equals(plan.StoreIdentity,StringComparison.OrdinalIgnoreCase) || before!=plan.Snapshot)throw new InvalidOperationException("Kategori listesi değişti veya önizleme başka kataloğa ait.");
        var known=JsonSerializer.Deserialize<List<TaxonomyEntry>>(before)!.Select(e=>NormalizedKey(e.Name,e.Value)).ToHashSet();
        foreach(var node in plan.Changes.Where(x=>x.RowNumbers.Any(selected.Contains)).SelectMany(x=>JsonSerializer.Deserialize<string[]>(x.Json)!).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if(!known.Add(NormalizedKey(node,"")))continue;
            using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="INSERT INTO TaxonomyEntries(Id,Kind,Name,Value,Active,UpdatedUtc,NormalizedKey) VALUES($id,0,$name,'',1,$at,$key)";cmd.Parameters.AddWithValue("$id",Guid.NewGuid().ToString("N"));cmd.Parameters.AddWithValue("$name",node);cmd.Parameters.AddWithValue("$at",DateTime.UtcNow.ToString("O"));cmd.Parameters.AddWithValue("$key",NormalizedKey(node,""));cmd.ExecuteNonQuery();
        }
        ExcelJournal.Save(c,tx,plan.Id,"categories",before,CategorySnapshot(c,tx));tx.Commit();return plan.Id;
    }
    public string? LastExcelCategoryImportId(){using var c=Open();return ExcelJournal.Last(c,"categories");}
    public void UndoExcelCategories(string id)
    {
        using var c=Open();using var tx=c.BeginTransaction();var receipt=ExcelJournal.Read(c,tx,id,"categories");if(CategorySnapshot(c,tx)!=receipt.After)throw new InvalidOperationException("Kategoriler işlemden sonra değişti; geri alma durduruldu.");
        var old=JsonSerializer.Deserialize<List<TaxonomyEntry>>(receipt.Before)!.Select(e=>e.Id).ToHashSet();
        foreach(var entry in JsonSerializer.Deserialize<List<TaxonomyEntry>>(receipt.After)!.Where(e=>!old.Contains(e.Id)))
        {
            using(var table=c.CreateCommand()){table.Transaction=tx;table.CommandText="SELECT 1 FROM sqlite_master WHERE type='table' AND name='CatalogProducts'";if(table.ExecuteScalar()!=null && CountProductUsage(c,tx,TaxonomyKind.Category,entry.Name)>0)throw new InvalidOperationException("Eklenen kategori bir üründe kullanılıyor; geri alma durduruldu.");}
            if(CountMappingUsage(c,tx,TaxonomyKind.Category,entry.Id)>0)throw new InvalidOperationException("Eklenen kategori eşlemede kullanılıyor; geri alma durduruldu.");
            using var del=c.CreateCommand();del.Transaction=tx;del.CommandText="DELETE FROM TaxonomyEntries WHERE Id=$id";del.Parameters.AddWithValue("$id",entry.Id);del.ExecuteNonQuery();
        }
        ExcelJournal.Complete(c,tx,id);tx.Commit();
    }
}
