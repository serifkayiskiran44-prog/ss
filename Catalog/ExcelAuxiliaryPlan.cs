using Microsoft.Data.Sqlite;
using System.Text.Json;
namespace TrMarketplaceHubDesktop.Catalog;
public sealed record ExcelAuxiliaryRow(int RowNumber,string Action,string Key,string Name,string Note)
{
    public string Status => Action switch {"CREATE"=>"Eklenecek","UPDATE"=>"Güncellenecek","SKIP"=>"Atlanacak",_=>"Hatalı"};
    public bool CanApply=>Action is "CREATE" or "UPDATE";
}
internal sealed record ExcelAuxiliaryChange(string Key,string Json,IReadOnlyList<int> RowNumbers);
public sealed class ExcelAuxiliaryPlan
{
    public string Id{get;}=Guid.NewGuid().ToString("N");
    public IReadOnlyList<ExcelAuxiliaryRow> Rows{get;}
    public IReadOnlyList<string> Errors{get;}
    internal string FileHash{get;}
    internal string ProfileHash{get;}
    internal string StoreIdentity{get;}
    internal string Snapshot{get;}
    internal string Scope{get;}
    internal IReadOnlyList<ExcelAuxiliaryChange> Changes{get;}
    internal ExcelAuxiliaryPlan(List<ExcelAuxiliaryRow> rows,List<ExcelAuxiliaryChange> changes,string fileHash,string profileHash,string identity,string snapshot,string scope)
    {Rows=rows.AsReadOnly();Changes=changes.AsReadOnly();Errors=rows.Where(r=>r.Action=="ERROR").Select(r=>$"Satır {r.RowNumber}: {r.Note}").ToList().AsReadOnly();FileHash=fileHash;ProfileHash=profileHash;StoreIdentity=identity;Snapshot=snapshot;Scope=scope;}
    internal void Validate(string path,ExcelImportProfile profile,IReadOnlyCollection<int> selected,string scope)
    {
        if(Errors.Count>0)throw new InvalidOperationException("Hatalı satırlar düzeltilmeden işlem uygulanamaz.");
        if(Scope!=scope || FileHash!=ExcelWorkbookData.HashFile(path) || ProfileHash!=ExcelWorkbookData.Signature(profile))throw new InvalidOperationException("Dosya veya ayarlar değişti; önizlemeyi yenileyin.");
        if(selected.Count==0 || selected.Distinct().Count()!=selected.Count || selected.Any(n=>!Rows.Any(r=>r.RowNumber==n && r.CanApply)))throw new InvalidOperationException("Geçerli önizleme satırları seçin.");
    }
}
internal sealed record ExcelJournalReceipt(string Before,string After);
internal static class ExcelJournal
{
    public static void Ensure(SqliteConnection c,SqliteTransaction tx)
    {using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="CREATE TABLE IF NOT EXISTS ExcelAuxJournal(Id TEXT PRIMARY KEY,Kind TEXT NOT NULL,Payload TEXT NOT NULL,CreatedUtc TEXT NOT NULL,Undone INTEGER NOT NULL DEFAULT 0)";cmd.ExecuteNonQuery();}
    public static void Save(SqliteConnection c,SqliteTransaction tx,string id,string kind,string before,string after)
    {Ensure(c,tx);using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="INSERT INTO ExcelAuxJournal(Id,Kind,Payload,CreatedUtc) VALUES($id,$kind,$json,$at)";cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$kind",kind);cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(new ExcelJournalReceipt(before,after)));cmd.Parameters.AddWithValue("$at",DateTime.UtcNow.ToString("O"));try{cmd.ExecuteNonQuery();}catch(SqliteException ex)when(ex.SqliteErrorCode==19){throw new InvalidOperationException("Bu önizleme zaten uygulandı.");}}
    public static ExcelJournalReceipt Read(SqliteConnection c,SqliteTransaction tx,string id,string kind)
    {Ensure(c,tx);using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="SELECT Payload FROM ExcelAuxJournal WHERE Id=$id AND Kind=$kind AND Undone=0";cmd.Parameters.AddWithValue("$id",id);cmd.Parameters.AddWithValue("$kind",kind);return JsonSerializer.Deserialize<ExcelJournalReceipt>(cmd.ExecuteScalar() as string??throw new InvalidOperationException("Geri alınacak Excel işlemi yok."))!;}
    public static void Complete(SqliteConnection c,SqliteTransaction tx,string id)
    {using var cmd=c.CreateCommand();cmd.Transaction=tx;cmd.CommandText="UPDATE ExcelAuxJournal SET Undone=1 WHERE Id=$id";cmd.Parameters.AddWithValue("$id",id);cmd.ExecuteNonQuery();}
    public static string? Last(SqliteConnection c,string kind)
    {using var cmd=c.CreateCommand();cmd.CommandText="SELECT 1 FROM sqlite_master WHERE type='table' AND name='ExcelAuxJournal'";if(cmd.ExecuteScalar()==null)return null;cmd.CommandText="SELECT Id FROM ExcelAuxJournal WHERE Kind=$kind AND Undone=0 ORDER BY CreatedUtc DESC LIMIT 1";cmd.Parameters.AddWithValue("$kind",kind);return cmd.ExecuteScalar() as string;}
}
