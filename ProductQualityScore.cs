namespace TrMarketplaceHubDesktop;

public sealed record QualityItem(string Key, int Points, int MaxPoints, string Status, string Detail);
public sealed record ProductQualityScore(string ProductId, int Score, int Version, IReadOnlyList<QualityItem> Items)
{
    public IReadOnlyList<QualityItem> FixList => Items.Where(x => x.Status is "MISSING" or "BLOCKED").ToArray();
}

public static class ProductQualityScoreCalculator
{
    public const int Version = 1;
    public static ProductQualityScore Calculate(Catalog.CatalogProduct product, string? channel = null, bool capabilityReady = true)
    {
        ArgumentNullException.ThrowIfNull(product);
        var items = new List<QualityItem>
        {
            new("identity",  string.IsNullOrWhiteSpace(product.Sku) || string.IsNullOrWhiteSpace(product.Barcode) ? 0 : 15, 15, string.IsNullOrWhiteSpace(product.Sku) || string.IsNullOrWhiteSpace(product.Barcode) ? "MISSING" : "PASS", "SKU ve barkod"),
            new("content", string.IsNullOrWhiteSpace(product.Name) || string.IsNullOrWhiteSpace(product.Description) ? 0 : 15, 15, string.IsNullOrWhiteSpace(product.Name) || string.IsNullOrWhiteSpace(product.Description) ? "MISSING" : "PASS", "Başlık ve açıklama"),
            new("commercial", product.Price > 0 && product.Stock > 0 ? 15 : 0, 15, product.Price > 0 && product.Stock > 0 ? "PASS" : "MISSING", "Pozitif fiyat ve stok"),
            new("media", string.IsNullOrWhiteSpace(product.ImageUrls) ? 0 : 15, 15, string.IsNullOrWhiteSpace(product.ImageUrls) ? "MISSING" : "PASS", "Görsel"),
            new("taxonomy", string.IsNullOrWhiteSpace(product.Category) || string.IsNullOrWhiteSpace(product.Brand) ? 0 : 15, 15, string.IsNullOrWhiteSpace(product.Category) || string.IsNullOrWhiteSpace(product.Brand) ? "MISSING" : "PASS", "Kategori ve marka"),
            new("provenance", string.IsNullOrWhiteSpace(product.SourceId) ? 0 : 10, 10, string.IsNullOrWhiteSpace(product.SourceId) ? "MISSING" : "PASS", "Kaynak/provenance")
        };
        if (!string.IsNullOrWhiteSpace(channel)) items.Add(new("channel", capabilityReady ? 15 : 0, 15, capabilityReady ? "PASS" : "BLOCKED", capabilityReady ? channel + " capability hazır" : channel + " capability doğrulanmadı"));
        return new(product.Id, items.Sum(x => x.Points) * 100 / items.Sum(x => x.MaxPoints), Version, items);
    }
}
