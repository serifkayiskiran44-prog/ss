namespace TrMarketplaceHubDesktop.Catalog;

public sealed record AutomationRunResult(int Queued, IReadOnlyList<string> Errors);

public static class AutomationRunner
{
    public static AutomationRunResult RunDue(CatalogStore catalog, AutomationStore automation, SyncStore sync, string jobId, string channel, string shop, DateTime nowUtc)
    {
        if (!automation.TryClaimLease(jobId, nowUtc, TimeSpan.FromMinutes(5), out var leaseToken)) return new(0, Array.Empty<string>());
        var job = automation.Get(jobId);
        return RunClaimed(catalog, automation, sync, job, channel, shop, nowUtc, false, leaseToken);
    }

    public static AutomationRunResult RunDue(CatalogStore catalog, AutomationStore automation, SyncStore sync, string jobId, DateTime nowUtc)
    {
        if (!automation.TryClaimLease(jobId, nowUtc, TimeSpan.FromMinutes(5), out var leaseToken)) return new(0, Array.Empty<string>());
        var job = automation.Get(jobId);
        return RunClaimed(catalog, automation, sync, job, job.Channel, job.Shop, nowUtc, true, leaseToken);
    }

    static AutomationRunResult RunClaimed(CatalogStore catalog, AutomationStore automation, SyncStore sync, AutomationJob job, string channel, string shop, DateTime nowUtc, bool requireListingMapping, string leaseToken)
    {
        var errors = new List<string>(); var queued = 0;
        // Exhaustive by AutomationKind, not an is/else fallback: an unrecognized
        // job.Kind (a corrupt/future value) must never silently fall into the
        // price-sync path. AutomationStore.TryRead already excludes a genuinely
        // corrupt Kind from Get()/Due(), so this default is defense-in-depth for
        // any caller that constructs an AutomationJob outside the store.
        switch (job.Kind)
        {
            case AutomationKind.Xml or AutomationKind.Health or AutomationKind.Sync:
                try
                {
                    var operation = job.Kind switch { AutomationKind.Xml => "xml-import", AutomationKind.Health => "health-check", _ => "sync" };
                    var entity = string.IsNullOrWhiteSpace(job.TemplateKey) ? shop : job.TemplateKey;
                    sync.Enqueue(new SyncRequest(channel, operation, entity, $"{job.Id}:{nowUtc.Ticks}", shop)); queued = 1;
                }
                catch (Exception error) { errors.Add(error.Message); }
                break;
            case AutomationKind.Stock or AutomationKind.Price:
                foreach (var product in catalog.Products())
                {
                    var operation = job.Kind == AutomationKind.Stock ? "stock" : "price";
                    var entity = channel.Equals("etsy", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(product.EtsyListingId) ? product.EtsyListingId : product.Id;
                    try
                    {
                        if (requireListingMapping && channel.Equals("etsy", StringComparison.OrdinalIgnoreCase) && (!long.TryParse(entity, out var listingId) || listingId <= 0)) throw new InvalidOperationException("Etsy ilan eşlemesi eksik.");
                        var payload = job.Kind == AutomationKind.Stock ? catalog.PreviewStock(channel, shop, product.Id).ToString(System.Globalization.CultureInfo.InvariantCulture) : catalog.PreviewPrice(channel, shop, product.Id).Price.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                        sync.Enqueue(new SyncRequest(channel, operation, entity, $"{product.Id}:{product.UpdatedUtc.Ticks}:{payload}", shop)); queued++;
                    }
                    catch (Exception error)
                    {
                        var failed = sync.Enqueue(new SyncRequest(channel, operation, entity, $"{product.Id}:{product.UpdatedUtc.Ticks}:error", shop)); sync.Fail(failed.Id, error.Message); errors.Add($"{product.Sku}: {error.Message}");
                    }
                }
                break;
            default:
                errors.Add($"Tanımsız otomasyon türü ({(int)job.Kind}); iş çalıştırılmadı.");
                break;
        }
        if (errors.Count == 0) automation.Complete(job.Id, nowUtc, leaseToken); else automation.Fail(job.Id, string.Join("; ", errors), leaseToken);
        return new(queued, errors);
    }
}
