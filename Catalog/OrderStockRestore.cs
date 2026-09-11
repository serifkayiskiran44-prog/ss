using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace TrMarketplaceHubDesktop.Catalog;

public sealed record OrderRestockPreviewLine(string ProductId, string Sku, int Quantity, int CurrentStock, int RestoredStock, DateTime ProductUpdatedUtc);
public sealed record OrderRestockPreview(string Marketplace, string ShopId, string OrderId, string ActionKey, IReadOnlyList<OrderRestockPreviewLine> Lines);
public sealed record OrderRestockResult(bool AlreadyApplied, DateTime AppliedUtc, IReadOnlyList<OrderRestockPreviewLine> Lines);

public partial class CatalogStore
{
    public OrderRestockPreview CreateOrderRestockPreview(string marketplace, string shopId, string orderId, string actionKey)
    {
        if (new[] { marketplace, shopId, orderId, actionKey }.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("İptal/iade stok önizlemesi için kanal, mağaza, sipariş ve olay anahtarı zorunlu.");
        if (actionKey.Any(char.IsControl) || actionKey.Length > 200) throw new ArgumentException("Olay anahtarı kontrol karakteri içeremez ve 200 karakteri aşamaz.");
        marketplace = marketplace.Trim().ToLowerInvariant(); shopId = shopId.Trim(); orderId = orderId.Trim(); actionKey = actionKey.Trim();
        var receipt = GetOrderStockStatus(marketplace, shopId, orderId) ?? throw new InvalidOperationException("Bu sipariş için daha önce uygulanmış stok hareketi bulunamadı; otomatik geri koyma önerisi üretilemez.");
        var products = Products().ToDictionary(x => x.Id);
        var lines = new List<OrderRestockPreviewLine>();
        foreach (var movement in receipt.Movements)
        {
            if (!products.TryGetValue(movement.ProductId, out var product)) throw new InvalidOperationException($"{movement.Sku} merkezi ürünü bulunamadı; sipariş yeniden yüklenmeli.");
            lines.Add(new(product.Id, product.Sku, movement.Quantity, product.Stock, checked(product.Stock + movement.Quantity), product.UpdatedUtc));
        }
        return new(marketplace, shopId, orderId, actionKey, lines);
    }

    public OrderRestockResult ApplyOrderRestock(OrderRestockPreview preview, bool approved)
    {
        if (!approved) throw new InvalidOperationException("İptal/iade stok geri koyma işlemi için açık onay gerekli.");
        if (preview.Lines.Count == 0) throw new InvalidOperationException("Geri koyulacak stok satırı yok.");
        if (string.IsNullOrWhiteSpace(preview.Marketplace) || string.IsNullOrWhiteSpace(preview.ShopId) || string.IsNullOrWhiteSpace(preview.OrderId) || string.IsNullOrWhiteSpace(preview.ActionKey) || preview.ActionKey.Any(char.IsControl)) throw new ArgumentException("Stok geri koyma kimliği geçersiz.");
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false);
        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction; existing.CommandText = "SELECT Payload,AppliedUtc FROM OrderStockRestores WHERE Marketplace=$marketplace AND ShopId=$shop AND OrderId=$order"; OrderStockIdentityParams(existing, preview.Marketplace, preview.ShopId, preview.OrderId);
            using var reader = existing.ExecuteReader();
            if (reader.Read()) { var lines = JsonSerializer.Deserialize<List<OrderRestockPreviewLine>>(reader.GetString(0)) ?? []; var at = DateTime.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind); reader.Close(); transaction.Commit(); return new(true, at, lines); }
        }
        foreach (var line in preview.Lines)
        {
            using var find = connection.CreateCommand(); find.Transaction = transaction; find.CommandText = "SELECT Json FROM CatalogProducts WHERE Id=$id"; find.Parameters.AddWithValue("$id", line.ProductId); var product = find.ExecuteScalar() is string json ? JsonSerializer.Deserialize<CatalogProduct>(json) : null;
            if (product is null) throw new InvalidOperationException($"{line.Sku} ürünü bulunamadı; siparişi yeniden yükleyin.");
            if (product.UpdatedUtc != line.ProductUpdatedUtc || product.Stock != line.CurrentStock) throw new InvalidOperationException($"{line.Sku} ürünü önizlemeden sonra değişti; yeni geri koyma önizlemesi alın.");
            product.Stock = line.RestoredStock; product.UpdatedUtc = DateTime.UtcNow; Put(connection, "CatalogProducts", product.Id, product, transaction);
        }
        var applied = DateTime.UtcNow;
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "INSERT INTO OrderStockRestores VALUES($marketplace,$shop,$order,$action,$payload,$at)"; OrderStockIdentityParams(command, preview.Marketplace, preview.ShopId, preview.OrderId); command.Parameters.AddWithValue("$action", preview.ActionKey); command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(preview.Lines)); command.Parameters.AddWithValue("$at", applied.ToString("O")); command.ExecuteNonQuery(); transaction.Commit(); return new(false, applied, preview.Lines);
    }
}
