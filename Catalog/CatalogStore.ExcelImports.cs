using Microsoft.Data.Sqlite;
using System.IO;
using System.Text.Json;

namespace TrMarketplaceHubDesktop.Catalog;

public partial class CatalogStore
{
    internal static void ValidateExcelProduct(CatalogProduct product) => Valid(product);
    internal (List<CatalogProduct> Products,string Hash,string Identity) ExcelSnapshot()
    {
        using var connection=Open();using var tx=connection.BeginTransaction(deferred:true);
        var (products,corrupt)=ReadProductsSafe(connection,tx);
        if(corrupt.Count>0)throw new InvalidOperationException("Katalogda bozuk kayıt var; Excel önizlemesi durduruldu.");
        return (products,ExcelHash(products),Path.GetFullPath(connection.DataSource));
    }
    static string ExcelHash(IEnumerable<CatalogProduct> products) => ExcelWorkbookData.Signature(products.OrderBy(p=>p.Id,StringComparer.Ordinal).ToList());
    static void EnsureExcelJournal(SqliteConnection connection,SqliteTransaction tx)
    {
        using var command=connection.CreateCommand();command.Transaction=tx;
        command.CommandText="CREATE TABLE IF NOT EXISTS ExcelImportJournal(Id TEXT PRIMARY KEY, Receipt TEXT NOT NULL, CreatedUtc TEXT NOT NULL, Undone INTEGER NOT NULL DEFAULT 0)";
        command.ExecuteNonQuery();
    }
    internal CatalogUndoReceipt ApplyExcelPlan(ExcelProductPlan plan,IReadOnlyCollection<int> rows)
    {
        using var connection=Open();using var tx=connection.BeginTransaction();
        if(!Path.GetFullPath(connection.DataSource).Equals(plan.StoreIdentity,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Önizleme başka bir kataloğa ait.");
        var (before,corrupt)=ReadProductsSafe(connection,tx);
        if(corrupt.Count>0 || ExcelHash(before)!=plan.CatalogHash)throw new InvalidOperationException("Katalog önizlemeden sonra değişti; tekrar önizleyin.");
        EnsureExcelJournal(connection,tx);
        using(var check=connection.CreateCommand()){check.Transaction=tx;check.CommandText="SELECT 1 FROM ExcelImportJournal WHERE Id=$id";check.Parameters.AddWithValue("$id",plan.Id);if(check.ExecuteScalar()!=null)throw new InvalidOperationException("Bu önizleme daha önce uygulandı.");}
        foreach(var row in plan.Products.Where(p=>rows.Contains(p.RowNumber)))
        {
            var product=JsonSerializer.Deserialize<CatalogProduct>(row.Json)!;
            Valid(product);EnsureUniqueIdentity(connection,tx,product);
            product.UpdatedUtc=DateTime.UtcNow;
            Put(connection,"CatalogProducts",product.Id,product,tx);
        }
        var after=ReadProductsSafe(connection,tx).Healthy;
        var receipt=new CatalogUndoReceipt(plan.Id,before,after);
        using(var command=connection.CreateCommand())
        {
            command.Transaction=tx;command.CommandText="INSERT INTO ExcelImportJournal(Id,Receipt,CreatedUtc) VALUES($id,$receipt,$at)";
            command.Parameters.AddWithValue("$id",receipt.Id);command.Parameters.AddWithValue("$receipt",JsonSerializer.Serialize(receipt));command.Parameters.AddWithValue("$at",DateTime.UtcNow.ToString("O"));command.ExecuteNonQuery();
        }
        tx.Commit();return receipt;
    }
    public string? LastExcelImportId()
    {
        using var connection=Open();using var command=connection.CreateCommand();
        command.CommandText="SELECT 1 FROM sqlite_master WHERE type='table' AND name='ExcelImportJournal'";if(command.ExecuteScalar()==null)return null;
        command.CommandText="SELECT Id FROM ExcelImportJournal WHERE Undone=0 ORDER BY CreatedUtc DESC LIMIT 1";return command.ExecuteScalar() as string;
    }
    public void UndoExcelImport(string receiptId)
    {
        using var connection=Open();using var tx=connection.BeginTransaction();EnsureExcelJournal(connection,tx);
        CatalogUndoReceipt receipt;
        using(var get=connection.CreateCommand())
        {
            get.Transaction=tx;get.CommandText="SELECT Receipt FROM ExcelImportJournal WHERE Id=$id AND Undone=0";get.Parameters.AddWithValue("$id",receiptId);
            var json=get.ExecuteScalar() as string??throw new InvalidOperationException("Geri alınacak Excel işlemi yok veya zaten geri alındı.");
            receipt=JsonSerializer.Deserialize<CatalogUndoReceipt>(json)??throw new InvalidOperationException("Excel geri alma kaydı okunamadı.");
        }
        var(current,corrupt)=ReadProductsSafe(connection,tx);
        if(corrupt.Count>0 || ExcelHash(current)!=ExcelHash(receipt.After))throw new InvalidOperationException("Katalog işlemden sonra değişti; sonraki düzenlemeleri korumak için geri alma durduruldu.");
        // Restore only products touched by this import; unrelated rows are never deleted/reinserted.
        var before=receipt.Before.ToDictionary(p=>p.Id);var after=receipt.After.ToDictionary(p=>p.Id);
        foreach(var product in receipt.After)
        {
            if(!before.ContainsKey(product.Id))InventoryLedger.DeleteCatalogProduct(connection,tx,product.Id);
            else if(JsonSerializer.Serialize(before[product.Id])!=JsonSerializer.Serialize(product))Put(connection,"CatalogProducts",product.Id,before[product.Id],tx);
        }
        using(var update=connection.CreateCommand()){update.Transaction=tx;update.CommandText="UPDATE ExcelImportJournal SET Undone=1 WHERE Id=$id";update.Parameters.AddWithValue("$id",receipt.Id);update.ExecuteNonQuery();}
        tx.Commit();
    }
}
