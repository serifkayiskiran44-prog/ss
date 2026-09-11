namespace TrMarketplaceHubDesktop.Catalog;

public enum EtsyStockDecisionKind { Sale, Cancellation, Return }
public sealed record EtsyStockDecisionPreview(string Marketplace,string ShopId,string OrderId,EtsyStockDecisionKind Kind,IReadOnlyList<OrderItem> Items,bool AlreadyApplied,string Summary);

public sealed class EtsyOrderStockDecisionService(CatalogStore catalog)
{
 public EtsyStockDecisionPreview CreatePreview(OrderSnapshot order,EtsyStockDecisionKind kind)
 {
  if(order is null)throw new ArgumentNullException(nameof(order));
  OrdersRules.Validate(order);
  if(!string.Equals(order.Marketplace,"Etsy",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Bu karar akışı yalnız Etsy siparişleri içindir.");
  if(kind!=EtsyStockDecisionKind.Sale) return new(order.Marketplace,order.ShopId,order.OrderId,kind,order.Items.AsReadOnly(),false,$"{kind}: stok geri koyma otomatik yapılmaz; kullanıcı kararı gerekir.");
  var existing=catalog.GetOrderStockStatus(order.Marketplace,order.ShopId,order.OrderId);
  if(order.Items.Any(i=>string.IsNullOrWhiteSpace(i.Sku)))throw new InvalidOperationException("Etsy siparişinde SKU eşleşmesi eksik; stok kararı oluşturulmadı.");
  return new(order.Marketplace,order.ShopId,order.OrderId,kind,order.Items.AsReadOnly(),existing is not null,existing is null?"Satış stok düşümü için açık onay bekliyor.":"Bu satışın stok düşümü daha önce uygulanmış.");
 }
 public OrderStockResult ApplyApproved(EtsyStockDecisionPreview preview,bool approved)
 {
  if(!approved)throw new InvalidOperationException("Stok kararı için açık kullanıcı onayı gerekli.");
  if(preview.Kind!=EtsyStockDecisionKind.Sale)throw new InvalidOperationException("İptal/iade stok geri koyma otomatik değildir; manuel işlem kararı gerekir.");
  if(preview.AlreadyApplied){var existing=catalog.GetOrderStockStatus(preview.Marketplace,preview.ShopId,preview.OrderId)??throw new InvalidOperationException("Stok receipt kaydı bulunamadı.");return new(true,existing);}
  return catalog.ApplyOrderStock(preview.Marketplace,preview.ShopId,preview.OrderId,preview.Items);
 }
}
