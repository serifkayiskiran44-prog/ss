using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TrMarketplaceHubDesktop;

public sealed partial class OrdersStore
{
    public void Archive(string marketplace,string shop,string orderId,string reason)
    {
        if(string.IsNullOrWhiteSpace(reason))throw new ArgumentException("Arşiv nedeni gerekli.");
        using var c=Open();EnsureArchive(c);using var tx=c.BeginTransaction();
        using var get=c.CreateCommand();get.Transaction=tx;get.CommandText="SELECT payload FROM orders WHERE marketplace=$m AND shop=$s AND id=$i";AddKey(get,marketplace,shop,orderId);
        var payload=get.ExecuteScalar() as string??throw new InvalidOperationException("Arşivlenecek sipariş bulunamadı.");
        if(!TryReadOrder(marketplace,shop,orderId,payload,out _,out var corrupt))throw new OrderRowCorruptException(marketplace,shop,orderId,corrupt!.Reason);
        using var put=c.CreateCommand();put.Transaction=tx;put.CommandText="INSERT INTO order_archive(marketplace,shop,id,payload,reason,archived_utc) VALUES($m,$s,$i,$p,$r,$u) ON CONFLICT(marketplace,shop,id) DO UPDATE SET payload=excluded.payload,reason=excluded.reason,archived_utc=excluded.archived_utc";AddKey(put,marketplace,shop,orderId);put.Parameters.AddWithValue("$p",payload);put.Parameters.AddWithValue("$r",reason.Trim());put.Parameters.AddWithValue("$u",DateTimeOffset.UtcNow.ToString("O"));put.ExecuteNonQuery();
        using var delete=c.CreateCommand();delete.Transaction=tx;delete.CommandText="DELETE FROM orders WHERE marketplace=$m AND shop=$s AND id=$i";AddKey(delete,marketplace,shop,orderId);if(delete.ExecuteNonQuery()!=1)throw new InvalidOperationException("Sipariş arşive taşınamadı.");tx.Commit();
    }

    public void Restore(string marketplace,string shop,string orderId)
    {
        using var c=Open();EnsureArchive(c);using var tx=c.BeginTransaction();
        using var get=c.CreateCommand();get.Transaction=tx;get.CommandText="SELECT payload FROM order_archive WHERE marketplace=$m AND shop=$s AND id=$i";AddKey(get,marketplace,shop,orderId);var payload=get.ExecuteScalar() as string??throw new InvalidOperationException("Geri yüklenecek arşiv kaydı bulunamadı.");
        if(!TryReadOrder(marketplace,shop,orderId,payload,out _,out var corrupt))throw new OrderRowCorruptException(marketplace,shop,orderId,corrupt!.Reason);
        using var exists=c.CreateCommand();exists.Transaction=tx;exists.CommandText="SELECT COUNT(*) FROM orders WHERE marketplace=$m AND shop=$s AND id=$i";AddKey(exists,marketplace,shop,orderId);if(Convert.ToInt32(exists.ExecuteScalar())!=0)throw new InvalidOperationException("Aynı kimlikte etkin sipariş var; arşiv kaydı geri yüklenmedi.");
        using var put=c.CreateCommand();put.Transaction=tx;put.CommandText="INSERT INTO orders VALUES($m,$s,$i,$p)";AddKey(put,marketplace,shop,orderId);put.Parameters.AddWithValue("$p",payload);put.ExecuteNonQuery();
        using var delete=c.CreateCommand();delete.Transaction=tx;delete.CommandText="DELETE FROM order_archive WHERE marketplace=$m AND shop=$s AND id=$i";AddKey(delete,marketplace,shop,orderId);delete.ExecuteNonQuery();tx.Commit();
    }

    public List<OrderSnapshot> ReadArchived()
    {
        using var c=Open();EnsureArchive(c);using var cmd=c.CreateCommand();cmd.CommandText="SELECT marketplace,shop,id,payload FROM order_archive ORDER BY archived_utc DESC";using var reader=cmd.ExecuteReader();var rows=new List<OrderSnapshot>();while(reader.Read())if(TryReadOrder(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),out var order,out _))rows.Add(order!);return rows;
    }

    static void EnsureArchive(SqliteConnection c){using var cmd=c.CreateCommand();cmd.CommandText="CREATE TABLE IF NOT EXISTS order_archive(marketplace TEXT NOT NULL,shop TEXT NOT NULL,id TEXT NOT NULL,payload TEXT NOT NULL,reason TEXT NOT NULL,archived_utc TEXT NOT NULL,PRIMARY KEY(marketplace,shop,id))";cmd.ExecuteNonQuery();}
    static void AddKey(SqliteCommand command,string marketplace,string shop,string id){command.Parameters.AddWithValue("$m",marketplace);command.Parameters.AddWithValue("$s",shop);command.Parameters.AddWithValue("$i",id);}
}
