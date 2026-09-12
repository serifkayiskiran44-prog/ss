using Microsoft.Data.Sqlite;
using System.IO;

namespace TrMarketplaceHubDesktop;

public sealed class OrderArchiveStore
{
    readonly string connectionString;
    public OrderArchiveStore(string directory)
    {
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "orders.db") }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "CREATE TABLE IF NOT EXISTS OrderArchive(Marketplace TEXT NOT NULL,ShopId TEXT NOT NULL,OrderId TEXT NOT NULL,ArchivedUtc TEXT NOT NULL,PRIMARY KEY(Marketplace,ShopId,OrderId))"; cmd.ExecuteNonQuery();
    }
    SqliteConnection Open() { var c = SqliteConnectionPolicy.Open(connectionString); return c; }
    public void Archive(string marketplace, string shopId, string orderId)
    { Validate(marketplace, shopId, orderId); using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "INSERT OR IGNORE INTO OrderArchive VALUES($m,$s,$o,$at)"; cmd.Parameters.AddWithValue("$m", marketplace.Trim()); cmd.Parameters.AddWithValue("$s", shopId.Trim()); cmd.Parameters.AddWithValue("$o", orderId.Trim()); cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O")); cmd.ExecuteNonQuery(); }
    public void Restore(string marketplace, string shopId, string orderId)
    { Validate(marketplace, shopId, orderId); using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "DELETE FROM OrderArchive WHERE Marketplace=$m AND ShopId=$s AND OrderId=$o"; cmd.Parameters.AddWithValue("$m", marketplace.Trim()); cmd.Parameters.AddWithValue("$s", shopId.Trim()); cmd.Parameters.AddWithValue("$o", orderId.Trim()); cmd.ExecuteNonQuery(); }
    public bool IsArchived(string marketplace, string shopId, string orderId)
    { Validate(marketplace, shopId, orderId); using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT 1 FROM OrderArchive WHERE Marketplace=$m AND ShopId=$s AND OrderId=$o"; cmd.Parameters.AddWithValue("$m", marketplace.Trim()); cmd.Parameters.AddWithValue("$s", shopId.Trim()); cmd.Parameters.AddWithValue("$o", orderId.Trim()); return cmd.ExecuteScalar() is not null; }
    static void Validate(string m, string s, string o) { if (string.IsNullOrWhiteSpace(m) || string.IsNullOrWhiteSpace(s) || string.IsNullOrWhiteSpace(o)) throw new ArgumentException("Sipariş arşiv kimliği eksik."); }
}
