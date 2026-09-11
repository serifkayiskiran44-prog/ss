using Microsoft.Data.Sqlite;
using System.IO;
using System.Text.Json;
namespace TrMarketplaceHubDesktop;
public sealed class ChannelProductsStore
{
 readonly string connectionString;
 public ChannelProductsStore(string? directory=null)
 {
  directory??=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MonoBridgeDesktop");Directory.CreateDirectory(directory);
  connectionString=new SqliteConnectionStringBuilder{DataSource=Path.Combine(directory,"channel_products.db")}.ToString();
  using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="CREATE TABLE IF NOT EXISTS ChannelPlans(ChannelId TEXT NOT NULL,ShopId TEXT NOT NULL,ProductId TEXT NOT NULL,Json TEXT NOT NULL,PRIMARY KEY(ChannelId,ShopId,ProductId))";cmd.ExecuteNonQuery();
 }
 SqliteConnection Open(){var c=new SqliteConnection(connectionString);c.Open();return c;}
 static void Identity(string channel,string shop,string product){if(string.IsNullOrWhiteSpace(channel)||string.IsNullOrWhiteSpace(shop)||string.IsNullOrWhiteSpace(product))throw new ArgumentException("Kanal, mağaza anahtarı ve merkez ürün kimliği zorunlu.");}
 public ChannelProductPlan? Find(string channel,string shop,string product)
 {
  Identity(channel,shop,product);using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT Json FROM ChannelPlans WHERE ChannelId=$channel AND ShopId=$shop AND ProductId=$product";cmd.Parameters.AddWithValue("$channel",channel.Trim().ToLowerInvariant());cmd.Parameters.AddWithValue("$shop",shop.Trim());cmd.Parameters.AddWithValue("$product",product);return cmd.ExecuteScalar() is string json?JsonSerializer.Deserialize<ChannelProductPlan>(json):null;
 }
 public void Save(ChannelProductPlan plan)
 {
  Identity(plan.ChannelId,plan.ShopId,plan.ProductId);
  if(plan.PlannedPrice<0||plan.PlannedStock<0)throw new ArgumentException("Plan fiyatı ve stok negatif olamaz.");
  if(plan.Currency is null||plan.Currency.Length!=3||!plan.Currency.All(c=>c>='A'&&c<='Z'))throw new ArgumentException("Para birimini üç büyük harfle girin (USD, EUR, RUB, TRY).");
  if(!string.IsNullOrEmpty(plan.ListingUrl)&&(!Uri.TryCreate(plan.ListingUrl,UriKind.Absolute,out var uri)||(uri.Scheme!="https"&&uri.Scheme!="http")||!string.IsNullOrEmpty(uri.UserInfo)))throw new ArgumentException("İlan bağlantısı geçerli bir HTTP/HTTPS adresi olmalı.");
  plan.ChannelId=plan.ChannelId.Trim().ToLowerInvariant();plan.ShopId=plan.ShopId.Trim();plan.UpdatedUtc=DateTime.UtcNow;
  using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="INSERT INTO ChannelPlans(ChannelId,ShopId,ProductId,Json) VALUES($channel,$shop,$product,$json) ON CONFLICT(ChannelId,ShopId,ProductId) DO UPDATE SET Json=excluded.Json";cmd.Parameters.AddWithValue("$channel",plan.ChannelId);cmd.Parameters.AddWithValue("$shop",plan.ShopId);cmd.Parameters.AddWithValue("$product",plan.ProductId);cmd.Parameters.AddWithValue("$json",JsonSerializer.Serialize(plan));cmd.ExecuteNonQuery();
 }
}
