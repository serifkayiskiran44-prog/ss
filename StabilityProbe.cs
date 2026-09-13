using System.Diagnostics;

namespace TrMarketplaceHubDesktop;

public sealed record StabilityProbeResult(int Iterations, TimeSpan Elapsed, long AllocatedBytes, long WorkingSetBytes)
{
    public double AverageMilliseconds => Iterations == 0 ? 0 : Elapsed.TotalMilliseconds / Iterations;
}

/// <summary>Deterministic local-only measurement helper for soak smoke runs; it never calls a marketplace.</summary>
public static class StabilityProbe
{
    public static StabilityProbeResult Run(int iterations, Action operation)
    {
        if (iterations is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(iterations));
        ArgumentNullException.ThrowIfNull(operation);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var before = GC.GetTotalAllocatedBytes(true); var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) operation();
        stopwatch.Stop();
        return new(iterations, stopwatch.Elapsed, GC.GetTotalAllocatedBytes(false) - before, Environment.WorkingSet);
    }
}
