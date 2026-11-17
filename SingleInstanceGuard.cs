using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TrMarketplaceHubDesktop;

/// <summary>A data-directory-scoped process guard with a second-launch activation signal.</summary>
public sealed class SingleInstanceGuard : IDisposable
{
    public const int MaxInstanceNameLength = 96;
    readonly ManualResetEvent stop = new(false);
    readonly ManualResetEvent ready = new(false);
    readonly Thread ownerThread;
    readonly EventWaitHandle activation;
    readonly RegisteredWaitHandle? activationRegistration;
    readonly Action? activationRequested;
    bool acquired;
    bool disposed;

    public string InstanceName { get; }

    SingleInstanceGuard(string instanceName, Action? activationRequested)
    {
        InstanceName = instanceName;
        this.activationRequested = activationRequested;
        ownerThread = new Thread(OwnMutex) { IsBackground = true, Name = "MonoBridge single-instance guard" };
        ownerThread.Start();
        if (!ready.WaitOne(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Tek örnek koruması başlatılamadı.");
        activation = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + "-activate");
        if (acquired && activationRequested is not null)
            activationRegistration = ThreadPool.RegisterWaitForSingleObject(activation, OnActivation, null, Timeout.Infinite, false);
    }

    public static SingleInstanceGuard? TryAcquire(string? dataDirectory, Action? activationRequested = null)
    {
        var name = CreateName(dataDirectory);
        var candidate = new SingleInstanceGuard(name, activationRequested);
        if (candidate.acquired) return candidate;
        try { candidate.activation.Set(); }
        finally { candidate.Dispose(); }
        return null;
    }

    static string CreateName(string? directory)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        var normalized = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return "Local\\MonoBridge-" + hash;
    }

    void OwnMutex()
    {
        using var mutex = new Mutex(false, InstanceName);
        try
        {
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            ready.Set();
            if (!acquired) return;
            stop.WaitOne();
            mutex.ReleaseMutex();
        }
        finally { ready.Set(); }
    }

    void OnActivation(object? _, bool timedOut)
    {
        if (!timedOut && !disposed) activationRequested?.Invoke();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        activationRegistration?.Unregister(null);
        stop.Set();
        if (ownerThread.IsAlive) ownerThread.Join(TimeSpan.FromSeconds(5));
        activation.Dispose();
        stop.Dispose();
        ready.Dispose();
    }
}
