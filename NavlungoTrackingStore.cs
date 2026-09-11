using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;

namespace TrMarketplaceHubDesktop;

public sealed record NavlungoTrackingRecord(string Id, string Marketplace, string ShopId, string OrderId, string ShipmentId, string TrackingNumber, string Carrier, string State, DateTime UpdatedUtc, string Source, string Error);

public sealed class NavlungoTrackingStore
{
    readonly string connectionString;
    public NavlungoTrackingStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop"); Directory.CreateDirectory(directory); connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "navlungo.db") }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "CREATE TABLE IF NOT EXISTS NavlungoTracking(Id TEXT PRIMARY KEY,Marketplace TEXT NOT NULL,ShopId TEXT NOT NULL,OrderId TEXT NOT NULL,ShipmentId TEXT NOT NULL,TrackingNumber TEXT NOT NULL,Carrier TEXT NOT NULL,State TEXT NOT NULL,UpdatedUtc TEXT NOT NULL,Source TEXT NOT NULL,Error TEXT NOT NULL DEFAULT '',UNIQUE(Marketplace,ShopId,OrderId,ShipmentId))"; cmd.ExecuteNonQuery();
    }
    SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); return c; }
    public void Save(NavlungoTrackingRecord record)
    {
        if (new[] { record.Marketplace, record.ShopId, record.OrderId, record.ShipmentId, record.TrackingNumber, record.State }.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Kanal, mağaza, sipariş, paket, tracking ve durum zorunlu.");
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "INSERT INTO NavlungoTracking(Id,Marketplace,ShopId,OrderId,ShipmentId,TrackingNumber,Carrier,State,UpdatedUtc,Source,Error) VALUES($id,$m,$s,$o,$p,$t,$c,$state,$at,$source,$error) ON CONFLICT(Marketplace,ShopId,OrderId,ShipmentId) DO UPDATE SET TrackingNumber=excluded.TrackingNumber,Carrier=excluded.Carrier,State=excluded.State,UpdatedUtc=excluded.UpdatedUtc,Source=excluded.Source,Error=excluded.Error"; cmd.Parameters.AddWithValue("$id", string.IsNullOrWhiteSpace(record.Id) ? Guid.NewGuid().ToString("N") : record.Id); cmd.Parameters.AddWithValue("$m", record.Marketplace.Trim()); cmd.Parameters.AddWithValue("$s", record.ShopId.Trim()); cmd.Parameters.AddWithValue("$o", record.OrderId.Trim()); cmd.Parameters.AddWithValue("$p", record.ShipmentId.Trim()); cmd.Parameters.AddWithValue("$t", record.TrackingNumber.Trim()); cmd.Parameters.AddWithValue("$c", record.Carrier.Trim()); cmd.Parameters.AddWithValue("$state", record.State.Trim()); cmd.Parameters.AddWithValue("$at", record.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture)); cmd.Parameters.AddWithValue("$source", record.Source.Trim()); cmd.Parameters.AddWithValue("$error", MarketplaceConnectionStore.Redact(record.Error)); cmd.ExecuteNonQuery();
    }
    public IReadOnlyList<NavlungoTrackingRecord> List(string? orderId = null)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = orderId is null ? "SELECT Id,Marketplace,ShopId,OrderId,ShipmentId,TrackingNumber,Carrier,State,UpdatedUtc,Source,Error FROM NavlungoTracking ORDER BY UpdatedUtc DESC" : "SELECT Id,Marketplace,ShopId,OrderId,ShipmentId,TrackingNumber,Carrier,State,UpdatedUtc,Source,Error FROM NavlungoTracking WHERE OrderId=$order ORDER BY UpdatedUtc DESC"; if (orderId is not null) cmd.Parameters.AddWithValue("$order", orderId); using var r = cmd.ExecuteReader(); var rows = new List<NavlungoTrackingRecord>(); while (r.Read()) rows.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), DateTime.Parse(r.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), r.GetString(9), r.GetString(10))); return rows;
    }
}
