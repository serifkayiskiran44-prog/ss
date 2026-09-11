using System.Security.Cryptography;
using System.Text;

namespace TrMarketplaceHubDesktop;

public sealed record InvoiceDraft(string ShopId, string OrderId, string Provider, string DocumentType, string? InvoiceNumber, string? UblXml, string? PublicLink);
public sealed record InvoiceSubmissionPreview(string ShopId, string OrderId, long ShipmentPackageId, string InvoiceLink, string? InvoiceNumber, long? InvoiceDateTime, string IdempotencyKey);
public sealed record InvoiceSubmissionResult(string State, int? HttpStatus, string Detail, bool ShouldRetry);
public sealed record InvoiceProviderState(string Provider, string Environment, string Status, string Detail);

public static class InvoiceCenter
{
    public static InvoiceSubmissionPreview CreateTrendyolPreview(long sellerId, string shopId, string orderId, long shipmentPackageId, string invoiceLink, string? invoiceNumber, long? invoiceDateTime)
    {
        if (sellerId <= 0 || shipmentPackageId <= 0 || string.IsNullOrWhiteSpace(shopId) || string.IsNullOrWhiteSpace(orderId)) throw new ArgumentException("seller/shop/order/package zorunludur.");
        if (!Uri.TryCreate(invoiceLink, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("Fatura linki yalnız HTTPS olabilir.");
        if (invoiceNumber is not null && invoiceNumber.Length > 64) throw new ArgumentException("Fatura numarası 64 karakteri aşamaz.");
        if (invoiceDateTime is <= 0) throw new ArgumentException("Fatura zamanı pozitif Unix zamanı olmalıdır.");
        var material = $"trendyol|{sellerId}|{shopId}|{orderId}|{shipmentPackageId}|{invoiceLink}|{invoiceNumber}|{invoiceDateTime}";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        return new(shopId, orderId, shipmentPackageId, invoiceLink, invoiceNumber, invoiceDateTime, key);
    }

    public static InvoiceSubmissionResult ClassifyTrendyolResponse(int statusCode) => statusCode switch
    {
        200 or 201 => new("SENT", statusCode, "Fatura linki kabul edildi.", false),
        400 => new("FAILED", statusCode, "İstek veya fatura alanları geçersiz.", false),
        401 => new("AUTH_ERROR", statusCode, "Yetkilendirme başarısız.", false),
        409 => new("DUPLICATE", statusCode, "Aynı paket/link daha önce gönderilmiş.", false),
        >= 500 and <= 599 => new("RETRYABLE_ERROR", statusCode, "Sağlayıcı sunucu hatası.", true),
        _ => new("FAILED", statusCode, "Beklenmeyen sağlayıcı yanıtı.", false)
    };

    public static void EnsureOrderScope(string expectedShopId, string expectedOrderId, InvoiceSubmissionPreview preview)
    {
        if (!StringComparer.Ordinal.Equals(expectedShopId, preview.ShopId) || !StringComparer.Ordinal.Equals(expectedOrderId, preview.OrderId)) throw new InvalidOperationException("WRONG_SHOP_OR_ORDER: önizleme mevcut sipariş kapsamıyla eşleşmiyor.");
    }

    public static void EnsureFresh(DateTimeOffset createdUtc, DateTimeOffset now, TimeSpan maxAge)
    { if (createdUtc + maxAge < now) throw new InvalidOperationException("STALE_PREVIEW: fatura gönderim önizlemesi süresi doldu."); }

    public static InvoiceSubmissionResult GuardDuplicate(ISet<string> submittedKeys, InvoiceSubmissionPreview preview) =>
        submittedKeys.Contains(preview.IdempotencyKey) ? new("DUPLICATE", 409, "Aynı fatura gönderimi idempotent olarak engellendi.", false) : new("READY", null, "Açık onay bekleniyor.", false);

    public static InvoiceProviderState DescribeProvider(string provider, bool configured, bool officialContractVerified) =>
        !configured ? new(provider, "UNKNOWN", "NOT_CONFIGURED", "Şifreli sağlayıcı ayarı yok.") : !officialContractVerified ? new(provider, "TEST_OR_LIVE", "LIVE_API_BLOCKED", "Resmi endpoint/scope sözleşmesi doğrulanmadı; HTTP isteği oluşturulmadı.") : new(provider, "TEST", "READY_READ_ONLY", "Sözleşme doğrulaması sonrası test akışı hazır.");
}
