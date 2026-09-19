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
public sealed partial class OrdersStore
{
 public sealed record OrderPage(IReadOnlyList<OrderSnapshot> Items,int Total);
 public sealed record SyncState(string ConnectionId,string Channel,string ShopId,long ConnectionRevision,DateTime CursorUtc,
  int FailureCount,DateTime? NextAttemptUtc,string LastError,long Version,DateTime UpdatedUtc);
 readonly string connectionString;
 readonly string directory;
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
  directory=Path.GetFullPath(directory??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MonoBridgeDesktop"));this.directory=directory;Directory.CreateDirectory(directory);
  connectionString=new SqliteConnectionStringBuilder{DataSource=Path.Combine(directory,"orders.db"),DefaultTimeout=15,Pooling=true}.ToString();
  using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="""
   PRAGMA journal_mode=WAL;PRAGMA synchronous=NORMAL;PRAGMA busy_timeout=15000;
   CREATE TABLE IF NOT EXISTS orders(marketplace TEXT NOT NULL,shop TEXT NOT NULL,id TEXT NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(marketplace,shop,id));
   CREATE INDEX IF NOT EXISTS IX_orders_marketplace_shop ON orders(marketplace,shop);
   CREATE INDEX IF NOT EXISTS IX_orders_status ON orders(json_extract(payload,'$.RawStatus'));
   CREATE INDEX IF NOT EXISTS IX_orders_updated ON orders(json_extract(payload,'$.UpdatedAt') DESC);
   CREATE TABLE IF NOT EXISTS order_sync_accounts(
    connection_id TEXT PRIMARY KEY,channel TEXT NOT NULL,shop_id TEXT NOT NULL,connection_revision INTEGER NOT NULL,
    cursor_utc TEXT NOT NULL,failure_count INTEGER NOT NULL CHECK(failure_count>=0),next_attempt_utc TEXT NULL,
    last_error TEXT NOT NULL,version INTEGER NOT NULL CHECK(version>=1),updated_utc TEXT NOT NULL);
   """;cmd.ExecuteNonQuery();SchemaVersion.Ensure(c);
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
  if(OrdersRules.IsRemoteOrder(copy))throw new InvalidOperationException("Uzak kaynaklı siparişler salt okunurdur; yerel kayda dönüştürülemez.");
  foreach(var s in copy.Shipments){var last=s.Events.LastOrDefault();if(last==null||last.State!=s.State){s.Source="Yerel / manuel";s.Events.Add(new(DateTimeOffset.UtcNow,s.State,s.Source));}}
  if(copy.Source!="Etsy API")copy.Source="Yerel / manuel";
  Save([copy],true);
 }
 public void SaveBatch(IReadOnlyList<OrderSnapshot> orders)=>Save(orders,false);

 internal static bool SaveAttached(SqliteConnection connection,SqliteTransaction transaction,OrderSnapshot original)
 {
  OrdersRules.Validate(original);var order=original.Copy();
  using(var get=connection.CreateCommand())
  {
   get.Transaction=transaction;get.CommandText="SELECT payload FROM ordersdb.orders WHERE marketplace=$m AND shop=$s AND id=$i";Key(get,order);
   if(get.ExecuteScalar() is string payload)
   {
    if(!TryReadOrder(order.Marketplace,order.ShopId,order.OrderId,payload,out var previous,out var corrupt))throw new OrderRowCorruptException(order.Marketplace,order.ShopId,order.OrderId,corrupt!.Reason);
    if(previous!.UpdatedAt>order.UpdatedAt)return false;
    foreach(var old in previous.Shipments)
    {
     var current=order.Shipments.FirstOrDefault(shipment=>shipment.Id==old.Id);if(current is null){order.Shipments.Add(old);continue;}current.Events=old.Events;
     if(old.Source=="Yerel / manuel"&&old.Events.Count>0){current.State=old.State;current.Source=old.Source;current.TrackingUrl=old.TrackingUrl;current.Carrier=old.Carrier;current.TrackingNumber=old.TrackingNumber;}
    }
   }
  }
  using var put=connection.CreateCommand();put.Transaction=transaction;put.CommandText="INSERT INTO ordersdb.orders VALUES($m,$s,$i,$p) ON CONFLICT(marketplace,shop,id) DO UPDATE SET payload=excluded.payload";Key(put,order);put.Parameters.AddWithValue("$p",JsonSerializer.Serialize(order));put.ExecuteNonQuery();return true;
 }

 public SyncState? GetSyncState(string connectionId)
 {
  if(string.IsNullOrWhiteSpace(connectionId))throw new ArgumentException("Bağlantı kimliği gerekli.",nameof(connectionId));
  using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT channel,shop_id,connection_revision,cursor_utc,failure_count,next_attempt_utc,last_error,version,updated_utc FROM order_sync_accounts WHERE connection_id=$id";cmd.Parameters.AddWithValue("$id",connectionId);
  using var r=cmd.ExecuteReader();if(!r.Read())return null;
  return new(connectionId,r.GetString(0),r.GetString(1),r.GetInt64(2),ParseUtc(r.GetString(3)),r.GetInt32(4),r.IsDBNull(5)?null:ParseUtc(r.GetString(5)),r.GetString(6),r.GetInt64(7),ParseUtc(r.GetString(8)));
 }

 internal SyncState SaveSyncState(SyncState state,long expectedVersion,MarketplaceConnection expected)
 {
  if(state.ConnectionId.Length is <1 or >512||state.Channel.Length is <1 or >80||state.ShopId.Length is <1 or >200||state.LastError.Length>500||state.FailureCount<0)throw new ArgumentException("Sipariş senkron durumu geçersiz.");
  if(state.ConnectionId!=expected.Id||state.Channel!=expected.Channel||state.ShopId!=expected.ShopId||state.ConnectionRevision!=expected.Revision)throw new InvalidOperationException("Sipariş senkron durumu bağlantı kimliğiyle uyuşmuyor.");
  using var c=Open();using(var attach=c.CreateCommand()){attach.CommandText="ATTACH DATABASE $path AS catalogdb";attach.Parameters.AddWithValue("$path",Path.Combine(directory,"catalog.db"));attach.ExecuteNonQuery();}SqliteTransaction? tx=null;
  try
  {
   tx=c.BeginTransaction(deferred:false);ValidateSyncConnectionFence(c,tx,expected);
   long currentVersion=0;using(var get=c.CreateCommand()){get.Transaction=tx;get.CommandText="SELECT version FROM order_sync_accounts WHERE connection_id=$id";get.Parameters.AddWithValue("$id",state.ConnectionId);currentVersion=get.ExecuteScalar() as long? ?? 0;}
   if(currentVersion!=expectedVersion)throw new InvalidOperationException("Sipariş senkron durumu başka bir işlemde değişti; sonuç uygulanmadı.");
   var nextVersion=checked(expectedVersion+1);using(var put=c.CreateCommand()){put.Transaction=tx;put.CommandText="""
    INSERT INTO order_sync_accounts(connection_id,channel,shop_id,connection_revision,cursor_utc,failure_count,next_attempt_utc,last_error,version,updated_utc)
    VALUES($id,$channel,$shop,$revision,$cursor,$failures,$next,$error,$version,$updated)
    ON CONFLICT(connection_id) DO UPDATE SET channel=excluded.channel,shop_id=excluded.shop_id,connection_revision=excluded.connection_revision,
     cursor_utc=excluded.cursor_utc,failure_count=excluded.failure_count,next_attempt_utc=excluded.next_attempt_utc,
     last_error=excluded.last_error,version=excluded.version,updated_utc=excluded.updated_utc
    """;put.Parameters.AddWithValue("$id",state.ConnectionId);put.Parameters.AddWithValue("$channel",state.Channel);put.Parameters.AddWithValue("$shop",state.ShopId);put.Parameters.AddWithValue("$revision",state.ConnectionRevision);put.Parameters.AddWithValue("$cursor",state.CursorUtc.ToUniversalTime().ToString("O",System.Globalization.CultureInfo.InvariantCulture));put.Parameters.AddWithValue("$failures",state.FailureCount);put.Parameters.AddWithValue("$next",state.NextAttemptUtc.HasValue?state.NextAttemptUtc.Value.ToUniversalTime().ToString("O",System.Globalization.CultureInfo.InvariantCulture):DBNull.Value);put.Parameters.AddWithValue("$error",state.LastError);put.Parameters.AddWithValue("$version",nextVersion);put.Parameters.AddWithValue("$updated",state.UpdatedUtc.ToUniversalTime().ToString("O",System.Globalization.CultureInfo.InvariantCulture));put.ExecuteNonQuery();}
   tx.Commit();return state with{Version=nextVersion};
  }
  finally{tx?.Dispose();using var detach=c.CreateCommand();detach.CommandText="DETACH DATABASE catalogdb";detach.ExecuteNonQuery();}
 }

 static void ValidateSyncConnectionFence(SqliteConnection connection,SqliteTransaction transaction,MarketplaceConnection expected)
 {
  using var command=connection.CreateCommand();command.Transaction=transaction;command.CommandText="SELECT Channel,ShopId,Enabled,Status,Revision,LastTestUtc FROM catalogdb.MarketplaceConnections WHERE Id=$id";command.Parameters.AddWithValue("$id",expected.Id);using var reader=command.ExecuteReader();
  if(!reader.Read()||reader.GetString(0)!=expected.Channel||reader.GetString(1)!=expected.ShopId||reader.GetInt64(2)!=1||reader.GetInt64(4)!=expected.Revision)throw new InvalidOperationException("Mağaza bağlantısı değişti veya devre dışı; senkron durumu uygulanmadı.");
  var status=reader.GetString(3);var lastTest=reader.IsDBNull(5)?null:reader.GetString(5);reader.Close();if(lastTest is not null&&!DateTime.TryParse(lastTest,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.RoundtripKind,out _))throw new InvalidOperationException("Mağaza bağlantısı sağlık kaydı bozuk; senkron durumu uygulanmadı.");if(status.Equals("FAILED",StringComparison.OrdinalIgnoreCase)||status.Equals("LIVE_API_BLOCKED",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Mağaza bağlantısı işlem için uygun değil.");
  var seeded=expected.Id.Equals(expected.Channel+":default",StringComparison.OrdinalIgnoreCase)&&expected.ShopId.Equals("default",StringComparison.OrdinalIgnoreCase);if(!seeded)return;
  var verified=lastTest is not null&&(status.Equals("CONNECTED",StringComparison.OrdinalIgnoreCase)||status.Equals("CONNECTED_READ_ONLY",StringComparison.OrdinalIgnoreCase));if(verified)return;
  using var marker=connection.CreateCommand();marker.Transaction=transaction;marker.CommandText="SELECT 1 FROM catalogdb.MarketplaceCredentialMigrations WHERE Channel=$channel AND ConnectionId=$id AND ShopId=$shop";marker.Parameters.AddWithValue("$channel",expected.Channel);marker.Parameters.AddWithValue("$id",expected.Id);marker.Parameters.AddWithValue("$shop",expected.ShopId);if(marker.ExecuteScalar() is null)throw new InvalidOperationException("Varsayılan mağaza hesabı doğrulanmadı; senkron durumu uygulanmadı.");
 }

 static DateTime ParseUtc(string value)=>DateTime.Parse(value,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
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
    // Equal remote timestamps are intentionally re-evaluated: a local account
    // binding repair can resolve an order without the marketplace changing it.
    if(previous!.UpdatedAt>o.UpdatedAt)continue;
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
