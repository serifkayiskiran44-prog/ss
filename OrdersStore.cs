using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
namespace TrMarketplaceHubDesktop;
/// Bounded diagnostics only (identity + a short reason code + detection time) -
/// never the raw payload/customer data - for an `orders` row that failed to
/// deserialize, failed domain re-validation, or whose payload identity does not
/// match its SQL primary key. Mirrors CatalogStore's CorruptProductRow pattern.
public sealed record CorruptOrderRow(string Marketplace,string ShopId,string OrderId,string Reason,DateTime DetectedUtc);
/// Raised by Save/SaveBatch when a non-manual merge finds the existing durable
/// row for the same key corrupt - the write is fail-closed rather than merging
/// (or overwriting) from invalid data; nothing in the batch is committed.
public sealed class OrderRowCorruptException : Exception
{
    public string Marketplace { get; } public string ShopId { get; } public string OrderId { get; }
    public OrderRowCorruptException(string marketplace,string shopId,string orderId,string reason)
        : base($"Sipariş kaydı bozuk (REVIEW_REQUIRED): {reason}") { Marketplace=marketplace; ShopId=shopId; OrderId=orderId; }
}
public sealed class OrdersStore
{
 public sealed record OrderPage(IReadOnlyList<OrderSnapshot> Items,int Total);
 readonly string connectionString;
 const int MaxPayloadBytes=4*1024*1024;
 /// Deserializes and re-validates a persisted order payload against its own SQL
 /// primary key. A row that fails any check is never silently materialized as a
 /// normal order, never rewritten by an ordinary read, and never merged into.
 static bool TryReadOrder(string marketplace,string shop,string id,string payload,out OrderSnapshot? order,out CorruptOrderRow? corrupt)
 {
  order=null;string? reason=null;
  if(string.IsNullOrWhiteSpace(payload))reason="boş kayıt";
  else if(System.Text.Encoding.UTF8.GetByteCount(payload)>MaxPayloadBytes)reason="kayıt boyutu sınırı aşıyor";
  else
  {
   OrderSnapshot? candidate;
   try{candidate=JsonSerializer.Deserialize<OrderSnapshot>(payload);}
   catch(JsonException){candidate=null;reason="geçersiz JSON";}
   if(reason is null)
   {
    if(candidate is null)reason="boş JSON";
    else if(candidate.Marketplace!=marketplace||candidate.ShopId!=shop||candidate.OrderId!=id)reason="kimlik uyuşmazlığı (yanlış pazaryeri/mağaza/sipariş)";
    else{try{OrdersRules.Validate(candidate);}catch(ArgumentException ex){reason=ex.Message;}}
    if(reason is null)order=candidate;
   }
  }
  corrupt=reason is null?null:new(marketplace,shop,id,reason,DateTime.UtcNow);
  return reason is null;
 }
 public OrdersStore(string? directory=null)
 {
  directory??=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MonoBridgeDesktop");Directory.CreateDirectory(directory);
  connectionString=new SqliteConnectionStringBuilder{DataSource=Path.Combine(directory,"orders.db"),DefaultTimeout=15,Pooling=true}.ToString();
  using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="PRAGMA journal_mode=WAL;PRAGMA synchronous=NORMAL;PRAGMA busy_timeout=15000;CREATE TABLE IF NOT EXISTS orders(marketplace TEXT NOT NULL,shop TEXT NOT NULL,id TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(marketplace,shop,id));CREATE INDEX IF NOT EXISTS IX_orders_marketplace_shop ON orders(marketplace,shop);CREATE INDEX IF NOT EXISTS IX_orders_status ON orders(json_extract(payload,'$.RawStatus'));CREATE INDEX IF NOT EXISTS IX_orders_updated ON orders(json_extract(payload,'$.UpdatedAt') DESC)";cmd.ExecuteNonQuery();SchemaVersion.Ensure(c);
 }
 SqliteConnection Open(){var c=new SqliteConnection(connectionString);c.Open();using var pragma=c.CreateCommand();pragma.CommandText="PRAGMA busy_timeout=15000";pragma.ExecuteNonQuery();return c;}
 public List<OrderSnapshot> ReadAll(){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT marketplace,shop,id,payload FROM orders";using var r=cmd.ExecuteReader();var rows=new List<OrderSnapshot>();while(r.Read())if(TryReadOrder(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),out var order,out _))rows.Add(order!);return rows.OrderByDescending(o=>o.UpdatedAt).ToList();}
 /// Read-only diagnostics: identity + bounded reason code only, never raw
 /// payload/customer data. Corrupt rows are never auto-deleted/overwritten by
 /// ReadAll/ReadPage; this is the only way to discover them for operator repair.
 public IReadOnlyList<CorruptOrderRow> CorruptOrders(){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT marketplace,shop,id,payload FROM orders";using var r=cmd.ExecuteReader();var rows=new List<CorruptOrderRow>();while(r.Read())if(!TryReadOrder(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),out _,out var corrupt))rows.Add(corrupt!);return rows;}
 public OrderPage ReadPage(string? marketplace=null,string? shopId=null,string? status=null,string? query=null,int offset=0,int limit=100)
 { if(offset<0||limit<1||limit>1000)throw new ArgumentOutOfRangeException(nameof(limit)); var text=query?.Trim()??""; using var c=Open(); var where="WHERE ($m='' OR marketplace=$m) AND ($s='' OR shop=$s) AND ($st='' OR json_extract(payload,'$.RawStatus')=$st) AND ($q='' OR id LIKE $like OR payload LIKE $like)"; using var count=c.CreateCommand(); count.CommandText="SELECT COUNT(*) FROM orders "+where; AddFilter(count,marketplace,shopId,status,text); var total=Convert.ToInt32(count.ExecuteScalar()); using var page=c.CreateCommand(); page.CommandText="SELECT marketplace,shop,id,payload FROM orders "+where+" ORDER BY json_extract(payload,'$.UpdatedAt') DESC LIMIT $limit OFFSET $offset"; AddFilter(page,marketplace,shopId,status,text); page.Parameters.AddWithValue("$limit",limit); page.Parameters.AddWithValue("$offset",offset); using var reader=page.ExecuteReader(); var items=new List<OrderSnapshot>(); while(reader.Read()) if(TryReadOrder(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),out var order,out _)) items.Add(order!); return new(items,total); }
 static void AddFilter(SqliteCommand command,string? marketplace,string? shopId,string? status,string text){command.Parameters.AddWithValue("$m",marketplace?.Trim()??"");command.Parameters.AddWithValue("$s",shopId?.Trim()??"");command.Parameters.AddWithValue("$st",status?.Trim()??"");command.Parameters.AddWithValue("$q",text);command.Parameters.AddWithValue("$like",$"%{text}%");}
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
   if(payload!=null&&!manual){
    if(!TryReadOrder(o.Marketplace,o.ShopId,o.OrderId,payload,out var previous,out var corrupt))throw new OrderRowCorruptException(o.Marketplace,o.ShopId,o.OrderId,corrupt!.Reason);
    if(previous!.UpdatedAt>=o.UpdatedAt)continue;
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
