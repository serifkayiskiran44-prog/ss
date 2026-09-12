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
  connectionString=new SqliteConnectionStringBuilder{DataSource=Path.Combine(directory,"orders.db"),DefaultTimeout=15,Pooling=true}.ToString();
  using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="PRAGMA journal_mode=WAL;PRAGMA synchronous=NORMAL;PRAGMA busy_timeout=15000;CREATE TABLE IF NOT EXISTS orders(marketplace TEXT NOT NULL,shop TEXT NOT NULL,id TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(marketplace,shop,id));CREATE INDEX IF NOT EXISTS IX_orders_marketplace_shop ON orders(marketplace,shop);CREATE INDEX IF NOT EXISTS IX_orders_status ON orders(json_extract(payload,'$.RawStatus'));CREATE INDEX IF NOT EXISTS IX_orders_updated ON orders(json_extract(payload,'$.UpdatedAt') DESC);"+
  // ReadPage always filters by marketplace/shop (the common "one store's order list" case) and always sorts
  // by UpdatedAt; the single-column indexes above cannot be combined for both, so SQLite fell back to a
  // separate temp-b-tree sort. This compound index lets that whole query -- filter and order -- be satisfied
  // by one index walk instead (confirmed via EXPLAIN QUERY PLAN in OrderListQueryPlanTests).
  "CREATE INDEX IF NOT EXISTS IX_orders_shop_updated ON orders(marketplace,shop,json_extract(payload,'$.UpdatedAt') DESC)";cmd.ExecuteNonQuery();SchemaVersion.Ensure(c);
 }
 SqliteConnection Open(){var c=SqliteConnectionPolicy.Open(connectionString);using var pragma=c.CreateCommand();pragma.CommandText="PRAGMA busy_timeout=15000";pragma.ExecuteNonQuery();return c;}
 public List<OrderSnapshot> ReadAll(){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT payload FROM orders";using var r=cmd.ExecuteReader();var rows=new List<OrderSnapshot>();while(r.Read())rows.Add(JsonSerializer.Deserialize<OrderSnapshot>(r.GetString(0))!);return rows.OrderByDescending(o=>o.UpdatedAt).ToList();}
 public OrderPage ReadPage(string? marketplace=null,string? shopId=null,string? status=null,string? query=null,int offset=0,int limit=100)
 { if(offset<0||limit<1||limit>1000)throw new ArgumentOutOfRangeException(nameof(limit)); var text=query?.Trim()??""; using var c=Open();
  // The payload is System.Text.Json output, which escapes every non-ASCII character (the dotless i in "Kırmızı"
  // is stored as a backslash-u escape, u0131), so matching the raw query against the payload silently found
  // nothing for any Turkish text (#787). The query is therefore matched in both forms -- raw, for ids and any
  // payload written unescaped, and JSON-encoded exactly as the serializer would write it.
  var where="WHERE ($m='' OR marketplace=$m) AND ($s='' OR shop=$s) AND ($st='' OR json_extract(payload,'$.RawStatus')=$st) AND ($q='' OR id LIKE $like OR payload LIKE $like OR payload LIKE $likeJson)"; using var count=c.CreateCommand(); count.CommandText="SELECT COUNT(*) FROM orders "+where; AddFilter(count,marketplace,shopId,status,text); var total=Convert.ToInt32(count.ExecuteScalar()); using var page=c.CreateCommand(); page.CommandText="SELECT payload FROM orders "+where+" ORDER BY json_extract(payload,'$.UpdatedAt') DESC LIMIT $limit OFFSET $offset"; AddFilter(page,marketplace,shopId,status,text); page.Parameters.AddWithValue("$limit",limit); page.Parameters.AddWithValue("$offset",offset); using var reader=page.ExecuteReader(); var items=new List<OrderSnapshot>(); while(reader.Read()) items.Add(JsonSerializer.Deserialize<OrderSnapshot>(reader.GetString(0))!); return new(items,total); }
 static void AddFilter(SqliteCommand command,string? marketplace,string? shopId,string? status,string text){command.Parameters.AddWithValue("$m",marketplace?.Trim()??"");command.Parameters.AddWithValue("$s",shopId?.Trim()??"");command.Parameters.AddWithValue("$st",status?.Trim()??"");command.Parameters.AddWithValue("$q",text);command.Parameters.AddWithValue("$like",$"%{text}%");command.Parameters.AddWithValue("$likeJson",$"%{System.Text.Encodings.Web.JavaScriptEncoder.Default.Encode(text)}%");}
 /// <summary>Exact lookup of one order by its (marketplace, shop, order number) identity; null when that shop has no such order.</summary>
 public OrderSnapshot? Find(string marketplace,string shopId,string orderId){if(new[]{marketplace,shopId,orderId}.Any(string.IsNullOrWhiteSpace))throw new ArgumentException("Pazaryeri, mağaza ve sipariş numarası zorunlu.");using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT payload FROM orders WHERE marketplace=$m COLLATE NOCASE AND shop=$s AND id=$i";cmd.Parameters.AddWithValue("$m",marketplace.Trim());cmd.Parameters.AddWithValue("$s",shopId.Trim());cmd.Parameters.AddWithValue("$i",orderId.Trim());return cmd.ExecuteScalar() is string json?JsonSerializer.Deserialize<OrderSnapshot>(json):null;}
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
   if(payload!=null&&!manual){var previous=JsonSerializer.Deserialize<OrderSnapshot>(payload)!;if(previous.UpdatedAt>=o.UpdatedAt)continue;
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
