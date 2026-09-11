using System.Diagnostics;

namespace TrMarketplaceHubDesktop;

public sealed record LargeScaleMetrics(int ProductCount, int MatchingCount, int PageCount, TimeSpan Elapsed, long AllocatedBytes, long WorkingSetBytes);

public static class LargeScalePerformance
{
    public static LargeScaleMetrics Measure(int productCount = 100_000, int pageSize = 100)
    {
        if (productCount is < 1 or > 100_000 || pageSize is < 1 or > 1_000) throw new ArgumentOutOfRangeException();
        var products = Enumerable.Range(0, productCount).Select(i => (Id: i, Sku: $"SKU-{i:D6}", Active: i % 7 != 0)).ToArray();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var before = GC.GetTotalAllocatedBytes(true); var sw = Stopwatch.StartNew();
        var matches = products.Where(x => x.Active && x.Sku.Contains("42", StringComparison.Ordinal)).ToArray();
        var page = matches.Take(pageSize).ToArray();
        sw.Stop();
        return new(productCount, matches.Length, page.Length, sw.Elapsed, GC.GetTotalAllocatedBytes(false) - before, Environment.WorkingSet);
    }
}
