using System.Globalization;

namespace TrMarketplaceHubDesktop.Catalog;

/// <summary>The availability projection of a product on a shop with the source stock's freshness as its own state: the stock as recorded, the available figure after the shop's safety and maximum, and whether the source stock behind it is fresh, stale, unobserved or frozen — with the observation, the threshold and the words.</summary>
public sealed record StockProjection(string Channel, string Shop, string ProductId, string Sku, int Stock, int Available, int SafetyStock, int? MaximumStock, string State, DateTime? ObservedUtc, TimeSpan Threshold, string SourceId, string Words, string BufferWords = "", bool BelowBuffer = false) // #933: the buffer used and where it came from; whether the stock sits below it
{
    public const string Fresh = "FRESH", Stale = "STALE", Missing = "MISSING", Frozen = "FROZEN";
    /// <summary>Only a fresh source stock may be written to a marketplace by itself.</summary>
    public bool Dispatchable => State == Fresh;
}

/// <summary>
/// Source stock freshness (#932). The stock a projection starts from was observed at a moment (#895) and its source
/// has a staleness threshold (#903: three times the source's interval, at least six hours); the projection now
/// carries that as a state beside the number — FRESH within the threshold (or the operator's own entry), STALE
/// beyond it, MISSING without an observation time, FROZEN when the source is off or gone — so a stale source stock
/// is never mistaken for a live one: the automation runner refuses to write it, the previews say it. The
/// availability arithmetic (active, minus the shop's safety stock, capped by its maximum) has one owner here.
/// </summary>
public partial class CatalogStore
{
    public StockProjection ProjectStock(string channel, string shop, string productId, DateTime nowUtc)
    {
        var policy = GetStockPolicy(channel, shop) ?? throw new InvalidOperationException("Önce mağaza stok ayarını kaydedin.");
        if (!policy.Enabled) throw new InvalidOperationException("Stok politikası pasif.");
        var product = FindProduct(productId) ?? throw new InvalidOperationException("Ürün bulunamadı.");
        // #933: the buffer is the most specific profile in force -- product, source, store -- or the shop policy's safety stock; a stock below it is nothing available, said so.
        var buffer = new SafetyBufferProfileStore(dataDirectory).Resolve(product.Id, product.SourceId, policy.Channel, policy.Shop, policy.SafetyStock, nowUtc);
        var available = product.Active ? Math.Max(0, product.Stock - buffer.Buffer) : 0; var belowBuffer = product.Active && product.Stock < buffer.Buffer;
        if (policy.MaximumStock.HasValue) available = Math.Min(available, policy.MaximumStock.Value);
        var sources = Sources();
        var freshness = ProductFreshness.Evaluate(product, id => sources.FirstOrDefault(s => s.Id == id), nowUtc).Fields.Single(f => f.Field == "Stock");
        var state = freshness.State switch { FreshnessState.Fresh => StockProjection.Fresh, FreshnessState.Stale => StockProjection.Stale, FreshnessState.Frozen => StockProjection.Frozen, _ => StockProjection.Missing };
        var figure = available.ToString(CultureInfo.InvariantCulture);
        var words = state switch
        {
            StockProjection.Fresh => $"kaynak stoku taze ({freshness.Words}); gösterilebilir {figure}",
            StockProjection.Stale => $"kaynak stoku bayat ({freshness.Words}); gösterilebilir {figure} güvenilmez; otomatik stok yazımı yapılmaz",
            StockProjection.Frozen => $"kaynak stoku tazelenmez ({freshness.Words}); gösterilebilir {figure} güvenilmez; otomatik stok yazımı yapılmaz",
            _ => $"kaynak stoku için gözlem zamanı yok; gösterilebilir {figure} doğrulanamaz; otomatik stok yazımı yapılmaz",
        };
        words += "; " + buffer.Words + (belowBuffer ? $"; stok tamponun altında ({product.Stock.ToString(CultureInfo.InvariantCulture)} < {buffer.Buffer.ToString(CultureInfo.InvariantCulture)})" : ""); // #933
        return new(policy.Channel, policy.Shop, product.Id, product.Sku, product.Stock, available, buffer.Buffer, policy.MaximumStock, state, freshness.ObservedUtc, freshness.Threshold, freshness.SourceId, words, buffer.Words, belowBuffer);
    }
}
