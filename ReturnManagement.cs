using System.Security.Cryptography;
using System.Text;

namespace TrMarketplaceHubDesktop;

public sealed record ReturnCase(string Channel, string ShopId, string OrderId, string LineId, string Sku, int Quantity, string Reason, string Status, DateTimeOffset SourceAt);
public sealed record ReturnRestockPreview(string Fingerprint, string ShopId, string OrderId, string Sku, int Quantity, string Status);

public static class ReturnManagement
{
    public static IReadOnlyList<ReturnCase> ValidateBatch(IEnumerable<ReturnCase> cases, string shopId)
    {
        var rows = cases.Where(x => x.ShopId.Equals(shopId, StringComparison.Ordinal)).Where(x => x.Quantity > 0).ToArray();
        return rows.GroupBy(Fingerprint, StringComparer.Ordinal).Select(g => g.First()).ToArray();
    }

    public static ReturnRestockPreview CreatePreview(ReturnCase @case, string expectedShopId, bool skuKnown, DateTimeOffset expectedOrderUpdated, DateTimeOffset currentOrderUpdated)
    {
        ArgumentNullException.ThrowIfNull(@case);
        if (!@case.ShopId.Equals(expectedShopId, StringComparison.Ordinal) || !skuKnown || @case.Quantity <= 0 || currentOrderUpdated != expectedOrderUpdated) return new(Fingerprint(@case), @case.ShopId, @case.OrderId, @case.Sku, @case.Quantity, "BLOCKED");
        return new(Fingerprint(@case), @case.ShopId, @case.OrderId, @case.Sku, @case.Quantity, "PREVIEW_ONLY");
    }
    public static string Fingerprint(ReturnCase @case) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", @case.Channel.ToLowerInvariant(), @case.ShopId, @case.OrderId, @case.LineId, @case.Sku, @case.Quantity, @case.SourceAt.ToUniversalTime().ToString("O")))));
}
