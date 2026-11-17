using Microsoft.Data.Sqlite;
using System.IO;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public enum MarketplaceOperation
{
 ProductsRead, OrdersRead, StockWrite, PriceWrite, Shipment,
 ProductManagement, ContentWrite, CategoryWrite, BrandWrite, DeliveryWrite,
 TaxonomyWrite, PropertiesWrite, ShippingWrite, ReadinessWrite, ListingCreate
}
public sealed record MarketplaceCapabilities(IReadOnlySet<MarketplaceOperation> Enabled)
{
 public bool Supports(MarketplaceOperation operation)=>Enabled.Contains(operation);
 public static MarketplaceCapabilities LocalOnly=>new(new HashSet<MarketplaceOperation>());
}
public sealed record MarketplaceMapping(string Channel,string ShopId,string LocalId,string ExternalId);
public sealed record MarketplacePreview(string Channel,string ShopId,string LocalId,string ExternalId,string Version,int? Stock,decimal? Price);
public sealed class MarketplaceMappingStore
{
 readonly string connectionString;
 public MarketplaceMappingStore(string? directory=null){directory??=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MonoBridgeDesktop");Directory.CreateDirectory(directory);connectionString=new SqliteConnectionStringBuilder{DataSource=Path.Combine(directory,"catalog.db")}.ToString();using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="CREATE TABLE IF NOT EXISTS MarketplaceMappings(Channel TEXT NOT NULL,ShopId TEXT NOT NULL,LocalId TEXT NOT NULL,ExternalId TEXT NOT NULL,PRIMARY KEY(Channel,ShopId,LocalId),UNIQUE(Channel,ShopId,ExternalId))";cmd.ExecuteNonQuery();}
 SqliteConnection Open(){var c=new SqliteConnection(connectionString);c.Open();return c;}
 public void Save(MarketplaceMapping mapping){if(new[]{mapping.Channel,mapping.ShopId,mapping.LocalId,mapping.ExternalId}.Any(string.IsNullOrWhiteSpace))throw new ArgumentException("Kanal, mağaza ve eşleme kimlikleri zorunlu.");using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="INSERT INTO MarketplaceMappings VALUES($c,$s,$l,$e) ON CONFLICT(Channel,ShopId,LocalId) DO UPDATE SET ExternalId=excluded.ExternalId";cmd.Parameters.AddWithValue("$c",mapping.Channel.ToLowerInvariant());cmd.Parameters.AddWithValue("$s",mapping.ShopId);cmd.Parameters.AddWithValue("$l",mapping.LocalId);cmd.Parameters.AddWithValue("$e",mapping.ExternalId);cmd.ExecuteNonQuery();}
 public MarketplaceMapping? Find(string channel,string shopId,string localId){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT Channel,ShopId,LocalId,ExternalId FROM MarketplaceMappings WHERE Channel=$c AND ShopId=$s AND LocalId=$l";cmd.Parameters.AddWithValue("$c",channel.ToLowerInvariant());cmd.Parameters.AddWithValue("$s",shopId);cmd.Parameters.AddWithValue("$l",localId);using var r=cmd.ExecuteReader();return r.Read()?new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3)):null;}
}
