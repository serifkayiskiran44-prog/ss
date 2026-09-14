using System.Globalization;

namespace TrMarketplaceHubDesktop.Catalog;

/// <summary>The availability projection of a product on a shop with the source stock's freshness as its own state: the stock as recorded, the available figure after the shop's safety and maximum, and whether the source stock behind it is fresh, stale, unobserved or frozen — with the observation, the threshold and the words.</summary>
public sealed record StockProjection(string Channel, string Shop, string ProductId, string Sku, int Stock, int Available, int SafetyStock, int? MaximumStock, string State, DateTime? ObservedUtc, TimeSpan Threshold, string SourceId, string Words, string BufferWords = "", bool BelowBuffer = false, string FallbackSourceId = "", int FallbackRevision = 0, int Reserved = 0) // #933: the buffer used and where it came from; whether the stock sits below it
{
    public const string Fresh = "FRESH", Stale = "STALE", Missing = "MISSING", Frozen = "FROZEN", Fallback = "FALLBACK"; // #936: an approved fallback source's fresh stock stands in
    /// <summary>Only a fresh source stock -- or an approved fallback's (#936), named with its revision -- may be written to a marketplace by itself.</summary>
    public bool Dispatchable => State == Fresh || State == Fallback;
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
        var reserved = new StockReservationStore(dataDirectory).ActiveQuantity(product.Id, policy.Channel, policy.Shop, nowUtc); // #937: the units held for orders on this store come off the available figure
        int Available(int units) { var a = product.Active ? Math.Max(0, units - buffer.Buffer - reserved) : 0; return policy.MaximumStock.HasValue ? Math.Min(a, policy.MaximumStock.Value) : a; } // #936: one arithmetic for the record's stock and a fallback's; #937: minus the holds
        var stock = product.Stock; var available = Available(stock); var belowBuffer = product.Active && stock < buffer.Buffer;
        var sources = Sources();
        var freshness = ProductFreshness.Evaluate(product, id => sources.FirstOrDefault(s => s.Id == id), nowUtc).Fields.Single(f => f.Field == "Stock");
        var state = freshness.State switch { FreshnessState.Fresh => StockProjection.Fresh, FreshnessState.Stale => StockProjection.Stale, FreshnessState.Frozen => StockProjection.Frozen, _ => StockProjection.Missing };
        // #936: when the record's stock is not fresh, an eligible fallback's fresh stock stands in -- only once the operator approved that source at its current revision, named with it, never silently.
        var fallback = state == StockProjection.Fresh ? null : FallbackStock.Select(product, sources, Sightings(product.Id), StockObservations(product.Id), new FallbackStockApprovalStore(dataDirectory).Get(product.Id), freshness, nowUtc);
        DateTime? observedUtc = freshness.ObservedUtc; var threshold = freshness.Threshold; var sourceId = freshness.SourceId; var fallbackSourceId = ""; var fallbackRevision = 0;
        if (fallback is { StandsIn: true })
        {
            var chosen = fallback.Selected!; stock = chosen.Stock!.Value; available = Available(stock); belowBuffer = product.Active && stock < buffer.Buffer;
            state = StockProjection.Fallback; observedUtc = chosen.SeenUtc; threshold = SourcePriority.StaleGrace(sources.FirstOrDefault(s => s.Id == chosen.SourceId)); sourceId = chosen.SourceId; fallbackSourceId = chosen.SourceId; fallbackRevision = chosen.Revision;
        }
        var figure = available.ToString(CultureInfo.InvariantCulture);
        var words = state switch
        {
            StockProjection.Fresh => $"kaynak stoku taze ({freshness.Words}); gösterilebilir {figure}",
            StockProjection.Fallback => $"{fallback!.Words}; gösterilebilir {figure}", // #936
            StockProjection.Stale => $"kaynak stoku bayat ({freshness.Words}); gösterilebilir {figure} güvenilmez; otomatik stok yazımı yapılmaz",
            StockProjection.Frozen => $"kaynak stoku tazelenmez ({freshness.Words}); gösterilebilir {figure} güvenilmez; otomatik stok yazımı yapılmaz",
            _ => $"kaynak stoku için gözlem zamanı yok; gösterilebilir {figure} doğrulanamaz; otomatik stok yazımı yapılmaz",
        };
        if (fallback is { StandsIn: false }) words += "; " + fallback.Words; // #936: what would stand in and what it waits for
        words += "; " + buffer.Words + (belowBuffer ? $"; stok tamponun altında ({stock.ToString(CultureInfo.InvariantCulture)} < {buffer.Buffer.ToString(CultureInfo.InvariantCulture)})" : ""); // #933
        if (reserved > 0) words += $"; rezerve {reserved.ToString(CultureInfo.InvariantCulture)} adet (aktif rezervasyon, gösterilebilirden düşüldü)"; // #937
        return new(policy.Channel, policy.Shop, product.Id, product.Sku, stock, available, buffer.Buffer, policy.MaximumStock, state, observedUtc, threshold, sourceId, words, buffer.Words, belowBuffer, fallbackSourceId, fallbackRevision, reserved);
    }
}
