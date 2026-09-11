namespace TrMarketplaceHubDesktop.Catalog;

public sealed record SyncRetryPreview(string JobId, string Channel, string ShopId, string EntityId, DateTime ExpectedUpdatedUtc, string Detail);

public static class SyncOperations
{
    public static IReadOnlyList<SyncRetryPreview> CreateRetryPreview(IEnumerable<SyncJob> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        return jobs.Where(x => x.Status == SyncStatus.Failed && SyncStore.IsRetryable(x.ErrorClass) && x.FailureCount < 3)
            .Select(x => new SyncRetryPreview(x.Id, x.Channel, x.ShopId, x.EntityId, x.UpdatedUtc, "Tekil retry önizlemesi; onay olmadan kuyruk değişmez.")).ToArray();
    }

    public static int ApplyApprovedRetry(SyncStore store, IEnumerable<SyncRetryPreview> previews, bool approved)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(previews);
        if (!approved) return 0;
        var applied = 0;
        foreach (var preview in previews)
        {
            var current = store.Get(preview.JobId);
            if (current.UpdatedUtc != preview.ExpectedUpdatedUtc || current.Channel != preview.Channel || current.ShopId != preview.ShopId || current.EntityId != preview.EntityId)
                throw new InvalidOperationException("Sync retry önizlemesi güncel değil; yeniden önizleme oluşturun.");
            store.Retry(preview.JobId); applied++;
        }
        return applied;
    }
}
