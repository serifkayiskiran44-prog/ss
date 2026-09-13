using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #878 (PERFORMANCE: UI render latency instrumentation). A view load or refresh is measured at its boundary and
// recorded as a structured metric — view, phase, outcome, duration, count, scope id, cold/warm, correlation — and
// never entity content: a scope that is not an id is recorded as "invalid-scope", a view that is not a fixed
// identifier is refused; cold is the first verdict of a view in the session, warm the next ones; cancellation,
// failure and a superseded request are outcomes of their own; views measured in parallel keep separate clocks and
// correlations; the first verdict wins; the store keeps a retention limit and summarises by view and phase; the
// diagnostics check and the support package carry the summary; a store that cannot take a row never throws.
[TestClass]
public sealed class UiLatencyTests
{
    [TestMethod]
    public void ViewLatencyIsRecordedAsPiiSafeMetricsWithOutcomesSummariesAndDiagnostics()
    {
        var root = Path.Combine(Path.GetTempPath(), "ui-latency-" + Guid.NewGuid().ToString("N"));
        var file = Path.Combine(root, LatencyStore.FileName);
        FileAttributes? attributes = null;
        try
        {
            Directory.CreateDirectory(root);
            var store = new LatencyStore(root);

            // Cold, then warm; the duration is the boundary's own clock; the count is what the view showed.
            var cold = UiLatency.Begin(store, UiLatency.ProductsView, LatencyPhase.Load, UiLatency.Unscoped); Thread.Sleep(15);
            var first = cold.Complete(200);
            Assert.IsFalse(first.Warm); Assert.IsTrue(first.DurationMs >= 10, first.DurationMs.ToString()); Assert.AreEqual(200, first.Count); Assert.AreEqual(LatencyOutcome.Completed, first.Outcome); Assert.AreEqual(LatencyPhase.Load, first.Phase);
            var second = UiLatency.Begin(store, UiLatency.ProductsView, LatencyPhase.Refresh, UiLatency.Unscoped).Complete(50);
            Assert.IsTrue(second.Warm); Assert.AreEqual(LatencyPhase.Refresh, second.Phase);
            Assert.IsFalse(UiLatency.Begin(store, UiLatency.OrdersView, LatencyPhase.Load, UiLatency.Unscoped).Complete(3).Warm, "another view is cold on its own first load");

            // Cancellation, failure and a superseded request are outcomes; the first verdict wins.
            Assert.AreEqual(LatencyOutcome.Cancelled, UiLatency.Begin(store, UiLatency.ImportView, LatencyPhase.Load, "feed-1").Cancel().Outcome);
            Assert.AreEqual(LatencyOutcome.Failed, UiLatency.Begin(store, UiLatency.DashboardView, LatencyPhase.Load, UiLatency.Unscoped).Fail().Outcome);
            Assert.AreEqual(LatencyOutcome.Superseded, UiLatency.Begin(store, UiLatency.ProductsView, LatencyPhase.Refresh, UiLatency.Unscoped).Supersede().Outcome);
            var settled = UiLatency.Begin(store, UiLatency.OrdersView, LatencyPhase.Refresh, UiLatency.Unscoped);
            var verdict = settled.Complete(7); var again = settled.Fail();
            Assert.AreSame(verdict, again); Assert.AreEqual(LatencyOutcome.Completed, again.Outcome);
            Assert.AreEqual(1, store.List().Count(r => r.Correlation == verdict.Correlation), "one row per scope");

            // Parallel views: two measurements in flight keep their own clocks and correlations.
            var a = UiLatency.Begin(store, UiLatency.ProductsView, LatencyPhase.Refresh, UiLatency.Unscoped);
            var b = UiLatency.Begin(store, UiLatency.OrdersView, LatencyPhase.Refresh, UiLatency.Unscoped);
            Thread.Sleep(20); var mb = b.Complete(3); Thread.Sleep(40); var ma = a.Complete(4);
            Assert.IsTrue(ma.DurationMs >= mb.DurationMs + 30, $"{ma.DurationMs} vs {mb.DurationMs}"); Assert.AreNotEqual(ma.Correlation, mb.Correlation);

            // Scope ids only: a store key or a source id passes, anything else is recorded as invalid-scope, never as its text; a view that is not an identifier is refused; a negative count is 0.
            Assert.AreEqual("trendyol:12345", UiLatency.Begin(store, UiLatency.OrdersView, LatencyPhase.Load, "trendyol:12345").Complete(1).Scope);
            Assert.AreEqual(UiLatency.InvalidScope, UiLatency.Begin(store, UiLatency.OrdersView, LatencyPhase.Load, "ali@example.com").Complete(1).Scope);
            Assert.AreEqual(UiLatency.InvalidScope, UiLatency.Begin(store, UiLatency.OrdersView, LatencyPhase.Load, "Kırmızı Elbise LEAKMARKER").Complete(1).Scope);
            Assert.AreEqual(UiLatency.Unscoped, UiLatency.Begin(store, UiLatency.OrdersView, LatencyPhase.Load, "").Complete(1).Scope);
            Assert.ThrowsException<ArgumentException>(() => UiLatency.Begin(store, "Ürün <b>", LatencyPhase.Load, UiLatency.Unscoped));
            Assert.AreEqual(0, UiLatency.Begin(store, UiLatency.OrdersView, LatencyPhase.Load, UiLatency.Unscoped).Complete(-4).Count);
            var rows = store.List();
            Assert.IsTrue(rows.Count >= 12); Assert.IsFalse(rows.Any(r => r.Scope.Contains('@') || r.Scope.Contains("LEAKMARKER", StringComparison.Ordinal)));
            Assert.AreEqual(rows.Max(r => r.AtUtc), rows[0].AtUtc, "newest first");
            Assert.AreEqual(2, store.List(view: UiLatency.ImportView).Count + store.List(view: UiLatency.DashboardView).Count);

            // The summary by view and phase: nearest-rank median and p95 over completed durations, failures and cancellations counted.
            var summaryStore = new LatencyStore(Path.Combine(root, "summary"));
            foreach (var ms in new long[] { 30, 10, 50, 20, 40 }) summaryStore.Record(new LatencyMetric(DateTime.UtcNow, UiLatency.OrdersView, LatencyPhase.Load, LatencyOutcome.Completed, ms, 1, UiLatency.Unscoped, false, Guid.NewGuid().ToString("N")[..12]));
            summaryStore.Record(new LatencyMetric(DateTime.UtcNow, UiLatency.OrdersView, LatencyPhase.Load, LatencyOutcome.Failed, 999, 0, UiLatency.Unscoped, true, "fail00000001"));
            summaryStore.Record(new LatencyMetric(DateTime.UtcNow, UiLatency.OrdersView, LatencyPhase.Load, LatencyOutcome.Cancelled, 999, 0, UiLatency.Unscoped, true, "cancel000001"));
            var orders = summaryStore.Summary().Single();
            Assert.AreEqual(5, orders.Samples); Assert.AreEqual(30, orders.MedianMs); Assert.AreEqual(50, orders.P95Ms); Assert.AreEqual(50, orders.MaxMs); Assert.AreEqual(1, orders.Failed); Assert.AreEqual(1, orders.Cancelled);
            Assert.AreEqual(0, LatencyStore.Percentile(Array.Empty<long>(), 0.95));

            // Retention: the store keeps the newest rows only.
            var small = new LatencyStore(Path.Combine(root, "small"), retention: 20);
            for (var i = 0; i < 25; i++) small.Record(new LatencyMetric(DateTime.UtcNow, UiLatency.ProductsView, LatencyPhase.Refresh, LatencyOutcome.Completed, i, i, UiLatency.Unscoped, true, $"row{i:D9}"));
            var kept = small.List(100); Assert.AreEqual(20, kept.Count); Assert.AreEqual(24, kept[0].DurationMs); Assert.AreEqual(5, kept[^1].DurationMs);

            // The diagnostics check and the support package carry the summary and nothing else; a slow p95 is a WARN.
            var check = new DiagnosticsService(root).Build().Checks.Single(c => c.Name == UiLatency.DiagnosticName);
            Assert.AreEqual("OK", check.Status); StringAssert.Contains(check.Detail, UiLatency.ProductsView); StringAssert.Contains(check.Detail, "p95"); Assert.IsFalse(check.Detail.Contains("LEAKMARKER", StringComparison.Ordinal));
            Assert.AreEqual("OK", UiLatency.Check(Array.Empty<LatencySummary>()).Status);
            store.Record(new LatencyMetric(DateTime.UtcNow, UiLatency.DashboardView, LatencyPhase.Refresh, LatencyOutcome.Completed, UiLatency.SlowThresholdMs + 1, 9, UiLatency.Unscoped, true, "slow00000001"));
            Assert.AreEqual("WARN", new DiagnosticsService(root).Build().Checks.Single(c => c.Name == UiLatency.DiagnosticName).Status);
            var export = Path.Combine(root, "support.zip"); SupportPackageService.Export(export, root);
            using (var archive = ZipFile.OpenRead(export))
            {
                var entry = archive.GetEntry("ui-latency.json"); Assert.IsNotNull(entry, "the package carries the latency metrics");
                using var reader = new StreamReader(entry.Open()); var json = reader.ReadToEnd();
                StringAssert.Contains(json, UiLatency.ProductsView); StringAssert.Contains(json, "DurationMs"); Assert.IsFalse(json.Contains("LEAKMARKER", StringComparison.Ordinal)); Assert.IsFalse(json.Contains("example.com", StringComparison.Ordinal));
            }

            // A store that cannot take the row: the verdict still comes back and nothing throws into the screen.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            attributes = File.GetAttributes(file); File.SetAttributes(file, attributes.Value | FileAttributes.ReadOnly);
            var blocked = UiLatency.Begin(store, UiLatency.ProductsView, LatencyPhase.Refresh, UiLatency.Unscoped).Complete(1);
            Assert.AreEqual(LatencyOutcome.Completed, blocked.Outcome);
            File.SetAttributes(file, attributes.Value); attributes = null;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
        finally
        {
            try { if (attributes is not null && File.Exists(file)) File.SetAttributes(file, attributes.Value); } catch (IOException) { }
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
                catch (IOException) { Thread.Sleep(300); }
                catch (UnauthorizedAccessException) { Thread.Sleep(300); }
            }
        }
    }
}
