using System.Collections.Concurrent;

namespace TrMarketplaceHubDesktop;

/// <summary>Serializes work per XML source while allowing independent sources to run concurrently.</summary>
public static class XmlSourceExecutionGate
{
    static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    public static async Task RunAsync(string sourceId, Func<Task> operation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("XML kaynak kimliği zorunlu.", nameof(sourceId));
        ArgumentNullException.ThrowIfNull(operation);
        var gate = Gates.GetOrAdd(sourceId.Trim(), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await operation().ConfigureAwait(false); }
        finally { gate.Release(); }
    }
}
