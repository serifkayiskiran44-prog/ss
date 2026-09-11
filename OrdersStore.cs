using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
namespace TrMarketplaceHubDesktop;
public sealed class OrdersStore
{
 public sealed record OrderPage(IReadOnlyList<OrderSnapshot> Items,int Total);
 readonly string connectionString;
 public OrdersStore(string? directory=null)
 {
  directory??=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MonoBridgeDesktop");Directory.CreateDirectory(directory);
  connectionString=new SqliteConnectionStringBuilder{DataSource=Path.Combine(directory,"orders.db")}.ToString();
  using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="CREATE TABLE IF NOT EXISTS orders(marketplace TEXT NOT NULL,shop TEXT NOT NULL,id TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(marketplace,shop,id))";cmd.ExecuteNonQuery();
 }
 SqliteConnection Open(){var c=new SqliteConnection(connectionString);c.Open();return c;}
 public List<OrderSnapshot> ReadAll(){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT payload FROM orders";using var r=cmd.ExecuteReader();var rows=new List<OrderSnapshot>();while(r.Read())rows.Add(JsonSerializer.Deserialize<OrderSnapshot>(r.GetString(0))!);return rows.OrderByDescending(o=>o.UpdatedAt).ToList();}
 public OrderPage ReadPage(string? marketplace=null,string? shopId=null,string? status=null,string? query=null,int offset=0,int limit=100)
 { if(offset<0||limit<1||limit>1000)throw new ArgumentOutOfRangeException(nameof(limit)); var text=query?.Trim()??""; var rows=ReadAll().Where(o=>(string.IsNullOrWhiteSpace(marketplace)||o.Marketplace.Equals(marketplace.Trim(),StringComparison.OrdinalIgnoreCase))&&(string.IsNullOrWhiteSpace(shopId)||o.ShopId.Equals(shopId.Trim(),StringComparison.OrdinalIgnoreCase))&&(string.IsNullOrWhiteSpace(status)||o.RawStatus.Equals(status.Trim(),StringComparison.OrdinalIgnoreCase))&&(text.Length==0||o.OrderId.Contains(text,StringComparison.CurrentCultureIgnoreCase)||o.Items.Any(i=>i.Sku.Contains(text,StringComparison.CurrentCultureIgnoreCase)||i.Title.Contains(text,StringComparison.CurrentCultureIgnoreCase)))).ToList(); return new(rows.Skip(offset).Take(limit).ToList(),rows.Count); }
 public void SaveManual(OrderSnapshot order)
 {
  var copy=order.Copy();OrdersRules.Validate(copy);
  foreach(var s in copy.Shipments){var last=s.Events.LastOrDefault();if(last==null||last.State!=s.State){s.Source="Yerel / manuel";s.Events.Add(new(DateTimeOffset.UtcNow,s.State,s.Source));}}
  if(copy.Source!="Etsy API")copy.Source="Yerel / manuel";
  Save([copy],true);
 }
 public void SaveBatch(IReadOnlyList<OrderSnapshot> orders)=>Save(orders,false);
 void Save(IReadOnlyList<OrderSnapshot> orders,bool manual)
 {
  foreach(var o in orders)OrdersRules.Validate(o);
  using var c=Open();using var tx=c.BeginTransaction();
  foreach(var original in orders)
  {
   var o=original.Copy();using var get=c.CreateCommand();get.Transaction=tx;get.CommandText="SELECT payload FROM orders WHERE marketplace=$m AND shop=$s AND id=$i";Key(get,o);
   var payload=get.ExecuteScalar() as string;
   if(payload!=null&&!manual){var previous=JsonSerializer.Deserialize<OrderSnapshot>(payload)!;if(previous.UpdatedAt>o.UpdatedAt)continue;
    foreach(var old in previous.Shipments){var current=o.Shipments.FirstOrDefault(s=>s.Id==old.Id);if(current==null){o.Shipments.Add(old);continue;}current.Events=old.Events;
     if(old.Source=="Yerel / manuel"&&old.Events.Count>0){current.State=old.State;current.Source=old.Source;current.TrackingUrl=old.TrackingUrl;current.Carrier=old.Carrier;current.TrackingNumber=old.TrackingNumber;}
    }
   }
   using var put=c.CreateCommand();put.Transaction=tx;put.CommandText="INSERT INTO orders VALUES($m,$s,$i,$p) ON CONFLICT(marketplace,shop,id) DO UPDATE SET payload=excluded.payload";Key(put,o);put.Parameters.AddWithValue("$p",JsonSerializer.Serialize(o));put.ExecuteNonQuery();
  }
  tx.Commit();
 }
 static void Key(SqliteCommand cmd,OrderSnapshot o){cmd.Parameters.AddWithValue("$m",o.Marketplace);cmd.Parameters.AddWithValue("$s",o.ShopId);cmd.Parameters.AddWithValue("$i",o.OrderId);}
}
