using TrMarketplaceHubDesktop;

namespace TrMarketplaceHubDesktop.Catalog;

public sealed record OrderStockDecisionPreview(
    string Marketplace,
    string ShopId,
    string OrderId,
    IReadOnlyList<OrderItem> Items,
    bool AlreadyApplied,
    string Summary);

/// <summary>Shared, local-only sale stock decision. Cancellation/return never restores stock automatically.</summary>
public sealed class OrderStockDecisionService(CatalogStore catalog)
{
    public OrderStockDecisionPreview CreatePreview(OrderSnapshot order)
    {
        if (string.IsNullOrWhiteSpace(order.Marketplace) || string.IsNullOrWhiteSpace(order.ShopId) || string.IsNullOrWhiteSpace(order.OrderId))
            throw new InvalidOperationException("Pazaryeri, mağaza ve sipariş kimliği zorunlu.");
        if (order.Items.Count == 0 || order.Items.Any(x => string.IsNullOrWhiteSpace(x.Sku) || x.Quantity <= 0))
            throw new InvalidOperationException("Stok kararı için her sipariş satırında SKU ve pozitif adet gerekli.");
        var existing = catalog.GetOrderStockStatus(order.Marketplace, order.ShopId, order.OrderId);
        return new(order.Marketplace, order.ShopId, order.OrderId, order.Items.Select(x => new OrderItem { Title = x.Title, Sku = x.Sku, Quantity = x.Quantity }).ToArray(), existing is not null,
            existing is null ? "Stok düşümü için açık onay bekliyor." : "Bu siparişin stok düşümü daha önce uygulandı; tekrar düşülmez.");
    }

    public OrderStockResult ApplyApproved(OrderStockDecisionPreview preview, bool approved)
    {
        if (!approved) throw new InvalidOperationException("Sipariş stok değişikliği için açık onay gerekli.");
        if (preview.AlreadyApplied)
            return new(true, catalog.GetOrderStockStatus(preview.Marketplace, preview.ShopId, preview.OrderId)
                ?? throw new InvalidOperationException("Önceden uygulanan stok receipt kaydı bulunamadı."));
        return catalog.ApplyOrderStock(preview.Marketplace, preview.ShopId, preview.OrderId, preview.Items);
    }
}
