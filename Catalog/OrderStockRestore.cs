using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TrMarketplaceHubDesktop.Catalog;

public sealed record OrderRestockPreviewLine(string ProductId, string Sku, int Quantity, int CurrentStock, int RestoredStock, DateTime ProductUpdatedUtc);
public sealed record OrderRestockPreview(string Marketplace, string ShopId, string OrderId, string ActionKey, IReadOnlyList<OrderRestockPreviewLine> Lines);
public sealed record OrderRestockResult(bool AlreadyApplied, DateTime AppliedUtc, IReadOnlyList<OrderRestockPreviewLine> Lines);

public enum OrderRestockReviewReason { ActionConflict, LegacyActionKey, ReceiptCorrupt }

/// Raised when an existing OrderStockRestores row for the order cannot be used as
/// proof that the incoming action already ran: its receipt is corrupt, its stored
/// ActionKey is blank/invalid (legacy), or it belongs to a different business
/// action. In every case zero stock is added. Diagnostics carry only a reason code
/// and short hashes of the action keys - never raw keys, payload or product data.
public sealed class OrderRestockReviewRequiredException : Exception
{
    public OrderRestockReviewReason Reason { get; }
    public string Marketplace { get; }
    public string ShopId { get; }
    public string OrderId { get; }
    public string IncomingActionKeyHash { get; }
    public string StoredActionKeyHash { get; }

    public OrderRestockReviewRequiredException(OrderRestockReviewReason reason, string marketplace, string shopId, string orderId, string incomingActionKeyHash, string storedActionKeyHash, string message) : base(message)
    {
        Reason = reason; Marketplace = marketplace; ShopId = shopId; OrderId = orderId; IncomingActionKeyHash = incomingActionKeyHash; StoredActionKeyHash = storedActionKeyHash;
    }
}

public partial class CatalogStore
{
    const int MaxRestockActionKeyLength = 200;
    const int MaxRestoreReceiptBytes = 200_000;

    /// The single canonical ActionKey rule, used at both preview and apply: raw
    /// input with control characters or over 200 characters is rejected, then
    /// surrounding whitespace is trimmed. Case is preserved and compared ordinally -
    /// no verified event contract says keys differing only in case are the same event.
    public static string CanonicalRestockActionKey(string? actionKey)
    {
        if (string.IsNullOrWhiteSpace(actionKey)) throw new ArgumentException("İptal/iade olay anahtarı zorunlu.");
        if (actionKey.Any(char.IsControl) || actionKey.Length > MaxRestockActionKeyLength) throw new ArgumentException("Olay anahtarı kontrol karakteri içeremez ve 200 karakteri aşamaz.");
        return actionKey.Trim();
    }

    static bool IsCanonicalStoredActionKey(string stored) =>
        !string.IsNullOrWhiteSpace(stored) && !stored.Any(char.IsControl) && stored.Length <= MaxRestockActionKeyLength && stored == stored.Trim();

    static string SafeKeyHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12];

    public OrderRestockPreview CreateOrderRestockPreview(string marketplace, string shopId, string orderId, string actionKey)
    {
        if (new[] { marketplace, shopId, orderId }.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("İptal/iade stok önizlemesi için kanal, mağaza, sipariş ve olay anahtarı zorunlu.");
        actionKey = CanonicalRestockActionKey(actionKey);
        marketplace = marketplace.Trim().ToLowerInvariant(); shopId = shopId.Trim(); orderId = orderId.Trim();
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

    /// The only decoder for a persisted restore receipt: bounded size, then decode,
    /// then internal consistency. Wraps only format/consistency failures - a
    /// SqliteException from the surrounding command (busy/locked/I/O) is never
    /// turned into "corrupt" or "conflict".
    static bool TryReadRestoreReceipt(string payload, string appliedUtc, out List<OrderRestockPreviewLine>? lines, out DateTime at, out string? reason)
    {
        lines = null; at = default; reason = null;
        if (payload.Length > MaxRestoreReceiptBytes) { reason = "Oversized restore receipt payload"; return false; }
        List<OrderRestockPreviewLine>? parsed;
        try { parsed = JsonSerializer.Deserialize<List<OrderRestockPreviewLine>>(payload); }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException or NotSupportedException) { reason = "Malformed JSON"; return false; }
        if (parsed is null || parsed.Count == 0) { reason = "Missing restore lines"; return false; }
        if (parsed.Any(l => l is null || string.IsNullOrWhiteSpace(l.ProductId) || string.IsNullOrWhiteSpace(l.Sku))) { reason = "Line missing product/sku identity"; return false; }
        if (parsed.Select(l => l.ProductId).Distinct(StringComparer.Ordinal).Count() != parsed.Count) { reason = "Duplicate ProductId line"; return false; }
        if (parsed.Any(l => l.Quantity <= 0 || l.CurrentStock < 0 || (long)l.RestoredStock != (long)l.CurrentStock + l.Quantity)) { reason = "Invalid or inconsistent line quantities"; return false; }
        if (!DateTime.TryParse(appliedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out at) || at < new DateTime(2000, 1, 1) || at > DateTime.UtcNow.AddDays(1)) { reason = "Invalid AppliedUtc"; return false; }
        lines = parsed; return true;
    }

    /// Idempotency is bound to the exact business action, not just the order key:
    /// an existing restore row counts as "already applied" only when its receipt is
    /// intact AND its stored ActionKey is valid AND ordinally equal to the incoming
    /// canonical key. The whole decision runs inside one immediate transaction
    /// before any CatalogProducts mutation, so concurrent callers serialize and a
    /// conflicting action can never add stock a second time. See #2668.
    public OrderRestockResult ApplyOrderRestock(OrderRestockPreview preview, bool approved)
    {
        if (!approved) throw new InvalidOperationException("İptal/iade stok geri koyma işlemi için açık onay gerekli.");
        if (preview.Lines.Count == 0) throw new InvalidOperationException("Geri koyulacak stok satırı yok.");
        if (string.IsNullOrWhiteSpace(preview.Marketplace) || string.IsNullOrWhiteSpace(preview.ShopId) || string.IsNullOrWhiteSpace(preview.OrderId)) throw new ArgumentException("Stok geri koyma kimliği geçersiz.");
        var actionKey = CanonicalRestockActionKey(preview.ActionKey);
        using var connection = Open(); using var transaction = connection.BeginTransaction(deferred: false);
        string? storedAction = null, storedPayload = null, storedAt = null;
        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction; existing.CommandText = "SELECT ActionKey,Payload,AppliedUtc FROM OrderStockRestores WHERE Marketplace=$marketplace AND ShopId=$shop AND OrderId=$order"; OrderStockIdentityParams(existing, preview.Marketplace, preview.ShopId, preview.OrderId);
            using var reader = existing.ExecuteReader();
            if (reader.Read()) { storedAction = reader.GetString(0); storedPayload = reader.GetString(1); storedAt = reader.GetString(2); }
        }
        if (storedPayload is not null)
        {
            var incomingHash = SafeKeyHash(actionKey);
            // Order matters: an untrustworthy receipt is never compared at all, so a
            // matching key can't turn corruption into a replay success.
            if (!TryReadRestoreReceipt(storedPayload, storedAt!, out var lines, out var at, out var reason))
                throw new OrderRestockReviewRequiredException(OrderRestockReviewReason.ReceiptCorrupt, preview.Marketplace, preview.ShopId, preview.OrderId, incomingHash, "", $"RECEIPT_RECOVERY_REQUIRED: geri koyma kaydı bozuk ({reason}); stok tekrar eklenmedi.");
            if (!IsCanonicalStoredActionKey(storedAction!))
                throw new OrderRestockReviewRequiredException(OrderRestockReviewReason.LegacyActionKey, preview.Marketplace, preview.ShopId, preview.OrderId, incomingHash, "", "REVIEW_REQUIRED: bu siparişin geri koyma kaydında olay anahtarı eksik veya geçersiz; hangi olaya ait olduğu doğrulanamadığı için stok tekrar eklenmedi.");
            if (!string.Equals(storedAction, actionKey, StringComparison.Ordinal))
                throw new OrderRestockReviewRequiredException(OrderRestockReviewReason.ActionConflict, preview.Marketplace, preview.ShopId, preview.OrderId, incomingHash, SafeKeyHash(storedAction!), "ACTION_CONFLICT (REVIEW_REQUIRED): bu sipariş için stok daha önce farklı bir iptal/iade olayıyla geri koyuldu; bu olay için stok tekrar eklenmedi.");
            transaction.Commit(); return new(true, at, lines!);
        }
        foreach (var line in preview.Lines)
        {
            using var find = connection.CreateCommand(); find.Transaction = transaction; find.CommandText = "SELECT Json FROM CatalogProducts WHERE Id=$id"; find.Parameters.AddWithValue("$id", line.ProductId); var product = find.ExecuteScalar() is string json ? JsonSerializer.Deserialize<CatalogProduct>(json) : null;
            if (product is null) throw new InvalidOperationException($"{line.Sku} ürünü bulunamadı; siparişi yeniden yükleyin.");
            if (product.UpdatedUtc != line.ProductUpdatedUtc || product.Stock != line.CurrentStock) throw new InvalidOperationException($"{line.Sku} ürünü önizlemeden sonra değişti; yeni geri koyma önizlemesi alın.");
            var balance = InventoryLedger.ReadBalance(connection, transaction, product.Id, InventoryLedger.OnlineLocationId);
            if (balance.Version == 0 || balance.Quantity != line.CurrentStock) throw new InvalidOperationException($"{line.Sku} çevrimiçi bakiyesi katalog stokuyla uyuşmuyor; stok incelemesi gerekli.");
            product.Stock = line.RestoredStock; product.UpdatedUtc = DateTime.UtcNow; Put(connection, "CatalogProducts", product.Id, product, transaction);
            InventoryLedger.RecordMovement(connection, transaction, product.Id, InventoryLedger.OnlineLocationId, line.CurrentStock, line.RestoredStock,
                InventoryMovementKind.OrderRestock, InventoryLedger.OrderReference(preview.Marketplace, preview.ShopId, preview.OrderId) + ":" + actionKey, product.UpdatedUtc);
        }
        var applied = DateTime.UtcNow;
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "INSERT INTO OrderStockRestores VALUES($marketplace,$shop,$order,$action,$payload,$at)"; OrderStockIdentityParams(command, preview.Marketplace, preview.ShopId, preview.OrderId); command.Parameters.AddWithValue("$action", actionKey); command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(preview.Lines)); command.Parameters.AddWithValue("$at", applied.ToString("O", CultureInfo.InvariantCulture)); command.ExecuteNonQuery(); transaction.Commit(); return new(false, applied, preview.Lines);
    }
}
