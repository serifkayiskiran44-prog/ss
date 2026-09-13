using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public sealed record EtsyProductReadinessResult(string Status, IReadOnlyList<string> Missing)
{
    public bool Ready => Status == "READY";
}

/// <summary>Local Etsy product readiness; does not contact Etsy and cannot authorize a write.</summary>
public static class EtsyProductReadiness
{
    public static EtsyProductReadinessResult Evaluate(CatalogProduct product, EtsyListingTemplate template, EtsyCredentials credentials, MarketplaceMapping? mapping)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(credentials.ShopId) || !long.TryParse(credentials.ShopId, out var shop) || shop <= 0 || !credentials.IsAccessTokenUsable()) missing.Add("credential/shop");
        if (string.IsNullOrWhiteSpace(product.Name) || string.IsNullOrWhiteSpace(product.Description)) missing.Add("title/description");
        if (product.Price <= 0 || product.Stock <= 0 || string.IsNullOrWhiteSpace(product.Currency)) missing.Add("price/stock/currency");
        if (string.IsNullOrWhiteSpace(product.ImageUrls)) missing.Add("image");
        if (template.TaxonomyId <= 0 || template.ShippingProfileId <= 0 || template.ReadinessStateId <= 0) missing.Add("taxonomy/shipping/readiness");
        if (mapping is null || !string.Equals(mapping.Channel, "etsy", StringComparison.OrdinalIgnoreCase) || !string.Equals(mapping.ShopId, credentials.ShopId.Trim(), StringComparison.Ordinal)) missing.Add("shop-scoped listing mapping");
        return new(missing.Count == 0 ? "READY" : "BLOCKED", missing);
    }
}
