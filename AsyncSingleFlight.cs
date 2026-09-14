namespace TrMarketplaceHubDesktop;

/// Serializes concurrent async callers of the same logical operation onto one at a
/// time, so a check-then-act sequence (read token -> refresh if expiring -> save)
/// cannot interleave: while one caller is inside the action, later callers wait
/// their turn instead of racing their own copy of the same read-refresh-save flow.
public sealed class AsyncSingleFlight<T>
{
    readonly SemaphoreSlim gate = new(1, 1);

    public async Task<T> RunAsync(Func<Task<T>> action, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await action().ConfigureAwait(false); }
        finally { gate.Release(); }
    }
}
