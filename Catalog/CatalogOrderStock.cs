using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace TrMarketplaceHubDesktop.Catalog;

public sealed record OrderStockMovement(string ProductId,string Sku,int Quantity,int StockBefore,int StockAfter);
public sealed record OrderStockReceipt(string Marketplace,string ShopId,string OrderId,DateTime AppliedUtc,List<OrderStockMovement> Movements);
public sealed record OrderStockResult(bool AlreadyApplied,OrderStockReceipt Receipt);

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
 public OrderStockReceipt? GetOrderStockStatus(string marketplace,string shopId,string orderId)
 {
  ValidateOrderStockIdentity(marketplace,shopId,orderId);
  using var c=Open();using var cmd=c.CreateCommand();
  cmd.CommandText="SELECT Json FROM OrderStockReceipts WHERE Marketplace=$marketplace AND ShopId=$shop AND OrderId=$order";
  OrderStockIdentityParams(cmd,marketplace,shopId,orderId);
  return cmd.ExecuteScalar() is string json?JsonSerializer.Deserialize<OrderStockReceipt>(json):null;
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
   using var reader=find.ExecuteReader();
   if(reader.Read())
   {
    if(reader.GetString(0)!=payload)throw new InvalidOperationException("Bu sipariş için stok daha önce farklı SKU/adetlerle düşüldü; tekrar uygulanamaz.");
    var existing=JsonSerializer.Deserialize<OrderStockReceipt>(reader.GetString(1))!;
    reader.Close();tx.Commit();return new(true,existing);
   }
  }
  var at=DateTime.UtcNow;var movements=new List<OrderStockMovement>();
  foreach(var pair in quantities)
  {
   var matches=new List<CatalogProduct>();
   using(var find=c.CreateCommand())
   {
    find.Transaction=tx;find.CommandText="SELECT Json FROM CatalogProducts WHERE json_extract(Json,'$.Sku')=$sku COLLATE BINARY LIMIT 2";
    find.Parameters.AddWithValue("$sku",pair.Key);using var reader=find.ExecuteReader();while(reader.Read())matches.Add(JsonSerializer.Deserialize<CatalogProduct>(reader.GetString(0))!);
   }
   if(matches.Count!=1)throw new InvalidOperationException($"SKU {pair.Key}: tek bir merkezi ürün eşleşmesi bulunamadı.");
   var product=matches[0];
   if(!product.Active)throw new InvalidOperationException($"SKU {pair.Key}: ürün pasif.");
   if(product.Stock<pair.Value)throw new InvalidOperationException($"SKU {pair.Key}: stok yetersiz ({product.Stock}/{pair.Value}).");
   var movement=new OrderStockMovement(product.Id,product.Sku,pair.Value,product.Stock,product.Stock-pair.Value);
   product.Stock=movement.StockAfter;
   // Preserve local sold stock when supplier XML refreshes its independent stock figure.
   product.LockStock=true;product.UpdatedUtc=at;Put(c,"CatalogProducts",product.Id,product,tx);
   using var cmd=c.CreateCommand();cmd.Transaction=tx;
   cmd.CommandText="INSERT INTO OrderStockMovements VALUES($marketplace,$shop,$order,$product,$sku,$quantity,$before,$after,$at)";
   OrderStockIdentityParams(cmd,marketplace,shopId,orderId);cmd.Parameters.AddWithValue("$product",product.Id);cmd.Parameters.AddWithValue("$sku",pair.Key);cmd.Parameters.AddWithValue("$quantity",pair.Value);cmd.Parameters.AddWithValue("$before",movement.StockBefore);cmd.Parameters.AddWithValue("$after",movement.StockAfter);cmd.Parameters.AddWithValue("$at",at.ToString("O"));cmd.ExecuteNonQuery();movements.Add(movement);
  }
  var receipt=new OrderStockReceipt(marketplace,shopId,orderId,at,movements);
  using(var cmd=c.CreateCommand())
  {
   cmd.Transaction=tx;cmd.CommandText="INSERT INTO OrderStockReceipts VALUES($marketplace,$shop,$order,$payload,$json)";
   OrderStockIdentityParams(cmd,marketplace,shopId,orderId);cmd.Parameters.AddWithValue("$payload",payload);cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(receipt));cmd.ExecuteNonQuery();
  }
  tx.Commit();return new(false,receipt);
 }
}
