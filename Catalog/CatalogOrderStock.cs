using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace TrMarketplaceHubDesktop.Catalog;

public sealed record OrderStockMovement(string ProductId,string Sku,int Quantity,int StockBefore,int StockAfter);
public sealed record OrderStockReceipt(string Marketplace,string ShopId,string OrderId,DateTime AppliedUtc,List<OrderStockMovement> Movements);
public sealed record OrderStockResult(bool AlreadyApplied,OrderStockReceipt Receipt);
/// Bounded diagnostics only (order identity, a short reason, detection time) -
/// never Payload/Json/movement/product content - for an OrderStockReceipts row
/// that failed decode or internal-consistency validation. See CatalogStore's
/// CorruptProductRow for the same pattern.
public sealed record CorruptOrderStockReceipt(string Marketplace,string ShopId,string OrderId,string Reason,DateTime DetectedUtc);
/// Raised whenever an existing OrderStockReceipts row for an order key cannot be
/// trusted as durable idempotency proof - malformed JSON, an identity mismatch
/// between the row's own key and the JSON payload, or an internally inconsistent
/// movement list. This is a hard stop: the row is the only evidence that this
/// order's inventory mutation already happened, so it is never treated as
/// "missing" (which would let a second stock decrement occur) and never
/// silently repaired/overwritten by a normal apply.
public sealed class OrderStockReceiptCorruptException : Exception
{
 public string Marketplace { get; } public string ShopId { get; } public string OrderId { get; }
 public OrderStockReceiptCorruptException(string marketplace,string shopId,string orderId,string reason) : base($"Sipariş stok makbuzu bozuk (REVIEW_REQUIRED); ikinci kez stok düşümü engellendi: {reason}") { Marketplace=marketplace; ShopId=shopId; OrderId=orderId; }
}

public partial class CatalogStore
{
 static void InitializeOrderStock(SqliteConnection c)
 {
  using var cmd=c.CreateCommand();
  cmd.CommandText="""
   CREATE TABLE IF NOT EXISTS OrderStockReceipts(Marketplace TEXT NOT NULL,ShopId TEXT NOT NULL,OrderId TEXT NOT NULL,Payload TEXT NOT NULL,Json TEXT NOT NULL,PRIMARY KEY(Marketplace,ShopId,OrderId));
   CREATE TABLE IF NOT EXISTS OrderStockMovements(Marketplace TEXT NOT NULL,ShopId TEXT NOT NULL,OrderId TEXT NOT NULL,ProductId TEXT NOT NULL,Sku TEXT NOT NULL,Quantity INTEGER NOT NULL,StockBefore INTEGER NOT NULL,StockAfter INTEGER NOT NULL,AppliedUtc TEXT NOT NULL,PRIMARY KEY(Marketplace,ShopId,OrderId,ProductId));
   CREATE TABLE IF NOT EXISTS OrderStockRestores(Marketplace TEXT NOT NULL,ShopId TEXT NOT NULL,OrderId TEXT NOT NULL,ActionKey TEXT NOT NULL,Payload TEXT NOT NULL,AppliedUtc TEXT NOT NULL,PRIMARY KEY(Marketplace,ShopId,OrderId));
   """;
  cmd.ExecuteNonQuery();
 }
 static void ValidateOrderStockIdentity(string marketplace,string shopId,string orderId)
 {
  if(new[]{marketplace,shopId,orderId}.Any(string.IsNullOrWhiteSpace))throw new ArgumentException("Pazaryeri, mağaza ve sipariş kimliği zorunlu.");
 }
 static void OrderStockIdentityParams(SqliteCommand cmd,string marketplace,string shopId,string orderId)
 {
  cmd.Parameters.AddWithValue("$marketplace",marketplace);cmd.Parameters.AddWithValue("$shop",shopId);cmd.Parameters.AddWithValue("$order",orderId);
 }
 const int MaxReceiptJsonBytes=200_000;
 /// A corrupt receipt is never "missing": it throws OrderStockReceiptCorruptException
 /// so a caller (order list, status display) can never mistake "review needed" for
 /// "stock not yet applied" - the latter would invite a second decrement elsewhere.
 public OrderStockReceipt? GetOrderStockStatus(string marketplace,string shopId,string orderId)
 {
  ValidateOrderStockIdentity(marketplace,shopId,orderId);
  using var c=Open();using var cmd=c.CreateCommand();
  cmd.CommandText="SELECT Json FROM OrderStockReceipts WHERE Marketplace=$marketplace AND ShopId=$shop AND OrderId=$order";
  OrderStockIdentityParams(cmd,marketplace,shopId,orderId);
  if(cmd.ExecuteScalar() is not string json)return null;
  if(!TryReadReceipt(json,marketplace,shopId,orderId,out var receipt,out var reason))throw new OrderStockReceiptCorruptException(marketplace,shopId,orderId,reason!);
  return receipt;
 }
 /// Bounded diagnostics for every receipt row that fails decode/consistency
 /// validation - never Payload/Json/movement content.
 public IReadOnlyList<CorruptOrderStockReceipt> CorruptOrderStockReceipts()
 {
  using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT Marketplace,ShopId,OrderId,Json FROM OrderStockReceipts";
  using var r=cmd.ExecuteReader();var result=new List<CorruptOrderStockReceipt>();
  while(r.Read()){var marketplace=r.GetString(0);var shop=r.GetString(1);var order=r.GetString(2);var json=r.GetString(3);if(!TryReadReceipt(json,marketplace,shop,order,out _,out var reason))result.Add(new(marketplace,shop,order,reason!,DateTime.UtcNow));}
  return result;
 }
 /// Guards every place a persisted receipt is read back as idempotency proof:
 /// bounded payload size (never fully parse an oversized/adversarial JSON blob),
 /// then decode, then verify the JSON's own identity matches the row's key and
 /// its movement list is internally consistent (no duplicate/negative/impossible
 /// quantities, no default/out-of-range AppliedUtc). Only ever wraps a
 /// format/consistency failure - never a SqliteException from the surrounding
 /// command, so a DB-busy/locked error is never misreported as receipt corruption.
 static bool TryReadReceipt(string json,string expectedMarketplace,string expectedShopId,string expectedOrderId,out OrderStockReceipt? receipt,out string? reason)
 {
  receipt=null;reason=null;
  if(json.Length>MaxReceiptJsonBytes){reason="Oversized receipt payload";return false;}
  OrderStockReceipt? parsed;
  try{parsed=JsonSerializer.Deserialize<OrderStockReceipt>(json);}
  catch(Exception ex)when(ex is JsonException or FormatException or ArgumentException){reason="Malformed JSON: "+ex.GetType().Name;return false;}
  if(parsed is null){reason="Empty or null document";return false;}
  if(parsed.Marketplace!=expectedMarketplace||parsed.ShopId!=expectedShopId||parsed.OrderId!=expectedOrderId){reason="Receipt identity mismatch";return false;}
  if(parsed.Movements is null||parsed.Movements.Count==0){reason="Missing movement rows";return false;}
  if(parsed.Movements.Any(m=>string.IsNullOrWhiteSpace(m.ProductId)||string.IsNullOrWhiteSpace(m.Sku))){reason="Movement missing product/sku identity";return false;}
  if(parsed.Movements.Select(m=>m.ProductId).Distinct(StringComparer.Ordinal).Count()!=parsed.Movements.Count){reason="Duplicate ProductId movement";return false;}
  if(parsed.Movements.Any(m=>m.Quantity<=0||m.StockBefore<0||m.StockAfter<0||m.StockAfter!=m.StockBefore-m.Quantity)){reason="Invalid or inconsistent movement quantities";return false;}
  if(parsed.AppliedUtc==default||parsed.AppliedUtc<new DateTime(2000,1,1)||parsed.AppliedUtc>DateTime.UtcNow.AddDays(1)){reason="Invalid AppliedUtc";return false;}
  receipt=parsed;return true;
 }
 public OrderStockResult ApplyOrderStock(string marketplace,string shopId,string orderId,IReadOnlyList<OrderItem> items)
 {
  ValidateOrderStockIdentity(marketplace,shopId,orderId);
  if(items is null||items.Count==0)throw new ArgumentException("Stok düşümü için sipariş satırı zorunlu.");
  var quantities=new SortedDictionary<string,int>(StringComparer.Ordinal);
  foreach(var item in items)
  {
   if(item is null||string.IsNullOrWhiteSpace(item.Sku)||item.Quantity<=0)throw new ArgumentException("Her satırda tam SKU ve pozitif adet zorunlu.");
   quantities[item.Sku]=checked(quantities.GetValueOrDefault(item.Sku)+item.Quantity);
  }
  // Only SKU and summed quantity affect inventory; titles and line ordering do not.
  var payload=JsonSerializer.Serialize(quantities);
  using var c=Open();using var tx=c.BeginTransaction(deferred:false);
  using(var find=c.CreateCommand())
  {
   find.Transaction=tx;find.CommandText="SELECT Payload,Json FROM OrderStockReceipts WHERE Marketplace=$marketplace AND ShopId=$shop AND OrderId=$order";
   OrderStockIdentityParams(find,marketplace,shopId,orderId);
   string? storedPayload=null;string? storedJson=null;
   using(var reader=find.ExecuteReader())if(reader.Read()){storedPayload=reader.GetString(0);storedJson=reader.GetString(1);}
   if(storedJson is not null)
   {
    // Corruption is checked before the payload-equality idempotency check: a
    // receipt that cannot be trusted must never be treated as "confirms this
    // exact payload was already applied" nor as "different payload, reject" -
    // both would let this call proceed past the durable proof this row is
    // supposed to be, one way or another.
    if(!TryReadReceipt(storedJson,marketplace,shopId,orderId,out var existing,out var reason))throw new OrderStockReceiptCorruptException(marketplace,shopId,orderId,reason!);
    if(storedPayload!=payload)throw new InvalidOperationException("Bu sipariş için stok daha önce farklı SKU/adetlerle düşüldü; tekrar uygulanamaz.");
    tx.Commit();return new(true,existing!);
   }
  }
  var at=DateTime.UtcNow;var movements=new List<OrderStockMovement>();var inventoryReference=InventoryLedger.OrderReference(marketplace,shopId,orderId);
  foreach(var pair in quantities)
  {
   var matches=new List<CatalogProduct>();
   using(var find=c.CreateCommand())
   {
    // json_valid(Json)=1 keeps a corrupt CatalogProducts row (see CorruptProductRow)
    // out of this scan instead of crashing sqlite's json_extract or this deserialize.
    find.Transaction=tx;find.CommandText="SELECT Json FROM CatalogProducts WHERE json_valid(Json)=1 AND json_extract(Json,'$.Sku')=$sku COLLATE BINARY LIMIT 2";
    find.Parameters.AddWithValue("$sku",pair.Key);using var reader=find.ExecuteReader();while(reader.Read()){var candidate=JsonSerializer.Deserialize<CatalogProduct>(reader.GetString(0));if(candidate is not null)matches.Add(candidate);}
   }
   if(matches.Count!=1)throw new InvalidOperationException($"SKU {pair.Key}: tek bir merkezi ürün eşleşmesi bulunamadı.");
   var product=matches[0];
   if(!product.Active)throw new InvalidOperationException($"SKU {pair.Key}: ürün pasif.");
   var balance=InventoryLedger.ReadBalance(c,tx,product.Id,InventoryLedger.OnlineLocationId);
   if(balance.Version==0||balance.Quantity!=product.Stock)throw new InvalidOperationException($"SKU {pair.Key}: çevrimiçi bakiye ile katalog stoku uyuşmuyor; stok incelemesi gerekli.");
   if(balance.Quantity<pair.Value)throw new InvalidOperationException($"SKU {pair.Key}: stok yetersiz ({balance.Quantity}/{pair.Value}).");
   var after=checked(balance.Quantity-pair.Value);
   InventoryLedger.SetBalance(c,tx,product.Id,InventoryLedger.OnlineLocationId,after,balance.Version);
   var movement=new OrderStockMovement(product.Id,product.Sku,pair.Value,balance.Quantity,after);
   product.Stock=movement.StockAfter;
   // Preserve local sold stock when supplier XML refreshes its independent stock figure.
   product.LockStock=true;product.UpdatedUtc=at;Put(c,"CatalogProducts",product.Id,product,tx);
   using var cmd=c.CreateCommand();cmd.Transaction=tx;
   cmd.CommandText="INSERT INTO OrderStockMovements VALUES($marketplace,$shop,$order,$product,$sku,$quantity,$before,$after,$at)";
   OrderStockIdentityParams(cmd,marketplace,shopId,orderId);cmd.Parameters.AddWithValue("$product",product.Id);cmd.Parameters.AddWithValue("$sku",pair.Key);cmd.Parameters.AddWithValue("$quantity",pair.Value);cmd.Parameters.AddWithValue("$before",movement.StockBefore);cmd.Parameters.AddWithValue("$after",movement.StockAfter);cmd.Parameters.AddWithValue("$at",at.ToString("O"));cmd.ExecuteNonQuery();movements.Add(movement);
   InventoryLedger.RecordMovement(c,tx,product.Id,InventoryLedger.OnlineLocationId,balance.Quantity,after,InventoryMovementKind.OnlineOrder,inventoryReference,at);
  }
  var receipt=new OrderStockReceipt(marketplace,shopId,orderId,at,movements);
  using(var cmd=c.CreateCommand())
  {
   cmd.Transaction=tx;cmd.CommandText="INSERT INTO OrderStockReceipts VALUES($marketplace,$shop,$order,$payload,$json)";
   OrderStockIdentityParams(cmd,marketplace,shopId,orderId);cmd.Parameters.AddWithValue("$payload",payload);cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(receipt));cmd.ExecuteNonQuery();
  }
  using(var cmd=c.CreateCommand())
  {
   cmd.Transaction=tx;cmd.CommandText="INSERT INTO InventoryOrderReceipts(Marketplace,ShopId,OrderId,Payload,AppliedUtc) VALUES($marketplace,$shop,$order,$payload,$at)";
   OrderStockIdentityParams(cmd,marketplace,shopId,orderId);cmd.Parameters.AddWithValue("$payload",payload);cmd.Parameters.AddWithValue("$at",at.ToString("O"));cmd.ExecuteNonQuery();
  }
  tx.Commit();return new(false,receipt);
 }
}
