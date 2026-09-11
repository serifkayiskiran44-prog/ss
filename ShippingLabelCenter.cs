namespace TrMarketplaceHubDesktop;

public sealed record ShipmentPreview(string Carrier, string OrderId, string TrackingCode, int PackageCount, string Status, string Detail);

public static class ShippingLabelCenter
{
    public static ShipmentPreview CreatePreview(string carrier, string orderId, string? trackingCode, int packageCount, ISet<string> knownTrackingCodes)
    {
        if (string.IsNullOrWhiteSpace(carrier) || string.IsNullOrWhiteSpace(orderId)) throw new ArgumentException("Taşıyıcı ve sipariş zorunludur.");
        if (packageCount <= 0) throw new ArgumentOutOfRangeException(nameof(packageCount));
        var code = trackingCode?.Trim() ?? string.Empty;
        if (code.Length > 0 && !knownTrackingCodes.Add(code)) return new(carrier.Trim(), orderId.Trim(), code, packageCount, "BLOCKED_DUPLICATE", "Tracking code zaten kayıtlı.");
        return new(carrier.Trim(), orderId.Trim(), code, packageCount, "PREVIEW_ONLY", "Resmi taşıyıcı capability doğrulaması ve kullanıcı onayı bekleniyor.");
    }
}
