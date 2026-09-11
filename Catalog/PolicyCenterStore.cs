using System.Text.Json;

namespace TrMarketplaceHubDesktop.Catalog;

public sealed record StockPolicyPreview(string Channel, string Shop, string ProductId, string Sku, int Stock, int AvailableStock, int PolicyVersion, DateTime ProductUpdatedUtc, DateTime PolicyUpdatedUtc);
public sealed record PricePolicyPreview(string Channel, string Shop, string ProductId, string Sku, decimal Price, string Currency, decimal FormulaPriceTry, decimal CostTry, int PolicyVersion, DateTime ProductUpdatedUtc, DateTime PolicyUpdatedUtc);

public partial class CatalogStore
{
    public IReadOnlyList<StockPolicy> ListStockPolicies()
    {
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "SELECT Json FROM StockPolicies ORDER BY Channel,Shop"; using var reader = command.ExecuteReader(); var rows = new List<StockPolicy>(); while (reader.Read()) rows.Add(JsonSerializer.Deserialize<StockPolicy>(reader.GetString(0))!); return rows;
    }
    public IReadOnlyList<PricePolicy> ListPricePolicies()
    {
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "SELECT Json FROM PricePolicies ORDER BY Channel,Shop"; using var reader = command.ExecuteReader(); var rows = new List<PricePolicy>(); while (reader.Read()) rows.Add(JsonSerializer.Deserialize<PricePolicy>(reader.GetString(0))!); return rows;
    }
    public StockPolicy CopyStockPolicy(string sourceChannel, string sourceShop, string targetChannel, string targetShop)
    {
        var source = GetStockPolicy(sourceChannel, sourceShop) ?? throw new InvalidOperationException("Kaynak stok politikası bulunamadı."); var target = GetStockPolicy(targetChannel, targetShop); return SaveStockPolicy(new() { Channel = targetChannel, Shop = targetShop, SafetyStock = source.SafetyStock, MaximumStock = source.MaximumStock, Enabled = source.Enabled, Version = target?.Version ?? 0 });
    }
    public PricePolicy CopyPricePolicy(string sourceChannel, string sourceShop, string targetChannel, string targetShop)
    {
        var source = GetPricePolicy(sourceChannel, sourceShop) ?? throw new InvalidOperationException("Kaynak fiyat politikası bulunamadı."); var target = GetPricePolicy(targetChannel, targetShop); return SavePricePolicy(new() { Channel = targetChannel, Shop = targetShop, Formula = source.Formula, Currency = source.Currency, TryPerUnit = source.TryPerUnit, MinimumPrice = source.MinimumPrice, MinimumMarginTry = source.MinimumMarginTry, Enabled = source.Enabled, Version = target?.Version ?? 0 });
    }
    public StockPolicyPreview PreviewStockDetailed(string channel, string shop, string productId)
    {
        var policy = GetStockPolicy(channel, shop) ?? throw new InvalidOperationException("Önce mağaza stok politikasını kaydedin."); if (!policy.Enabled) throw new InvalidOperationException("Stok politikası pasif."); var product = Products().SingleOrDefault(x => x.Id == productId) ?? throw new InvalidOperationException("Ürün bulunamadı."); var available = product.Active ? Math.Max(0, product.Stock - policy.SafetyStock) : 0; if (policy.MaximumStock.HasValue) available = Math.Min(available, policy.MaximumStock.Value); return new(policy.Channel, policy.Shop, product.Id, product.Sku, product.Stock, available, policy.Version, product.UpdatedUtc, policy.UpdatedUtc);
    }
    public PricePolicyPreview PreviewPriceDetailed(string channel, string shop, string productId)
    {
        var policy = GetPricePolicy(channel, shop) ?? throw new InvalidOperationException("Önce mağaza fiyat politikasını kaydedin."); if (!policy.Enabled) throw new InvalidOperationException("Fiyat politikası pasif."); var product = Products().SingleOrDefault(x => x.Id == productId) ?? throw new InvalidOperationException("Ürün bulunamadı."); var formula = PriceFormula.Evaluate(policy.Formula, product.Cost); if (formula <= 0) throw new InvalidOperationException("Formül pozitif fiyat üretmedi."); var rate = policy.Currency == "TRY" ? 1 : policy.TryPerUnit; var price = Math.Round(formula / rate, 2, MidpointRounding.AwayFromZero); if (price < policy.MinimumPrice || formula - product.Cost < policy.MinimumMarginTry) throw new InvalidOperationException("Fiyat koruması kuralı reddetti."); return new(policy.Channel, policy.Shop, product.Id, product.Sku, price, policy.Currency, formula, product.Cost, policy.Version, product.UpdatedUtc, policy.UpdatedUtc);
    }
}
