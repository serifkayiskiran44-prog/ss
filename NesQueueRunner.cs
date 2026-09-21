using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace TrMarketplaceHubDesktop;

public static class NesQueueRuntime
{
    public static Action? WakeRequested { get; set; }
    public static void Wake() => WakeRequested?.Invoke();
}

public sealed class NesQueueRunner : IAsyncDisposable
{
    readonly Func<CancellationToken, Task<bool>> processNext;
    readonly TimeSpan idleDelay;
    readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropWrite });
    readonly object gate = new();
    CancellationTokenSource? cancellation;
    Task? loop;

    public event Action<string>? StatusChanged;

    public NesQueueRunner(Func<CancellationToken, Task<bool>> processNext, TimeSpan idleDelay)
    {
        this.processNext = processNext ?? throw new ArgumentNullException(nameof(processNext));
        if (idleDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idleDelay));
        this.idleDelay = idleDelay;
    }

    public void Start()
    {
        lock (gate)
        {
            if (loop is { IsCompleted: false }) return;
            cancellation?.Dispose(); cancellation = new CancellationTokenSource();
            loop = RunAsync(cancellation.Token);
        }
        Wake();
    }

    public void Wake() => wake.Writer.TryWrite(true);

    async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var signal = wake.Reader.ReadAsync(token).AsTask();
                var timer = Task.Delay(idleDelay, token);
                await Task.WhenAny(signal, timer);
                token.ThrowIfCancellationRequested();
                while (wake.Reader.TryRead(out _)) { }
                StatusChanged?.Invoke("NES kuyruğu çalışıyor");
                while (await processNext(token)) token.ThrowIfCancellationRequested();
                StatusChanged?.Invoke("NES kuyruğu bekliyor");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            StatusChanged?.Invoke("NES kuyruğu durdu: " + MarketplaceConnectionStore.Redact(ex.Message));
        }
    }

    public async Task StopAsync()
    {
        Task? current;
        lock (gate) { cancellation?.Cancel(); current = loop; }
        if (current is not null) try { await current; } catch (OperationCanceledException) { }
        lock (gate) { loop = null; }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        lock (gate) { cancellation?.Dispose(); cancellation = null; }
    }
}
