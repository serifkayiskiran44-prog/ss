namespace TrMarketplaceHubDesktop.Catalog;

/// <summary>
/// The product card lists every channel pre-filled with the base price. Saving all rows would freeze each channel at a copy of that price,
/// so later base-price changes (XML, Excel, bulk edit) would silently stop reaching the channels. Only channels that already had their own
/// price, or that the user actually changed away from the pre-filled default, are kept.
/// </summary>
public static class ChannelPriceMerge
{
    public static Dictionary<string, XmlChannelPrice> Keep(IReadOnlyDictionary<string, XmlChannelPrice> existing,
        IEnumerable<(string Channel, decimal SalePrice, decimal? ListPrice, string Currency)> edited, decimal defaultSale, string defaultCurrency)
    {
        var result = new Dictionary<string, XmlChannelPrice>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in edited)
        {
            var changed = row.SalePrice != defaultSale || row.ListPrice is > 0 || !string.Equals(row.Currency, defaultCurrency, StringComparison.OrdinalIgnoreCase);
            if (changed || existing.ContainsKey(row.Channel))
                result[row.Channel] = new XmlChannelPrice(row.SalePrice, row.ListPrice is > 0 ? row.ListPrice : null, row.Currency);
        }
        return result;
    }
}
