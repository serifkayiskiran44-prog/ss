using System.Security.Cryptography;
using System.Text;

namespace TrMarketplaceHubDesktop;

/// <summary>
/// Immutable preview of a live "could not supply" cancellation. The operator sees exactly which lines go to Trendyol,
/// and the confirm step is refused when the stored order changed (status, lines, quantities, update time) after the preview.
/// </summary>
public sealed record TrendyolCancelPreview(long PackageId, int ReasonId, IReadOnlyList<TrendyolUnsuppliedLine> Lines, string Summary, string Fingerprint)
{
    public static TrendyolCancelPreview Build(OrderSnapshot order, int reasonId)
    {
        if (order.IsCancelled || order.IsReturn) throw new InvalidOperationException("Sipariş zaten iptal/iade durumunda; önizleme üretilmedi.");
        if (!long.TryParse(order.Shipments.FirstOrDefault()?.Id, out var packageId) || packageId <= 0) throw new InvalidOperationException("Geçerli Trendyol paket kimliği yok.");
        if (order.Items.Count == 0) throw new InvalidOperationException("Siparişte iptal edilecek kalem yok.");
        var lines = order.Items.Select(x => new TrendyolUnsuppliedLine(x.MarketplaceLineId, x.Quantity)).ToList();
        var text = new StringBuilder("Paket " + packageId + " için Trendyol'a şu kalemler iptal bildirilecek:");
        foreach (var item in order.Items) text.Append("\n  • ").Append(item.Title).Append(" × ").Append(item.Quantity);
        text.Append("\nNeden kodu: ").Append(reasonId).Append(". Onaylamak için aynı düğmeye bir kez daha basın.");
        return new(packageId, reasonId, lines, text.ToString(), Hash(order));
    }

    public void RequireUnchanged(OrderSnapshot current)
    {
        if (current.IsCancelled || current.IsReturn || !string.Equals(Hash(current), Fingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("Sipariş önizlemeden sonra değişti; önizlemeyi yeniden oluşturun. Trendyol'a bir şey gönderilmedi.");
    }

    static string Hash(OrderSnapshot order)
    {
        var text = string.Join("|", order.RawStatus, order.SourceUpdatedAt.UtcTicks, order.Shipments.FirstOrDefault()?.Id,
            string.Join(",", order.Items.OrderBy(x => x.MarketplaceLineId).Select(x => x.MarketplaceLineId + "x" + x.Quantity)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
