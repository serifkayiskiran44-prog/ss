namespace TrMarketplaceHubDesktop.Trendyol;

public sealed record TrendyolCategory(long Id, string Name, string Path, bool IsLeaf);
public sealed record TrendyolBrand(long Id, string Name);
public sealed record TrendyolAttribute(long Id, string Name, bool Required, bool AllowCustom, bool AllowMultiple, IReadOnlyList<TrendyolAttributeValue> Values);
public sealed record TrendyolAttributeValue(long Id, string Name);
public sealed record TrendyolRemoteProduct(string Barcode, string StockCode, string Title, long ContentId, int? Quantity, decimal? SalePrice, decimal? ListPrice, bool Approved)
{
    public string Status { get; init; } = "";
    public string StatusDetail { get; init; } = "";
}
public sealed record TrendyolAddress(long Id, string Name, bool IsShipment, bool IsReturning);
public sealed record TrendyolCarrier(string Code, string Name);
public sealed record TrendyolBuybox(string Barcode, int? Rank, decimal? FirstPrice, decimal? SecondPrice, decimal? ThirdPrice, bool HasMultipleSeller);
