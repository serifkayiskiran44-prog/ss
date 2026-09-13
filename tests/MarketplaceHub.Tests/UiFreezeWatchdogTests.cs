using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #879 (PERFORMANCE: UI freeze watchdog diagnostics). A background heartbeat the dispatcher cannot run within the
// threshold is a freeze: recorded once when the dispatcher answers again, with its duration, the command that was
// active and a correlation id — never a stack, never a payload — as an in-memory event and a sanitized audit row.
// Long legitimate async work that awaits never blocks the heartbeat and is never reported; the first heartbeat is a
// warm-up; recovery clears the frozen flag; a second block is a second event; the application's entry installs the
// process-wide watchdog whose events the diagnostics read.
[TestClass]
public sealed class UiFreezeWatchdogTests
{
    [TestMethod]
    public void ABlockedDispatcherIsRecordedOnceWithTheActiveCommandAndLegitimateAsyncWorkIsNot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ui-freeze-" + Guid.NewGuid().ToString("N"));
        var (dispatcher, thread) = StartDispatcher();
        UiFreezeWatchdog watchdog = null;
        try
        {
            Directory.CreateDirectory(root);
            var audit = new AuditStore(root);
            var seen = new List<UiFreezeEvent>();
            watchdog = UiFreezeWatchdog.Start(dispatcher, audit, thresholdMs: 200, intervalMs: 25, onFreeze: f => { lock (seen) seen.Add(f); });
            Assert.AreEqual(200, watchdog.ThresholdMs);

            // Warm-up and an idle dispatcher: nothing to report.
            Thread.Sleep(300);
            Assert.AreEqual(0, watchdog.Events.Count); Assert.IsFalse(watchdog.IsFrozen);

            // Long legitimate async work on the UI thread awaits and never blocks the heartbeat.
            var done = new ManualResetEventSlim(false);
            dispatcher.BeginInvoke(new Action(async () => { using var activity = UiActivity.Enter("legitimate-wait"); await Task.Delay(600); done.Set(); }));
            Assert.IsTrue(done.Wait(5000), "the async work finished"); Thread.Sleep(300);
            Assert.AreEqual(0, watchdog.Events.Count, "an await is not a freeze"); Assert.IsFalse(watchdog.IsFrozen);

            // A simulated block inside a named command: detected while it lasts, recorded once when the dispatcher answers again.
            dispatcher.Invoke(() => { using var activity = UiActivity.Enter("simulated-block", "corr00000001"); Thread.Sleep(700); });
            WaitUntil(() => watchdog.Events.Count >= 1, "the freeze event");
            Thread.Sleep(300);
            Assert.IsFalse(watchdog.IsFrozen, "recovered");
            var freeze = watchdog.Events.Single();
            Assert.IsTrue(freeze.DurationMs >= 450 && freeze.DurationMs < 10_000, freeze.DurationMs.ToString());
            Assert.AreEqual("simulated-block", freeze.ActiveCommand); Assert.AreEqual("corr00000001", freeze.Correlation);
            lock (seen) Assert.AreEqual(1, seen.Count);
            var row = audit.List(50).Single(a => a.Module == UiFreezeWatchdog.AuditModule && a.Action == UiFreezeWatchdog.AuditAction);
            Assert.AreEqual("WARN", row.Outcome); StringAssert.Contains(row.Detail, "simulated-block"); StringAssert.Contains(row.Detail, "corr00000001"); StringAssert.Contains(row.Detail, "ms");
            Assert.IsFalse(row.Detail.Contains("   at ", StringComparison.Ordinal), "no stack"); Assert.IsFalse(row.Detail.Contains("Thread.Sleep", StringComparison.Ordinal));

            // A second block outside any command is a second event naming idle, with its own correlation.
            dispatcher.Invoke(() => Thread.Sleep(450));
            WaitUntil(() => watchdog.Events.Count >= 2, "the second freeze event");
            Assert.AreEqual(UiActivity.Idle, watchdog.Events[1].ActiveCommand); Assert.AreNotEqual(freeze.Correlation, watchdog.Events[1].Correlation);
            Thread.Sleep(300); Assert.AreEqual(2, watchdog.Events.Count, "a freeze is recorded once");

            // The diagnostics line: count, longest, the last command; OK with none; a note when nothing is watching.
            var check = UiFreezeWatchdog.Check(watchdog.Events, 200);
            Assert.AreEqual("WARN", check.Status); StringAssert.Contains(check.Detail, "2 donma"); StringAssert.Contains(check.Detail, UiActivity.Idle);
            Assert.AreEqual("OK", UiFreezeWatchdog.Check(Array.Empty<UiFreezeEvent>(), 200).Status);
            StringAssert.Contains(UiFreezeWatchdog.Check(null, 200).Detail, "izlenmiyor");

            // Activities: the innermost is current, a delegate's name is an identifier, a name that is not one is "unnamed".
            using (UiActivity.Enter("outer")) using (UiActivity.Enter("inner", "c2")) Assert.AreEqual(("inner", "c2"), UiActivity.Current);
            Assert.AreEqual(UiActivity.Idle, UiActivity.Current.Name);
            Assert.AreEqual("Named", UiActivity.NameOf(new Func<Task>(Named)));
            Assert.IsTrue(UiActivity.NameOf(new Func<Task>(() => Task.CompletedTask)).All(c => char.IsLetterOrDigit(c) || c is '_' or '-'));
            Assert.AreEqual("unnamed", UiActivity.Safe("ali@example.com LEAKMARKER")); Assert.AreEqual("refresh-products", UiActivity.Safe("refresh-products"));
            Assert.IsFalse(UiFreezeWatchdog.Describe(new UiFreezeEvent(DateTime.UtcNow, 1500, "ali@example.com", "c1")).Contains("example.com", StringComparison.Ordinal));
        }
        finally
        {
            watchdog?.Dispose();
            dispatcher.InvokeShutdown(); thread.Join(3000);
            Cleanup(root);
        }
    }

    [TestMethod]
    public void TheApplicationEntryInstallsTheWatchdogWhoseFreezesTheDiagnosticsRead()
    {
        var root = Path.Combine(Path.GetTempPath(), "ui-freeze-app-" + Guid.NewGuid().ToString("N"));
        var (dispatcher, thread) = StartDispatcher();
        UiFreezeWatchdog watchdog = null;
        try
        {
            Directory.CreateDirectory(root);
            watchdog = App.StartWatchdog(dispatcher, root, thresholdMs: 200, intervalMs: 25);
            Assert.AreSame(watchdog, UiFreezeWatchdog.Current);
            Thread.Sleep(300);
            Assert.AreEqual("OK", new DiagnosticsService(root).Build().Checks.Single(c => c.Name == UiFreezeWatchdog.DiagnosticName).Status);
            dispatcher.Invoke(() => Thread.Sleep(600));
            WaitUntil(() => watchdog.Events.Count >= 1, "the application watchdog's event");
            var check = new DiagnosticsService(root).Build().Checks.Single(c => c.Name == UiFreezeWatchdog.DiagnosticName);
            Assert.AreEqual("WARN", check.Status); StringAssert.Contains(check.Detail, "1 donma");
            Assert.IsTrue(new AuditStore(root).List(50).Any(a => a.Action == UiFreezeWatchdog.AuditAction), "the audit row is in the data directory");
            watchdog.Dispose();
            Assert.IsNull(UiFreezeWatchdog.Current, "disposing uninstalls");
            StringAssert.Contains(new DiagnosticsService(root).Build().Checks.Single(c => c.Name == UiFreezeWatchdog.DiagnosticName).Detail, "izlenmiyor");
        }
        finally
        {
            watchdog?.Dispose();
            dispatcher.InvokeShutdown(); thread.Join(3000);
            Cleanup(root);
        }
    }

    static Task Named() => Task.CompletedTask;

    static (Dispatcher, Thread) StartDispatcher()
    {
        Dispatcher dispatcher = null; var ready = new ManualResetEventSlim(false);
        var thread = new Thread(() => { dispatcher = Dispatcher.CurrentDispatcher; ready.Set(); Dispatcher.Run(); }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); ready.Wait();
        return (dispatcher, thread);
    }

    static void WaitUntil(Func<bool> condition, string what)
    {
        for (var i = 0; i < 200; i++) { if (condition()) return; Thread.Sleep(25); }
        Assert.Fail($"Timed out waiting for {what}.");
    }

    static void Cleanup(string root)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
            catch (IOException) { Thread.Sleep(300); }
            catch (UnauthorizedAccessException) { Thread.Sleep(300); }
        }
    }
}
