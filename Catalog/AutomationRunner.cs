namespace TrMarketplaceHubDesktop.Catalog;

public sealed record AutomationRunResult(int Queued, IReadOnlyList<string> Errors);

public static class AutomationRunner
{
    public static AutomationRunResult RunDue(CatalogStore catalog, AutomationStore automation, SyncStore sync, string jobId, string channel, string shop, DateTime nowUtc)
    {
        sync.RecoverAbandonedRunning(TimeSpan.FromMinutes(5), nowUtc);
        if (!automation.TryClaimLease(jobId, nowUtc, TimeSpan.FromMinutes(5), out var leaseToken)) return new(0, Array.Empty<string>());
        var job = automation.Get(jobId);
        return RunClaimed(catalog, automation, sync, job, channel, shop, nowUtc, false, leaseToken);
    }

    public static AutomationRunResult RunDue(CatalogStore catalog, AutomationStore automation, SyncStore sync, string jobId, DateTime nowUtc)
    {
        sync.RecoverAbandonedRunning(TimeSpan.FromMinutes(5), nowUtc);
        if (!automation.TryClaimLease(jobId, nowUtc, TimeSpan.FromMinutes(5), out var leaseToken)) return new(0, Array.Empty<string>());
        var job = automation.Get(jobId);
        return RunClaimed(catalog, automation, sync, job, job.Channel, job.Shop, nowUtc, true, leaseToken);
    }

    static AutomationRunResult RunClaimed(CatalogStore catalog, AutomationStore automation, SyncStore sync, AutomationJob job, string channel, string shop, DateTime nowUtc, bool requireListingMapping, string leaseToken)
    {
        var errors = new List<string>(); var queued = 0;
        if (job.Kind is AutomationKind.Xml or AutomationKind.Health or AutomationKind.Sync or AutomationKind.XmlExport)
        {
            try
            {
                var operation = job.Kind switch { AutomationKind.Xml => "xml-import", AutomationKind.Health => "health-check", AutomationKind.XmlExport => "xml-export", _ => "sync" };
                var entity = string.IsNullOrWhiteSpace(job.TemplateKey) ? shop : job.TemplateKey;
                sync.Enqueue(new SyncRequest(channel, operation, entity, $"{job.Id}:{nowUtc.Ticks}", shop)); queued = 1;
            }
            catch (Exception error) { errors.Add(error.Message); }
        }
        else
        {
            RecalculationQueue.Reconcile(sync, catalog, channel, shop); /* #931: a pending job whose rule revision or product version moved on is cancelled, never applied */
            // #932: a stale, unobserved or frozen source stock is never written to a marketplace by itself -- the projection says why and the job fails by name.
            string StockPayload(string productId) { var projection = catalog.ProjectStock(channel, shop, productId, nowUtc); if (!projection.Dispatchable) throw new InvalidOperationException("Stok gönderimi engellendi: " + projection.Words); return projection.Available.ToString(System.Globalization.CultureInfo.InvariantCulture); }
            foreach (var product in catalog.Products())
            {
                var operation = job.Kind == AutomationKind.Stock ? "stock" : "price";
                var entity = channel.Equals("etsy", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(product.EtsyListingId) ? product.EtsyListingId : product.Id;
                try
                {
                    if (requireListingMapping && channel.Equals("etsy", StringComparison.OrdinalIgnoreCase) && (!long.TryParse(entity, out var listingId) || listingId <= 0)) throw new InvalidOperationException("Etsy ilan eşlemesi eksik.");
                    var payload = job.Kind == AutomationKind.Stock ? StockPayload(product.Id) /* #932 */ : catalog.PreviewPrice(channel, shop, product.Id, null, automatic: true).Price.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) /* #924: an automatic live write never proceeds on a stale rate */;
                    // #931: the key names the product, its version, the rule revision and the payload; one pending job per product, shop and operation -- the older pending ones are coalesced.
                    RecalculationQueue.Enqueue(sync, channel, shop, operation, entity, new RecalculationKey(product.Id, product.UpdatedUtc.Ticks, RecalculationQueue.RuleVersion(catalog, operation, channel, shop), payload)); queued++;
                }
                catch (Exception error)
                {
                    // One atomic write: Enqueue-then-Fail left a Pending (dispatchable) ":error" job between the two statements (#778).
                    sync.EnqueueFailed(new SyncRequest(channel, operation, entity, $"{product.Id}:{product.UpdatedUtc.Ticks}:error", shop), error.Message); errors.Add($"{product.Sku}: {error.Message}");
                }
            }
        }
        if (errors.Count == 0) automation.Complete(job.Id, nowUtc, leaseToken); else automation.Fail(job.Id, string.Join("; ", errors), leaseToken);
        return new(queued, errors);
    }
}
