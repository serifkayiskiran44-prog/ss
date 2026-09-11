using System.Globalization;
using System.Xml.Linq;

namespace TrMarketplaceHubDesktop;

public sealed record OrderTransferRow(string Marketplace, string ShopId, string OrderId, string Source, DateTimeOffset UpdatedAt, decimal Total);

public static class OrderTransferCodec
{
    public static string Export(IEnumerable<OrderTransferRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var root = new XElement("orders", rows.Select(x => new XElement("order", new XAttribute("marketplace", x.Marketplace), new XAttribute("shop", x.ShopId), new XAttribute("id", x.OrderId), new XAttribute("source", x.Source), new XAttribute("updatedUtc", x.UpdatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)), new XAttribute("total", x.Total.ToString("0.################", CultureInfo.InvariantCulture))));
        return new XDocument(root).ToString(SaveOptions.DisableFormatting);
    }

    public static IReadOnlyList<OrderTransferRow> Import(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) throw new ArgumentException("Sipariş XML boş.", nameof(xml));
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        if (document.Root?.Name != "orders") throw new InvalidDataException("Sipariş XML kökü geçersiz.");
        return document.Root.Elements("order").Select(e =>
        {
            var marketplace = (string?)e.Attribute("marketplace") ?? throw new InvalidDataException("Marketplace eksik.");
            var shop = (string?)e.Attribute("shop") ?? throw new InvalidDataException("Mağaza eksik.");
            var id = (string?)e.Attribute("id") ?? throw new InvalidDataException("Sipariş no eksik.");
            var updated = (string?)e.Attribute("updatedUtc") ?? throw new InvalidDataException("Tarih eksik.");
            var total = (string?)e.Attribute("total") ?? throw new InvalidDataException("Tutar eksik.");
            return new OrderTransferRow(marketplace, shop, id, (string?)e.Attribute("source") ?? "MANUAL", DateTimeOffset.Parse(updated, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), decimal.Parse(total, CultureInfo.InvariantCulture));
        }).ToArray();
    }
}
