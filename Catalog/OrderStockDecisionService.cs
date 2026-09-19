using TrMarketplaceHubDesktop;
namespace TrMarketplaceHubDesktop.Catalog;

public sealed record ResolvedOrderStockLine(string ProductId,string Sku,int Quantity,long ProductGeneration,long BalanceVersion,DateTime ProductUpdatedUtc);
public sealed record ResolvedOrderBinding(string ProductId,string ConnectionId,string RemoteId,string RemoteSku,string RemoteBarcode,bool ManageStock,long Version,DateTime UpdatedUtc);
public sealed record OrderStockDecisionPreview(string Marketplace,string ShopId,string OrderId,IReadOnlyList<ResolvedOrderStockLine> Lines,bool AlreadyApplied,string Summary);
public sealed record AccountOrderStockResolution(OrderSnapshot Order,IReadOnlyList<ResolvedOrderStockLine> Lines,IReadOnlyList<ResolvedOrderBinding> Bindings,bool ReviewRequired,string ReviewReason);
public sealed record AccountOrderApplyResult(bool OrderApplied,bool StockApplied,bool AlreadyApplied);

/// <summary>Every decision binds an order line to an immutable product identity and inventory version.</summary>
public sealed class OrderStockDecisionService
{
 readonly CatalogStore catalog; readonly ProductChannelBindingStore? bindings; readonly InventoryLocationStore inventory;
 public OrderStockDecisionService(CatalogStore catalog){this.catalog=catalog;inventory=new(catalog.DataDirectory);}
 public OrderStockDecisionService(string? directory){catalog=new(directory);bindings=new(directory);inventory=new(directory);}

 public OrderStockDecisionPreview CreatePreview(OrderSnapshot order)
 {
  ArgumentNullException.ThrowIfNull(order);
  if(!OrdersRules.CanApplyOnlineStock(order))throw new InvalidOperationException(order.IsPhysicalSale?"Fiziksel satış çevrimiçi stoktan düşülemez.":"Uzak veya inceleme gerektiren sipariş bu yerel stok işlemine alınamaz.");
  Validate(order);var products=catalog.Products();var matches=new List<(CatalogProduct Product,int Quantity)>();
  foreach(var group in order.Items.GroupBy(line=>line.Sku,StringComparer.Ordinal))
  {
   if(string.IsNullOrWhiteSpace(group.Key))throw new InvalidOperationException("Stok kararı için tam SKU gerekli.");
   var found=products.Where(product=>product.Sku.Equals(group.Key,StringComparison.Ordinal)).ToArray();
   if(found.Length!=1)throw new InvalidOperationException($"SKU {group.Key}: tek bir merkezi ürün eşleşmesi bulunamadı.");
   if(!found[0].Active)throw new InvalidOperationException($"SKU {group.Key}: ürün pasif.");
   matches.Add((found[0],checked(group.Sum(line=>line.Quantity))));
  }
  var existing=catalog.GetOrderStockStatus(order.Marketplace,order.ShopId,order.OrderId);
  return new(order.Marketplace,order.ShopId,order.OrderId,Capture(matches),existing is not null,existing is null?"Stok düşümü için açık onay bekliyor.":"Bu siparişin stok düşümü daha önce uygulandı; tekrar düşülmez.");
 }

 public OrderStockResult ApplyApproved(OrderStockDecisionPreview preview,bool approved)
 {
  ArgumentNullException.ThrowIfNull(preview);if(!approved)throw new InvalidOperationException("Sipariş stok değişikliği için açık onay gerekli.");
  return catalog.ApplyResolvedOrderStock(preview.Marketplace,preview.ShopId,preview.OrderId,preview.Lines);
 }

 public AccountOrderStockResolution ResolveAccountOrder(MarketplaceConnection connection,OrderSnapshot source)
 {
  ArgumentNullException.ThrowIfNull(connection);ArgumentNullException.ThrowIfNull(source);
  if(bindings is null)throw new InvalidOperationException("Hesap kapsamlı sipariş çözümü için veri dizini gerekli.");
  var marketplace=MarketplaceConnectionCatalog.Get(connection.Channel).Name;
  if(!source.Marketplace.Equals(marketplace,StringComparison.OrdinalIgnoreCase)||!source.ShopId.Equals(connection.ShopId,StringComparison.Ordinal))throw new InvalidOperationException("Sipariş kimliği seçili mağaza hesabıyla eşleşmiyor.");
  if(source.Items.Count==0)throw new InvalidOperationException("Sipariş ürün satırı içermiyor.");
  var order=source.Copy();order.Marketplace=marketplace;order.ShopId=connection.ShopId;order.ConnectionId=connection.Id;order.ConnectionDisplayName=connection.DisplayName;order.ReviewRequired=false;order.ReviewReason="";
  var accountBindings=bindings.List(connectionId:connection.Id);var products=catalog.Products().ToDictionary(product=>product.Id,StringComparer.Ordinal);var resolved=new List<(CatalogProduct Product,int Quantity)>();var resolvedBindings=new List<ProductChannelBinding>();var reasons=new List<string>();
  foreach(var line in order.Items)
  {
   line.ProductId="";line.ReviewRequired=false;line.ReviewReason="";
   var candidates=accountBindings.Where(binding=>(!string.IsNullOrWhiteSpace(line.Sku)&&binding.RemoteSku.Equals(line.Sku,StringComparison.Ordinal))||(!string.IsNullOrWhiteSpace(line.Barcode)&&binding.RemoteBarcode.Equals(line.Barcode,StringComparison.Ordinal))).GroupBy(binding=>binding.ProductId,StringComparer.Ordinal).Select(group=>group.First()).ToArray();
   string? reason=candidates.Length switch{0=>"Bu hesapta exact SKU/barkod bağlantısı bulunamadı.",>1=>"Bu hesapta SKU/barkod birden fazla ürüne bağlı.",_ when !candidates[0].ManageStock=>"Bu hesap bağlantısında stok yönetimi kapalı.",_ when !products.ContainsKey(candidates[0].ProductId)=>"Bağlı merkezi ürün bulunamadı.",_ when !products[candidates[0].ProductId].Active=>"Bağlı merkezi ürün pasif.",_=>null};
   if(reason is not null){line.ReviewRequired=true;line.ReviewReason=reason;reasons.Add(reason);continue;}
   var binding=candidates[0];var product=products[binding.ProductId];line.ProductId=product.Id;resolved.Add((product,line.Quantity));resolvedBindings.Add(binding);
  }
  order.ReviewRequired=reasons.Count>0;order.ReviewReason=order.ReviewRequired?string.Join(" ",reasons.Distinct(StringComparer.Ordinal)).Trim():"";
  var lines=order.ReviewRequired?Array.Empty<ResolvedOrderStockLine>():Capture(resolved.GroupBy(item=>item.Product.Id,StringComparer.Ordinal).Select(group=>(group.First().Product,checked(group.Sum(item=>item.Quantity)))));
  var snapshots=order.ReviewRequired?Array.Empty<ResolvedOrderBinding>():resolvedBindings.GroupBy(binding=>binding.ProductId,StringComparer.Ordinal).Select(group=>group.First()).OrderBy(binding=>binding.ProductId,StringComparer.Ordinal).Select(binding=>new ResolvedOrderBinding(binding.ProductId,binding.ConnectionId,binding.RemoteId,binding.RemoteSku,binding.RemoteBarcode,binding.ManageStock,binding.Version,binding.UpdatedUtc)).ToArray();
  return new(order,lines,snapshots,order.ReviewRequired,order.ReviewReason);
 }

 public OrderStockResult ApplyAccepted(AccountOrderStockResolution resolution)
 {
  ArgumentNullException.ThrowIfNull(resolution);if(resolution.ReviewRequired||resolution.Lines.Count==0)throw new InvalidOperationException("İnceleme gerektiren sipariş stoktan düşülemez.");
  return catalog.ApplyResolvedOrderStock(resolution.Order.Marketplace,resolution.Order.ShopId,resolution.Order.OrderId,resolution.Lines);
 }
public AccountOrderApplyResult ApplyAccountOrder(MarketplaceConnection connection,AccountOrderStockResolution resolution,bool deductStock,long? expectedSettingsRevision=null)=>catalog.ApplyAccountOrder(connection,resolution,deductStock,expectedSettingsRevision);

 IReadOnlyList<ResolvedOrderStockLine> Capture(IEnumerable<(CatalogProduct Product,int Quantity)> products)=>products.OrderBy(item=>item.Product.Id,StringComparer.Ordinal).Select(item=>
 {
  if(item.Quantity<=0)throw new InvalidOperationException("Stok kararı için pozitif adet gerekli.");var balance=inventory.GetBalance(item.Product.Id,InventoryLocationStore.OnlineLocationId);
  return new ResolvedOrderStockLine(item.Product.Id,item.Product.Sku,item.Quantity,inventory.GetProductGeneration(item.Product.Id),balance.Version,item.Product.UpdatedUtc);
 }).ToArray();
 static void Validate(OrderSnapshot order){if(new[]{order.Marketplace,order.ShopId,order.OrderId}.Any(string.IsNullOrWhiteSpace))throw new InvalidOperationException("Pazaryeri, mağaza ve sipariş kimliği zorunlu.");if(order.Items.Count==0||order.Items.Any(item=>item.Quantity<=0))throw new InvalidOperationException("Stok kararı için her sipariş satırında pozitif adet gerekli.");}
}
