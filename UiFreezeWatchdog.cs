using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace TrMarketplaceHubDesktop;

/// <summary>One UI-thread freeze: when it began, how long the dispatcher stayed unresponsive, the command that was active, and a correlation id. Never a stack, never a payload.</summary>
public sealed record UiFreezeEvent(DateTime AtUtc, long DurationMs, string ActiveCommand, string Correlation);

/// <summary>
/// The logical UI operations in flight (#879): a guarded command, a shortcut, a measured view load — entered by
/// name (a code identifier or a command key, never a payload) and left when done. The innermost one is what a
/// freeze diagnostic names as the active command; a latency measurement enters with its own correlation id so the
/// freeze and the slow load share it.
/// </summary>
public static class UiActivity
{
    public const string Idle = "idle";
    static readonly Regex NameShape = new("^[A-Za-z0-9:_.-]{1,64}$", RegexOptions.Compiled);
    static readonly List<(string Name, string Correlation)> active = new();
    static readonly object gate = new();

    /// <summary>Enters an activity; dispose the token to leave it. A name that is not an identifier is recorded as "unnamed".</summary>
    public static IDisposable Enter(string? name, string? correlation = null)
    {
        var entry = (Safe(name), string.IsNullOrWhiteSpace(correlation) ? Guid.NewGuid().ToString("N")[..12] : correlation!);
        lock (gate) active.Add(entry);
        return new Token(entry);
    }

    /// <summary>A code identifier from a delegate: the method name for a named method, the compiler's lambda name otherwise, cleaned to identifier characters.</summary>
    public static string NameOf(Delegate work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var name = work.Method.Name;
        var cleaned = Regex.Replace(name, "[^A-Za-z0-9_]", "-").Trim('-');
        return cleaned.Length == 0 ? "unnamed" : cleaned.Length > 64 ? cleaned[..64] : cleaned;
    }

    public static string Safe(string? name) => string.IsNullOrWhiteSpace(name) ? "unnamed" : NameShape.IsMatch(name) ? name : "unnamed";

    /// <summary>The innermost activity in flight, or idle.</summary>
    public static (string Name, string Correlation) Current { get { lock (gate) return active.Count == 0 ? (Idle, "") : active[^1]; } }

    sealed class Token : IDisposable
    {
        readonly (string, string) entry; bool disposed;
        public Token((string, string) entry) => this.entry = entry;
        public void Dispose() { if (disposed) return; disposed = true; lock (gate) { var index = active.LastIndexOf(entry); if (index >= 0) active.RemoveAt(index); } }
    }
}

/// <summary>
/// UI freeze watchdog diagnostics (#879). A background thread posts a heartbeat to the dispatcher at Normal priority
/// every <see cref="IntervalMs"/>; an operation the dispatcher cannot run within <see cref="ThresholdMs"/> means the
/// UI thread is blocked. When the heartbeat finally runs the freeze is recorded once — its duration, the command
/// that was active and a correlation id — as an in-memory event and as a sanitized audit row (module "ui", action
/// "freeze"); never a stack, never a payload. Legitimate long work that awaits (a Task.Run, a Task.Delay) never
/// blocks the heartbeat, so it is never reported; the first heartbeat after start is a warm-up and not measured; a
/// gap while a debugger is attached is a breakpoint, not a freeze; a dispatcher that is shutting down ends the
/// watch. Recovery clears the frozen flag and the diagnostics line counts the freezes of this session.
/// </summary>
public sealed class UiFreezeWatchdog : IDisposable
{
    public const int DefaultThresholdMs = 1500;
    public const int DefaultIntervalMs = 250;
    public const int MinThresholdMs = 100;
    public const int EventLimit = 100;
    public const string DiagnosticName = "UI donması";
    public const string AuditModule = "ui";
    public const string AuditAction = "freeze";

    readonly Dispatcher dispatcher; readonly AuditStore? audit; readonly int thresholdMs; readonly int intervalMs; readonly Action<UiFreezeEvent>? onFreeze;
    readonly Thread thread; readonly ManualResetEventSlim stop = new(false); readonly object gate = new();
    readonly List<UiFreezeEvent> events = new();
    long lastPongTicks; long pendingPingTicks; bool pendingPing; bool warm; volatile bool frozen; string frozenCommand = UiActivity.Idle; string frozenCorrelation = "";

    UiFreezeWatchdog(Dispatcher dispatcher, AuditStore? audit, int thresholdMs, int intervalMs, Action<UiFreezeEvent>? onFreeze)
    {
        this.dispatcher = dispatcher; this.audit = audit; this.thresholdMs = Math.Max(MinThresholdMs, thresholdMs); this.intervalMs = Math.Clamp(intervalMs, 10, this.thresholdMs); this.onFreeze = onFreeze;
        thread = new Thread(Loop) { IsBackground = true, Name = "ui-freeze-watchdog" };
    }

    /// <summary>Starts watching a dispatcher; the audit store may be null (events stay in memory only).</summary>
    public static UiFreezeWatchdog Start(Dispatcher dispatcher, AuditStore? audit, int thresholdMs = DefaultThresholdMs, int intervalMs = DefaultIntervalMs, Action<UiFreezeEvent>? onFreeze = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        var watchdog = new UiFreezeWatchdog(dispatcher, audit, thresholdMs, intervalMs, onFreeze);
        watchdog.thread.Start();
        return watchdog;
    }

    public int ThresholdMs => thresholdMs;
    /// <summary>True while a heartbeat is overdue.</summary>
    public bool IsFrozen => frozen;
    /// <summary>The freezes recorded in this session, oldest first, at most <see cref="EventLimit"/>.</summary>
    public IReadOnlyList<UiFreezeEvent> Events { get { lock (gate) return events.ToList(); } }

    void Loop()
    {
        lastPongTicks = Stopwatch.GetTimestamp();
        while (!stop.Wait(intervalMs))
        {
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
            var now = Stopwatch.GetTimestamp();
            if (!pendingPing)
            {
                pendingPing = true; pendingPingTicks = now;
                try { dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Pong)); }
                catch (Exception) { return; } // the dispatcher is gone
                continue;
            }
            var overdueMs = ElapsedMs(pendingPingTicks, now);
            if (!frozen && overdueMs >= thresholdMs && warm && !Debugger.IsAttached)
            {
                var (command, correlation) = UiActivity.Current;
                frozen = true; frozenCommand = command; frozenCorrelation = string.IsNullOrEmpty(correlation) ? Guid.NewGuid().ToString("N")[..12] : correlation;
            }
        }
    }

    // Runs on the dispatcher: the heartbeat answered. A freeze that was in progress is over and is recorded once, with the time the dispatcher stayed unresponsive.
    void Pong()
    {
        var now = Stopwatch.GetTimestamp();
        var wasFrozen = frozen;
        var began = pendingPingTicks;
        pendingPing = false; lastPongTicks = now; frozen = false;
        if (!warm) { warm = true; return; }
        if (!wasFrozen) return;
        var freeze = new UiFreezeEvent(DateTime.UtcNow.AddMilliseconds(-ElapsedMs(began, now)), ElapsedMs(began, now), frozenCommand, frozenCorrelation);
        lock (gate) { events.Add(freeze); if (events.Count > EventLimit) events.RemoveAt(0); }
        try { audit?.Append(new AuditEvent { Module = AuditModule, Action = AuditAction, Outcome = "WARN", Detail = Describe(freeze) }); }
        catch (Exception error) { Debug.WriteLine(error.Message); } // a diagnostic never breaks the UI thread it just got back
        try { onFreeze?.Invoke(freeze); } catch (Exception error) { Debug.WriteLine(error.Message); }
    }

    static long ElapsedMs(long fromTicks, long toTicks) => (long)((toTicks - fromTicks) * 1000d / Stopwatch.Frequency);

    /// <summary>The sanitized one-line description: duration, active command, correlation.</summary>
    public static string Describe(UiFreezeEvent freeze)
    {
        ArgumentNullException.ThrowIfNull(freeze);
        return AuditStore.Sanitize($"UI {freeze.DurationMs:N0} ms yanıt vermedi · etkin komut: {UiActivity.Safe(freeze.ActiveCommand)} · korelasyon: {freeze.Correlation}");
    }

    /// <summary>The diagnostics line for a session's freezes: a note when nothing is watching (null), OK when none, WARN with the count, the longest and the last active command.</summary>
    public static DiagnosticCheck Check(IReadOnlyList<UiFreezeEvent>? freezes, int thresholdMs = DefaultThresholdMs)
    {
        if (freezes is null) return new DiagnosticCheck(DiagnosticName, "OK", "UI izleyici bu süreçte çalışmıyor (yalnız uygulama oturumunda izlenir); donma izlenmiyor");
        if (freezes.Count == 0) return new DiagnosticCheck(DiagnosticName, "OK", $"Bu oturumda {thresholdMs:N0} ms üstü donma yok");
        var longest = freezes.Max(f => f.DurationMs); var last = freezes[^1];
        return new DiagnosticCheck(DiagnosticName, "WARN", AuditStore.Sanitize($"{freezes.Count} donma · en uzun {longest:N0} ms · son: {UiActivity.Safe(last.ActiveCommand)} ({last.DurationMs:N0} ms, korelasyon {last.Correlation})"));
    }

    /// <summary>The process-wide watchdog the application starts; the diagnostics read its events.</summary>
    public static UiFreezeWatchdog? Current { get; private set; }
    public static void Install(UiFreezeWatchdog? watchdog) => Current = watchdog;

    public void Dispose()
    {
        stop.Set();
        if (thread.IsAlive && Thread.CurrentThread != thread) thread.Join(TimeSpan.FromSeconds(2));
        if (ReferenceEquals(Current, this)) Current = null;
        stop.Dispose();
    }
}
