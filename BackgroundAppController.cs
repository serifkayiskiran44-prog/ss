using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public enum BackgroundAppState { Stopped, Running, Paused, Stopping }
public enum BackgroundJobAccess { LocalOnly, RemoteReadOnly, RemoteWrite }

public interface IBackgroundJob : IDisposable
{
    string Id { get; }
    BackgroundJobAccess Access { get; }
    Task ExecuteAsync(CancellationToken token);
}

public static class BackgroundJobFactory
{
    public static IReadOnlyList<IBackgroundJob> Create(string? dataDirectory)
    {
        var xml = new ScheduledXmlImportBackgroundJob(dataDirectory,
            new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(60) });
        return
        [
            new AutomationReadQueueBackgroundJob(dataDirectory, xml, new MarketplaceHealthReadExecutor(dataDirectory)),
            new MarketplaceOrderReadBackgroundJob(dataDirectory)
        ];
    }
}

/// <summary>Runs due XML imports. HTTP is read-only; all local writes retain the source revision fence.</summary>
public sealed class ScheduledXmlImportBackgroundJob : IBackgroundJob
{
    static readonly ConcurrentDictionary<string, SemaphoreSlim> SourceQueues = new(StringComparer.OrdinalIgnoreCase);
    readonly string? directory;
    readonly HttpClient http;
    readonly Func<DateTime> utcNow;
    public string Id => "scheduled-xml-import";
    public BackgroundJobAccess Access => BackgroundJobAccess.RemoteReadOnly;

    public ScheduledXmlImportBackgroundJob(string? directory, HttpClient http, Func<DateTime>? utcNow = null)
    {
        this.directory = directory; this.http = http ?? throw new ArgumentNullException(nameof(http)); this.utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public Task ExecuteAsync(CancellationToken token) => ExecuteDueAsync(Array.Empty<string>(), token);

    public async Task ExecuteDueAsync(IEnumerable<string> excludedSourceIds, CancellationToken token)
    {
        var catalog = new CatalogStore(directory);
        var errors = new List<string>();
        var excluded = excludedSourceIds.Select(id => id.Trim()).ToHashSet(StringComparer.Ordinal);
        var due = catalog.Sources().Where(source => source.Enabled && source.AutoImport &&
            !excluded.Contains(source.Id) &&
            utcNow() - (source.LastRunUtc ?? DateTime.MinValue) >= TimeSpan.FromMinutes(source.IntervalMinutes))
            .OrderBy(source => source.LastRunUtc ?? DateTime.MinValue)
            .ThenBy(source => source.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(source => source.Id, StringComparer.Ordinal)
            .ToList();
        foreach (var source in due)
        {
            try
            {
                await ExecuteSourceAsync(source.Id, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                errors.Add(Bound(AuditStore.Sanitize(error.Message)));
            }
        }
        if (errors.Count > 0) throw new InvalidOperationException(Bound(string.Join("; ", errors)));
    }

    public Task ExecuteSourceAsync(string sourceId, CancellationToken token) =>
        XmlSourceExecutionGate.RunCoalescedAsync(directory, sourceId, () => ExecuteQueuedSourceAsync(sourceId, token), token);

    async Task ExecuteQueuedSourceAsync(string sourceId, CancellationToken token)
    {
        var profile = Path.GetFullPath(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop"));
        var queue = SourceQueues.GetOrAdd(profile, _ => new SemaphoreSlim(1, 1));
        await queue.WaitAsync(token).ConfigureAwait(false);
        try { await ExecuteSourceCoreAsync(sourceId, token).ConfigureAwait(false); }
        finally { queue.Release(); }
    }

    async Task ExecuteSourceCoreAsync(string sourceId, CancellationToken token)
    {
        var catalog = new CatalogStore(directory);
        var source = catalog.Sources().SingleOrDefault(item => item.Id == sourceId && item.Enabled)
            ?? throw new InvalidOperationException("XML otomasyon kaynağı bulunamadı veya devre dışı.");
        token.ThrowIfCancellationRequested();
        var runStore = new XmlRunStore(directory);
        var runId = runStore.Start(source.Id);
        var status = "";
        var recordSourceRun = true;
        try
        {
            var xml = await new XmlSourceReader(http).ReadAsync(source.Location, XmlAuthStore.Load(source.Id, directory), token).ConfigureAwait(false);
            await RefreshAutoFxAsync(catalog, source, token).ConfigureAwait(false);
            var expectedRevision = CatalogStore.SourceConfigRevision(source);
            var rows = await Task.Run(() => XmlCatalog.Preview(xml, source, catalog), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var result = await Task.Run(() => catalog.ImportIfSourceCurrent(source, expectedRevision, rows), token).ConfigureAwait(false);
            runStore.Complete(runId, result);
            status = $"Otomatik: {result.Added} yeni / {result.Updated} güncel / {result.Unchanged} aynı";
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            recordSourceRun = false;
            runStore.Fail(runId, "Uygulama kapanışı nedeniyle iptal edildi.");
            throw new OperationCanceledException(token);
        }
        catch (Exception error)
        {
            status = Bound(AuditStore.Sanitize(error.Message));
            runStore.Fail(runId, status);
            throw new InvalidOperationException(status);
        }
        finally
        {
            if (recordSourceRun) catalog.TryRecordSourceRun(source.Id, utcNow(), status);
        }
    }

    async Task RefreshAutoFxAsync(CatalogStore catalog, XmlSource source, CancellationToken token)
    {
        if (source.PriceMode != "Formula" || !source.AutoFx || source.Currency == "TRY") return;
        var revision = CatalogStore.SourceConfigRevision(source);
        var quote = await new TcmbRates(http).FetchAsync(source.Currency, source.FxKind, token).ConfigureAwait(false);
        if (!catalog.TryRecordAutoFxQuote(source.Id, revision, quote))
            throw new InvalidOperationException("XML kaynağı kur okuması sırasında değişti; aktarım uygulanmadı.");
        source.TryPerTargetUnit = quote.TryPerUnit;
        source.FxRateDate = quote.RateDate;
        source.FxFetchedUtc = quote.FetchedUtc;
    }

    static string Bound(string value) => value.Length <= BackgroundAppController.MaxNotificationLength ? value : value[..BackgroundAppController.MaxNotificationLength];
    public void Dispose() => http.Dispose();
}

/// <summary>Claims only local/XML and health-read automation; write-capable kinds remain untouched.</summary>
public interface IMarketplaceHealthReadExecutor : IDisposable
{
    Task ExecuteAsync(AutomationJob job, CancellationToken token);
}

/// <summary>Runs an account-scoped remote read and applies the result through the connection revision fence.</summary>
public sealed class MarketplaceHealthReadExecutor : IMarketplaceHealthReadExecutor
{
    readonly string? directory;
    readonly Func<HttpClient> httpFactory;
    readonly Func<MarketplaceConnection, EtsyCredentials?> etsyCredentials;

    public MarketplaceHealthReadExecutor(string? directory, Func<HttpClient>? httpFactory = null,
        Func<MarketplaceConnection, EtsyCredentials?>? etsyCredentials = null)
    {
        this.directory = directory;
        this.httpFactory = httpFactory ?? (() => new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = TimeSpan.FromSeconds(60)
        });
        this.etsyCredentials = etsyCredentials ?? (connection =>
            new MarketplaceCredentialVault(directory).Load<EtsyCredentials>(connection.Id, connection.Channel, connection.ShopId));
    }

    public async Task ExecuteAsync(AutomationJob job, CancellationToken token)
    {
        if (job.Kind != AutomationKind.Health) throw new ArgumentException("Sağlık yürütücüsü yalnız sağlık otomasyonu kabul eder.", nameof(job));
        var connections = new MarketplaceConnectionStore(directory);
        var connection = connections.Find(job.Channel, job.Shop)
            ?? throw new InvalidOperationException("NOT_CONFIGURED: Sağlık otomasyonu mağaza bağlantısı bulunamadı.");
        if (!connection.Enabled) throw new InvalidOperationException("DISABLED: Devre dışı mağaza için sağlık otomasyonu çalıştırılamaz.");
        var revision = connection.Revision;
        try
        {
            using var http = httpFactory();
            switch (connection.Channel)
            {
                case "etsy":
                    var etsy = etsyCredentials(connection)
                        ?? throw new InvalidOperationException("NOT_CONFIGURED: Etsy şifreli bağlantısı bulunamadı.");
                    await new EtsyMetadataClient(http).GetShopAsync(etsy, token).ConfigureAwait(false);
                    break;
                case "trendyol":
                    var trendyol = new MarketplaceCredentialVault(directory).Load<TrendyolSettings>(connection.Id, connection.Channel, connection.ShopId)
                        ?? throw new InvalidOperationException("NOT_CONFIGURED: Trendyol şifreli bağlantısı bulunamadı.");
                    using (var client = new Trendyol.TrendyolApiClient(trendyol, http))
                        await client.GetAddressesAsync(token).ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidOperationException("LIVE_API_BLOCKED: Bu kanal için hesap kapsamlı salt okunur sağlık yürütücüsü yok.");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            ApplyFencedObservation(connections, connection, revision, false, ApiHealthClassifier.FromException(error), error.Message);
            throw;
        }
        ApplyFencedObservation(connections, connection, revision, true,
            new ApiHealthObservation { State = "HEALTHY", AuthStatus = "VALID" }, "");
    }

    void ApplyFencedObservation(MarketplaceConnectionStore connections, MarketplaceConnection expected, long revision,
        bool success, ApiHealthObservation observation, string error)
    {
        EnsureApplied(connections.RecordTest(expected.Id, revision, success, error));
        var current = connections.Get(expected.Id);
        if (current is null || !current.Enabled || current.Revision != revision ||
            !string.Equals(current.Channel, expected.Channel, StringComparison.Ordinal) ||
            !string.Equals(current.ShopId, expected.ShopId, StringComparison.Ordinal))
            throw new InvalidOperationException("Sağlık sonucu mağaza ayarı değiştiği için uygulanmadı.");
        new ApiHealthStore(directory).Observe(expected.Channel, expected.ShopId, observation);
    }

    static void EnsureApplied(ConnectionTestApplyResult outcome)
    {
        if (outcome != ConnectionTestApplyResult.Applied)
            throw new InvalidOperationException("Sağlık sonucu mağaza ayarı değiştiği için uygulanmadı.");
    }

    public void Dispose() { }
}

public sealed class AutomationReadQueueBackgroundJob : IBackgroundJob
{
    readonly string? directory;
    readonly Func<DateTime> utcNow;
    readonly ScheduledXmlImportBackgroundJob xml;
    readonly IMarketplaceHealthReadExecutor health;
    public string Id => "automation-read-queue";
    public BackgroundJobAccess Access => BackgroundJobAccess.RemoteReadOnly;

    public AutomationReadQueueBackgroundJob(string? directory, Func<DateTime>? utcNow = null)
        : this(directory,
            new ScheduledXmlImportBackgroundJob(directory,
                new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(60) }, utcNow),
            new MarketplaceHealthReadExecutor(directory), utcNow)
    { }

    public AutomationReadQueueBackgroundJob(string? directory, ScheduledXmlImportBackgroundJob xml,
        IMarketplaceHealthReadExecutor health, Func<DateTime>? utcNow = null)
    {
        this.directory = directory; this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        this.xml = xml ?? throw new ArgumentNullException(nameof(xml));
        this.health = health ?? throw new ArgumentNullException(nameof(health));
    }

    public async Task ExecuteAsync(CancellationToken token)
    {
        var automation = new AutomationStore(directory);
        var errors = new List<string>();
        var now = DateTime.SpecifyKind(utcNow(), DateTimeKind.Utc);
        var managedXmlSources = automation.List()
            .Where(row => row.Enabled && row.Kind == AutomationKind.Xml)
            .Select(row => XmlSourceTarget(row).Trim())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        try { await xml.ExecuteDueAsync(managedXmlSources, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { errors.Add(Bound(AuditStore.Sanitize(error.Message))); }

        var due = automation.Due(now).Where(IsReadOnlyKind)
            .OrderBy(row => row.NextRunUtc).ThenBy(row => row.Id, StringComparer.Ordinal).ToList();
        foreach (var group in due.Where(row => row.Kind == AutomationKind.Xml)
            .GroupBy(row => XmlSourceTarget(row).Trim(), StringComparer.Ordinal)
            .OrderBy(group => group.Min(row => row.NextRunUtc)).ThenBy(group => group.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var claimed = new List<(AutomationJob Row, string LeaseToken)>();
            foreach (var row in group)
                if (automation.TryClaimLease(row.Id, now, TimeSpan.FromMinutes(5), out var leaseToken))
                    claimed.Add((row, leaseToken));
            if (claimed.Count == 0) continue;
            try
            {
                token.ThrowIfCancellationRequested();
                await xml.ExecuteSourceAsync(group.Key, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                foreach (var item in claimed) automation.ReleaseLease(item.Row.Id, item.LeaseToken);
                throw;
            }
            catch (Exception error)
            {
                var safe = Bound(AuditStore.Sanitize(error.Message));
                foreach (var item in claimed)
                    try { automation.Fail(item.Row.Id, safe, item.LeaseToken); }
                    catch (InvalidOperationException) { }
                errors.Add(safe);
                continue;
            }
            foreach (var item in claimed)
                try { automation.Complete(item.Row.Id, now, item.LeaseToken); }
                catch (InvalidOperationException error) { errors.Add(Bound(AuditStore.Sanitize(error.Message))); }
        }

        foreach (var row in due.Where(row => row.Kind == AutomationKind.Health))
        {
            token.ThrowIfCancellationRequested();
            if (!automation.TryClaimLease(row.Id, now, TimeSpan.FromMinutes(5), out var leaseToken)) continue;
            try
            {
                await health.ExecuteAsync(row, token).ConfigureAwait(false);
                automation.Complete(row.Id, now, leaseToken);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                automation.ReleaseLease(row.Id, leaseToken);
                throw;
            }
            catch (Exception error)
            {
                var safe = Bound(AuditStore.Sanitize(error.Message));
                try { automation.Fail(row.Id, safe, leaseToken); }
                catch (InvalidOperationException) { }
                errors.Add(safe);
            }
        }
        if (errors.Count > 0) throw new InvalidOperationException(Bound(string.Join("; ", errors)));
    }

    static bool IsReadOnlyKind(AutomationJob job) => job.Kind is AutomationKind.Xml or AutomationKind.Health;
    static string XmlSourceTarget(AutomationJob job) => string.Equals(job.TemplateKey, "xml-refresh", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(job.TemplateKey)
        ? job.Shop : job.TemplateKey;
    static string Bound(string value) => value.Length <= BackgroundAppController.MaxNotificationLength ? value : value[..BackgroundAppController.MaxNotificationLength];
    public void Dispose() { xml.Dispose(); health.Dispose(); }

}

public sealed class MarketplaceOrderReadBackgroundJob : IBackgroundJob
{
    readonly MarketplaceOrderSyncService service;
    public string Id => "marketplace-order-read";
    public BackgroundJobAccess Access => BackgroundJobAccess.RemoteReadOnly;
    public MarketplaceOrderReadBackgroundJob(string? directory) => service = new MarketplaceOrderSyncService(directory);
    public async Task ExecuteAsync(CancellationToken token)
    {
        var results = await service.RefreshAllAsync(token).ConfigureAwait(false);
        var errors = results.Where(row => row.Status == MarketplaceOrderSyncStatus.Failed).Select(row => row.Error).Where(error => error.Length > 0).ToList();
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("; ", errors));
    }
    public void Dispose() { }
}

public sealed class DelegateBackgroundJob : IBackgroundJob
{
    readonly Func<CancellationToken, Task> execute;
    readonly Action? dispose;
    public string Id { get; }
    public BackgroundJobAccess Access { get; }
    public bool IsDisposed { get; private set; }

    public DelegateBackgroundJob(string id, BackgroundJobAccess access, Func<CancellationToken, Task> execute, Action? dispose = null)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Arka plan iş kimliği zorunlu.", nameof(id));
        Id = id.Trim(); Access = access; this.execute = execute ?? throw new ArgumentNullException(nameof(execute)); this.dispose = dispose;
    }

    public Task ExecuteAsync(CancellationToken token) => execute(token);
    public void Dispose() { if (IsDisposed) return; IsDisposed = true; dispose?.Invoke(); }
}

public sealed record BackgroundJobResult(string JobId, bool Succeeded, string Error);
public sealed record WindowCloseDecision(bool CancelClose, bool HideWindow, bool ShowBackgroundNotification);

/// <summary>Owns the one scheduler loop for the whole application process.</summary>
public sealed class BackgroundAppController : IAsyncDisposable
{
    const string PausePreference = "background:paused";
    const string CloseToTrayPreference = "background:close-to-tray";
    public const int MaxNotificationLength = 300;

    readonly string? dataDirectory;
    readonly IReadOnlyList<IBackgroundJob> jobs;
    readonly TimeSpan interval;
    readonly UiPreferenceStore preferences;
    readonly CancellationTokenSource lifetime = new();
    readonly SemaphoreSlim runGate = new(1, 1);
    readonly object stateGate = new();
    Task? schedulerLoop;
    bool started;
    bool jobsDisposed;
    bool firstHideNotificationShown;
    bool explicitExit;
    BackgroundAppState state;

    public BackgroundAppState State { get { lock (stateGate) return state; } }
    public bool CloseToTray
    {
        get => !string.Equals(preferences.Get(CloseToTrayPreference), "false", StringComparison.OrdinalIgnoreCase);
        set => preferences.Set(CloseToTrayPreference, value ? "true" : "false");
    }
    public event EventHandler? StateChanged;
    public event EventHandler<string>? NotificationRequested;

    public BackgroundAppController(string? dataDirectory, IEnumerable<IBackgroundJob> jobs, TimeSpan? interval = null)
    {
        this.dataDirectory = dataDirectory;
        this.jobs = (jobs ?? throw new ArgumentNullException(nameof(jobs))).ToArray();
        if (this.jobs.Any(job => job.Access == BackgroundJobAccess.RemoteWrite))
            throw new ArgumentException("Arka plan denetleyicisi uzak yazma işi kabul etmez.", nameof(jobs));
        if (this.jobs.Select(job => job.Id).Distinct(StringComparer.Ordinal).Count() != this.jobs.Count)
            throw new ArgumentException("Arka plan iş kimlikleri benzersiz olmalı.", nameof(jobs));
        this.interval = interval ?? TimeSpan.FromMinutes(1);
        if (this.interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        preferences = new UiPreferenceStore(dataDirectory);
        state = string.Equals(preferences.Get(PausePreference), "true", StringComparison.OrdinalIgnoreCase)
            ? BackgroundAppState.Paused : BackgroundAppState.Stopped;
    }

    public void Start()
    {
        lock (stateGate)
        {
            if (started || state == BackgroundAppState.Stopping) return;
            started = true;
            if (state != BackgroundAppState.Paused) state = BackgroundAppState.Running;
            schedulerLoop = Task.Run(() => SchedulerLoopAsync(lifetime.Token));
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        lock (stateGate)
        {
            if (state == BackgroundAppState.Stopping) return;
            state = BackgroundAppState.Paused;
            preferences.Set(PausePreference, "true");
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Resume()
    {
        lock (stateGate)
        {
            if (state == BackgroundAppState.Stopping) return;
            state = started ? BackgroundAppState.Running : BackgroundAppState.Stopped;
            preferences.Set(PausePreference, "false");
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<IReadOnlyList<BackgroundJobResult>> RunNowAsync()
    {
        if (State == BackgroundAppState.Stopping || lifetime.IsCancellationRequested) return Array.Empty<BackgroundJobResult>();
        try { await runGate.WaitAsync(lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return Array.Empty<BackgroundJobResult>(); }
        try
        {
            var results = new List<BackgroundJobResult>(jobs.Count);
            foreach (var job in jobs)
            {
                try
                {
                    lifetime.Token.ThrowIfCancellationRequested();
                    await job.ExecuteAsync(lifetime.Token).ConfigureAwait(false);
                    results.Add(new(job.Id, true, ""));
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                    results.Add(new(job.Id, false, "İş uygulama kapanışı nedeniyle iptal edildi."));
                    break;
                }
                catch (Exception error)
                {
                    var safe = Bound(AuditStore.Sanitize(error.Message));
                    results.Add(new(job.Id, false, safe));
                    RecordFailure(job.Id, safe);
                    NotificationRequested?.Invoke(this, safe);
                }
            }
            return results;
        }
        finally { runGate.Release(); }
    }

    public WindowCloseDecision DecideWindowClose()
    {
        if (explicitExit || !CloseToTray) return new(false, false, false);
        var notify = !firstHideNotificationShown;
        firstHideNotificationShown = true;
        return new(true, true, notify);
    }

    public void RequestExplicitExit() => explicitExit = true;

    public async Task ShutdownAsync()
    {
        Task? loop;
        lock (stateGate)
        {
            if (state == BackgroundAppState.Stopped && jobsDisposed) return;
            state = BackgroundAppState.Stopping;
            lifetime.Cancel();
            loop = schedulerLoop;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        await runGate.WaitAsync().ConfigureAwait(false);
        runGate.Release();
        DisposeJobs();
        lock (stateGate) state = BackgroundAppState.Stopped;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    async Task SchedulerLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            if (State == BackgroundAppState.Running) await RunNowAsync().ConfigureAwait(false);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                if (State == BackgroundAppState.Running) await RunNowAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    void RecordFailure(string jobId, string detail)
    {
        try
        {
            new AuditStore(dataDirectory).Append(new AuditEvent
            {
                Module = "background", Action = jobId, Outcome = "Failed", Detail = detail
            });
        }
        catch (Exception) { }
    }

    static string Bound(string value) => value.Length <= MaxNotificationLength ? value : value[..MaxNotificationLength];

    void DisposeJobs()
    {
        if (jobsDisposed) return;
        jobsDisposed = true;
        foreach (var job in jobs) try { job.Dispose(); } catch (Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
        lifetime.Dispose();
        runGate.Dispose();
    }
}
