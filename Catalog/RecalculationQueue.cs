using System.Globalization;

namespace TrMarketplaceHubDesktop.Catalog;

/// <summary>What a recalculation job was computed from: the product and its version (its last change), the rule revision in force, and the payload the marketplace would receive. The version text is the sync job's key.</summary>
public sealed record RecalculationKey(string ProductId, long ProductTicks, int RuleVersion, string Payload)
{
    public string Version => $"{ProductId}:{ProductTicks.ToString(CultureInfo.InvariantCulture)}:r{RuleVersion.ToString(CultureInfo.InvariantCulture)}:{Payload}";

    /// <summary>"product:ticks:rN:payload" → the key; an older three-part key or anything else → null (not judged, never cancelled).</summary>
    public static RecalculationKey? Parse(string? version)
    {
        var parts = (version ?? "").Split(':');
        if (parts.Length < 4 || parts[0].Length == 0 || !parts[2].StartsWith('r')) return null;
        if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) || !int.TryParse(parts[2][1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rule)) return null;
        return new(parts[0], ticks, rule, string.Join(':', parts.Skip(3)));
    }
}

/// <summary>Whether a queued job still describes the world: current, or stale with the reason.</summary>
public sealed record RecalculationVerdict(bool Current, string Words);

/// <summary>
/// Recalculation queue deduplication (#931). The automation runner recomputes a product's price or stock for a shop
/// on every run; each result is a sync job keyed by the product, the product's version, the rule revision and the
/// payload. A burst of runs while the product or the rule keeps changing used to leave a trail of pending jobs for
/// the same product and shop; now one pending job stands per product, shop and operation — enqueueing a new key
/// cancels the older pending ones as superseded (the same key is the same job, never a duplicate) — and a pending
/// job whose rule revision or product version has moved on is stale: the runner cancels it before it queues anew,
/// and a dispatcher may ask the verdict and must not apply a stale one. A job the operator cancelled stays
/// cancelled; the same key never comes back to life.
/// </summary>
public static class RecalculationQueue
{
    public static readonly IReadOnlySet<string> Operations = new HashSet<string>(new[] { "price", "stock" }, StringComparer.OrdinalIgnoreCase);

    /// <summary>The revision of the rule a recalculation depends on: the price rule's version for a price, the stock policy's for a stock; 0 without a rule.</summary>
    public static int RuleVersion(CatalogStore catalog, string operation, string channel, string shop)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        try { return string.Equals(operation, "stock", StringComparison.OrdinalIgnoreCase) ? catalog.GetStockPolicy(channel, shop)?.Version ?? 0 : catalog.GetPricePolicy(channel, shop)?.Version ?? 0; }
        catch (Exception) { return 0; }
    }

    /// <summary>Queues one recalculation for a product on a shop: every older pending job of the same product, shop and operation with another key is cancelled as superseded; the same key returns the same job. Returns the job and how many were coalesced.</summary>
    public static (SyncJob Job, int Coalesced) Enqueue(SyncStore sync, string channel, string shop, string operation, string entityId, RecalculationKey key)
    {
        ArgumentNullException.ThrowIfNull(sync); ArgumentNullException.ThrowIfNull(key);
        var version = key.Version; var coalesced = 0;
        foreach (var older in sync.List().Where(j => j.Status == SyncStatus.Pending && Same(j, channel, shop, operation) && j.EntityId == entityId && j.Version != version))
            if (sync.Cancel(older.Id)) coalesced++;
        return (sync.Enqueue(new SyncRequest(channel, operation, entityId, version, shop)), coalesced);
    }

    /// <summary>Whether a job's key still describes the product and the rule as they are now.</summary>
    public static RecalculationVerdict Verdict(SyncJob job, CatalogStore catalog)
    {
        ArgumentNullException.ThrowIfNull(job); ArgumentNullException.ThrowIfNull(catalog);
        var key = RecalculationKey.Parse(job.Version);
        if (key is null) return new(true, "sürüm anahtarı eski biçimde; denetlenmedi");
        var product = catalog.FindProduct(key.ProductId);
        if (product is null) return new(false, "ürün silinmiş");
        if (product.UpdatedUtc.Ticks != key.ProductTicks) return new(false, "ürün iş kuyruğa alındıktan sonra değişti");
        var rule = RuleVersion(catalog, job.Operation, job.Channel, job.ShopId);
        if (rule != key.RuleVersion) return new(false, $"kural sürümü değişti ({key.RuleVersion.ToString(CultureInfo.InvariantCulture)} → {rule.ToString(CultureInfo.InvariantCulture)})");
        return new(true, "güncel");
    }

    /// <summary>For a dispatcher: a stale job's result is never applied.</summary>
    public static void EnsureCurrent(SyncJob job, CatalogStore catalog)
    {
        var verdict = Verdict(job, catalog);
        if (!verdict.Current) throw new InvalidOperationException("Bayat yeniden hesaplama sonucu uygulanmaz: " + verdict.Words);
    }

    /// <summary>Cancels every pending price or stock job of a channel/shop whose key no longer describes the product or the rule; returns how many.</summary>
    public static int Reconcile(SyncStore sync, CatalogStore catalog, string channel, string shop)
    {
        ArgumentNullException.ThrowIfNull(sync); ArgumentNullException.ThrowIfNull(catalog);
        var cancelled = 0;
        foreach (var job in sync.List().Where(j => j.Status == SyncStatus.Pending && Operations.Contains(j.Operation) && SameShop(j, channel, shop)))
            if (!Verdict(job, catalog).Current && sync.Cancel(job.Id)) cancelled++;
        return cancelled;
    }

    static bool SameShop(SyncJob job, string channel, string shop) => string.Equals(job.Channel, (channel ?? "").Trim().ToLowerInvariant(), StringComparison.Ordinal) && string.Equals(job.ShopId, (shop ?? "").Trim(), StringComparison.Ordinal);
    static bool Same(SyncJob job, string channel, string shop, string operation) => SameShop(job, channel, shop) && string.Equals(job.Operation, operation, StringComparison.OrdinalIgnoreCase);
}
