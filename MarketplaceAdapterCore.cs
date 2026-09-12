using Microsoft.Data.Sqlite;
using System.IO;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public enum MarketplaceOperation { ProductsRead, OrdersRead, StockWrite, PriceWrite, Shipment }
public sealed record MarketplaceCapabilities(IReadOnlySet<MarketplaceOperation> Enabled)
{
 public bool Supports(MarketplaceOperation operation)=>Enabled.Contains(operation);
 public static MarketplaceCapabilities LocalOnly=>new(new HashSet<MarketplaceOperation>());
}
public sealed record MarketplaceAdapterDescriptor(string Channel,string ShopId,MarketplaceCapabilities Capabilities,bool LiveApiBlocked);
public sealed record MarketplaceMapping(string Channel,string ShopId,string LocalId,string ExternalId);
public sealed record MarketplacePreview(string Channel,string ShopId,string LocalId,string ExternalId,string Version,int? Stock,decimal? Price);
public interface IMarketplaceAdapter
{
 MarketplaceAdapterDescriptor Descriptor {get;}
 Task TestConnectionAsync(CancellationToken cancellationToken=default);
 Task DispatchAsync(MarketplaceOperation operation,MarketplacePreview preview,bool approved,SyncStore sync,string syncJobId,CancellationToken cancellationToken=default);
}
public sealed class LocalMarketplaceAdapter(MarketplaceAdapterDescriptor descriptor):IMarketplaceAdapter
{
 public MarketplaceAdapterDescriptor Descriptor {get;}=descriptor;
 public Task TestConnectionAsync(CancellationToken cancellationToken=default)=>throw new InvalidOperationException($"{Descriptor.Channel} için doğrulanmış resmi API sözleşmesi yapılandırılmadı (LIVE_API_BLOCKED).");
 public Task DispatchAsync(MarketplaceOperation operation,MarketplacePreview preview,bool approved,SyncStore sync,string syncJobId,CancellationToken cancellationToken=default)
 {if(!Descriptor.Capabilities.Supports(operation))throw new InvalidOperationException($"{Descriptor.Channel}/{operation}: capability desteklenmiyor; HTTP isteği oluşturulmadı.");if(!approved)throw new InvalidOperationException("Canlı işlem için açık onay gerekli.");throw new InvalidOperationException($"{Descriptor.Channel} canlı API'si yapılandırılmadı (LIVE_API_BLOCKED); HTTP isteği oluşturulmadı.");}
}
public static class MarketplaceAdapterRegistry
{
 public static readonly string[] Channels=["ozon","joom","allegro","wish","navlungo"];
 public static IMarketplaceAdapter Create(string channel,string shopId){var normalized=channel.Trim().ToLowerInvariant();if(!Channels.Contains(normalized,StringComparer.Ordinal))throw new ArgumentException("Desteklenmeyen kanal.");if(string.IsNullOrWhiteSpace(shopId))throw new ArgumentException("Mağaza kimliği zorunlu.");return new LocalMarketplaceAdapter(new(normalized,shopId,MarketplaceCapabilities.LocalOnly,true));}
}
public sealed class MarketplaceMappingStore
{
 readonly string connectionString;
 public MarketplaceMappingStore(string? directory=null){directory??=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MonoBridgeDesktop");Directory.CreateDirectory(directory);connectionString=new SqliteConnectionStringBuilder{DataSource=Path.Combine(directory,"catalog.db")}.ToString();using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="CREATE TABLE IF NOT EXISTS MarketplaceMappings(Channel TEXT NOT NULL,ShopId TEXT NOT NULL,LocalId TEXT NOT NULL,ExternalId TEXT NOT NULL,PRIMARY KEY(Channel,ShopId,LocalId),UNIQUE(Channel,ShopId,ExternalId))";cmd.ExecuteNonQuery();}
 SqliteConnection Open(){var c=SqliteConnectionPolicy.Open(connectionString);return c;}
 public void Save(MarketplaceMapping mapping){if(new[]{mapping.Channel,mapping.ShopId,mapping.LocalId,mapping.ExternalId}.Any(string.IsNullOrWhiteSpace))throw new ArgumentException("Kanal, mağaza ve eşleme kimlikleri zorunlu.");using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="INSERT INTO MarketplaceMappings VALUES($c,$s,$l,$e) ON CONFLICT(Channel,ShopId,LocalId) DO UPDATE SET ExternalId=excluded.ExternalId";cmd.Parameters.AddWithValue("$c",mapping.Channel.ToLowerInvariant());cmd.Parameters.AddWithValue("$s",mapping.ShopId);cmd.Parameters.AddWithValue("$l",mapping.LocalId);cmd.Parameters.AddWithValue("$e",mapping.ExternalId);cmd.ExecuteNonQuery();}
 public MarketplaceMapping? Find(string channel,string shopId,string localId){using var c=Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT Channel,ShopId,LocalId,ExternalId FROM MarketplaceMappings WHERE Channel=$c AND ShopId=$s AND LocalId=$l";cmd.Parameters.AddWithValue("$c",channel.ToLowerInvariant());cmd.Parameters.AddWithValue("$s",shopId);cmd.Parameters.AddWithValue("$l",localId);using var r=cmd.ExecuteReader();return r.Read()?new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3)):null;}
}
