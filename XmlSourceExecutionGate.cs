using System.Collections.Concurrent;
using System.IO;

namespace TrMarketplaceHubDesktop;

/// <summary>Serializes work per XML source while allowing independent sources to run concurrently.</summary>
public static class XmlSourceExecutionGate
{
    static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);
    static readonly ConcurrentDictionary<string, Task> InFlight = new(StringComparer.Ordinal);

    public static async Task RunAsync(string sourceId, Func<Task> operation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("XML kaynak kimliği zorunlu.", nameof(sourceId));
        ArgumentNullException.ThrowIfNull(operation);
        var gate = Gates.GetOrAdd(sourceId.Trim(), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await operation().ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    /// <summary>Concurrent schedulers for the same source share one execution and its outcome.</summary>
    public static Task RunCoalescedAsync(string? dataDirectory, string sourceId, Func<Task> operation, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("XML kaynak kimliği zorunlu.", nameof(sourceId));
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        dataDirectory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        var scope = Path.GetFullPath(dataDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var key = scope + "\0" + sourceId.Trim();
        while (true)
        {
            if (InFlight.TryGetValue(key, out var existing)) return existing;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!InFlight.TryAdd(key, completion.Task)) continue;
            _ = ExecuteOwnerAsync(key, operation, completion);
            return completion.Task;
        }
    }

    static async Task ExecuteOwnerAsync(string key, Func<Task> operation, TaskCompletionSource completion)
    {
        try
        {
            await operation().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (OperationCanceledException error) { completion.TrySetCanceled(error.CancellationToken); }
        catch (Exception error) { completion.TrySetException(error); }
        finally { InFlight.TryRemove(key, out _); }
    }
}
