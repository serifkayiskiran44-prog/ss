using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #848 (DESIGN: Report run progress surface). The stage model: an unknown total is a count with an indeterminate
// bar, never a percentage; a known total is a percentage; cancellation lands on the running stage; a failure keeps
// its redacted note as the diagnostics; the headline follows. The runner emits real events -- query (total
// unknown until the first page, then page by page) and generate on a query, export on an export -- and on cancel
// or failure marks the stage it was in. Neither the run note nor the audit detail ever carries the query text.
[TestClass]
public sealed class ReportRunProgressTests
{
    sealed class Sink : IProgress<ReportRunProgressEvent> { public readonly List<ReportRunProgressEvent> Events = new(); public void Report(ReportRunProgressEvent value) => Events.Add(value); }

    [TestMethod]
    public void StagesCountShowPercentOnlyWithATotalCancelOnTheRunningStageAndKeepFailureDiagnostics()
    {
        var t0 = new DateTime(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc); var state = new ReportRunProgressState();
        Assert.AreEqual("Bekliyor", state.Headline(t0)); Assert.IsFalse(state.IsTerminal); Assert.AreEqual("", state.Diagnostics);
        state.Apply(new(ReportRunStage.Query, ReportRunStageStatus.Running, AtUtc: t0));
        var query = state[ReportRunStage.Query];
        Assert.IsTrue(query.IsIndeterminate); Assert.IsNull(query.Percent); Assert.AreEqual("", query.Counter); Assert.AreEqual("Sorgu sürüyor", state.Headline(t0.AddSeconds(2)));
        state.Apply(new(ReportRunStage.Query, ReportRunStageStatus.Running, 1000, 4000, t0.AddSeconds(3)));
        query = state.Snapshot(t0.AddSeconds(5)).Single(s => s.Stage == ReportRunStage.Query);
        Assert.IsFalse(query.IsIndeterminate); Assert.AreEqual(25d, query.Percent); Assert.AreEqual($"{1000:N0} / {4000:N0}", query.Counter); Assert.AreEqual(TimeSpan.FromSeconds(5), query.Elapsed);
        StringAssert.StartsWith(state.Headline(t0.AddSeconds(5)), "Sorgu sürüyor · "); StringAssert.EndsWith(state.Headline(t0.AddSeconds(5)), "(%25)");
        state.Apply(new(ReportRunStage.Query, ReportRunStageStatus.Done, 4000, 4000, t0.AddSeconds(8)));
        Assert.AreEqual(100d, state[ReportRunStage.Query].Percent); Assert.AreEqual(TimeSpan.FromSeconds(8), state[ReportRunStage.Query].Elapsed);
        state.Apply(new(ReportRunStage.Generate, ReportRunStageStatus.Running, 0, 4000, t0.AddSeconds(8)));
        state.Cancel(t0.AddSeconds(9));
        Assert.AreEqual(ReportRunStage.Generate, state.Failed); Assert.IsTrue(state.IsCancelled && state.IsTerminal && !state.IsComplete);
        Assert.AreEqual(ReportRunStageStatus.Pending, state[ReportRunStage.Export].Status, "Later stages stay pending."); Assert.AreEqual(ReportRunStageStatus.Done, state[ReportRunStage.Query].Status, "Earlier stages keep what they did.");
        Assert.AreEqual("Üretim iptal", state.Headline(t0.AddSeconds(9))); Assert.AreEqual("İptal edildi; dosya yazılmadı.", state.Diagnostics);

        var listed = new ReportRunProgressState();
        listed.Apply(new(ReportRunStage.Query, ReportRunStageStatus.Running, AtUtc: t0)); listed.Apply(new(ReportRunStage.Query, ReportRunStageStatus.Done, 3, 3, t0)); listed.Apply(new(ReportRunStage.Generate, ReportRunStageStatus.Running, AtUtc: t0)); listed.Apply(new(ReportRunStage.Generate, ReportRunStageStatus.Done, 3, 3, t0));
        Assert.IsFalse(listed.IsTerminal); StringAssert.StartsWith(listed.Headline(t0), "Sonuç hazır", "A listed result with the export still optional (#849).");

        var failed = listed;
        failed.Apply(new(ReportRunStage.Export, ReportRunStageStatus.Running, AtUtc: t0));
        failed.Apply(new(ReportRunStage.Export, ReportRunStageStatus.Failed, AtUtc: t0.AddSeconds(1), Note: "Disk dolu; token=abc123 gizli"));
        Assert.AreEqual(ReportRunStage.Export, failed.Failed); Assert.IsFalse(failed.Diagnostics.Contains("abc123")); StringAssert.StartsWith(failed.Diagnostics, "Disk dolu"); StringAssert.StartsWith(failed.Headline(t0), "Dışa aktarma başarısız: Disk dolu");
        Assert.AreEqual("✖", ReportRunProgressState.Glyph(ReportRunStageStatus.Failed)); Assert.AreEqual("iptal", ReportRunProgressState.StatusWord(ReportRunStageStatus.Cancelled));

        var restarted = failed; restarted.Apply(new(ReportRunStage.Query, ReportRunStageStatus.Running, AtUtc: t0.AddMinutes(1)));
        Assert.IsNull(restarted.Failed); Assert.AreEqual(ReportRunStageStatus.Pending, restarted[ReportRunStage.Export].Status, "Starting the first stage again resets everything after it.");
        var complete = new ReportRunProgressState(); foreach (var s in ReportRunProgressState.Stages) { complete.Apply(new(s.Stage, ReportRunStageStatus.Running, AtUtc: t0)); complete.Apply(new(s.Stage, ReportRunStageStatus.Done, 1, 1, t0)); }
        Assert.IsTrue(complete.IsComplete); Assert.AreEqual("Tamamlandı", complete.Headline(t0));
    }

    [TestMethod]
    public async Task TheRunnerEmitsRealStageEventsMarksCancelOrFailureAtItsStageAndNeverLogsTheQuery()
    {
        var root = Path.Combine(Path.GetTempPath(), "report-progress-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var orders = new OrdersStore(root); var now = DateTimeOffset.UtcNow;
            for (var i = 0; i < 3; i++) orders.SaveManual(new() { Marketplace = "etsy", ShopId = "S1", OrderId = $"10{i}", UpdatedAt = now.AddDays(-i), Total = 5m, Currency = "USD", Items = [new() { Sku = "A", Title = "Kupa", Quantity = 1 }], Shipments = [new() { Id = "P" + i, Carrier = "Aras", TrackingNumber = "T" + i, State = "InTransit" }] });
            var def = ReportCatalog.Find("orders-csv")!; var allowed = new[] { "etsy|S1" }; var columns = ReportColumns.Default(ReportColumns.OrdersSchema).VisibleKeys;
            var p = ReportParameters.Defaults(def, allowed, DateTime.UtcNow) with { Query = "musteri@example.com" };
            var path = Path.Combine(root, "orders.csv"); var sink = new Sink();

            var outcome = await ReportRunner.QueryAsync(root, def, p, allowed, CancellationToken.None, sink);
            Assert.AreEqual(ReportRunState.Succeeded, outcome.State); Assert.AreEqual(0, outcome.Result!.Rows.Count, "The query text matches no order.");
            var events = sink.Events;
            Assert.AreEqual((ReportRunStage.Query, ReportRunStageStatus.Running, (long?)null), (events[0].Stage, events[0].Status, events[0].Total), "The first query event knows no total.");
            Assert.IsTrue(events.Any(e => e.Stage == ReportRunStage.Query && e.Status == ReportRunStageStatus.Running && e.Total is not null), "Page by page the total becomes known.");
            CollectionAssert.AreEqual(new[] { ReportRunStage.Query, ReportRunStage.Generate, ReportRunStage.Generate }, events.Where(e => e.Status == ReportRunStageStatus.Done || e.Stage != ReportRunStage.Query).Select(e => e.Stage).ToArray());
            Assert.AreEqual((ReportRunStage.Generate, ReportRunStageStatus.Done), (events.Last().Stage, events.Last().Status));
            var replay = new ReportRunProgressState(); foreach (var e in events) replay.Apply(e); StringAssert.StartsWith(replay.Headline(DateTime.UtcNow), "Sonuç hazır");

            var run = new ReportRunStore(root).Latest()["orders-csv"];
            Assert.IsFalse(run.Note.Contains("example.com"), run.Note); StringAssert.Contains(run.Note, "Arama: var");
            var audit = new AuditStore(root).List(10).Single(a => a.Module == "reports");
            Assert.AreEqual("run:orders-csv", audit.Action); Assert.AreEqual("Succeeded", audit.Outcome); Assert.AreEqual("etsy", audit.Marketplace); Assert.AreEqual("S1", audit.ShopId);
            Assert.IsFalse(audit.Detail.Contains("example.com"), audit.Detail); StringAssert.Contains(audit.Detail, "sipariş yok");

            // The export stage, on the rows the query lifted the search from.
            var full = new Sink(); var withRows = await ReportRunner.QueryAsync(root, def, p with { Query = "" }, allowed, CancellationToken.None, full);
            Assert.AreEqual(3, withRows.Result!.Rows.Count); Assert.AreEqual(3, full.Events.Single(e => e.Stage == ReportRunStage.Query && e.Status == ReportRunStageStatus.Done).Total);
            Assert.AreEqual(3, full.Events.Single(e => e.Stage == ReportRunStage.Query && e.Status == ReportRunStageStatus.Running && e.Total is not null).Total);
            var exportSink = new Sink(); var exported = await ReportRunner.ExportAsync(root, withRows.Result, columns, path, CancellationToken.None, exportSink);
            Assert.AreEqual(ReportRunState.Succeeded, exported.State);
            CollectionAssert.AreEqual(new[] { ReportRunStageStatus.Running, ReportRunStageStatus.Done }, exportSink.Events.Select(e => e.Status).ToArray()); Assert.IsTrue(exportSink.Events.All(e => e.Stage == ReportRunStage.Export));
            foreach (var e in exportSink.Events) replay.Apply(e);
            Assert.IsTrue(replay.IsComplete, "A query's events followed by an export's events compose one complete run, as the setup shows them."); Assert.AreEqual("Tamamlandı", replay.Headline(DateTime.UtcNow));

            // Cancelled before the query: the query stage is the one marked cancelled.
            using var cts = new CancellationTokenSource(); cts.Cancel(); var cancelledSink = new Sink();
            var cancelled = await ReportRunner.QueryAsync(root, def, p, allowed, cts.Token, cancelledSink);
            Assert.AreEqual(ReportRunState.Cancelled, cancelled.State); Assert.AreEqual((ReportRunStage.Query, ReportRunStageStatus.Cancelled), (cancelledSink.Events.Last().Stage, cancelledSink.Events.Last().Status));
            Assert.AreEqual("Cancelled", new AuditStore(root).List(10).First(a => a.Module == "reports").Outcome);

            // A failed export marks the export stage with a sanitized note.
            var blocked = Path.Combine(root, "dir.csv"); Directory.CreateDirectory(blocked); var failedSink = new Sink();
            var failed = await ReportRunner.ExportAsync(root, withRows.Result, columns, blocked, CancellationToken.None, failedSink);
            Assert.AreEqual(ReportRunState.Failed, failed.State); var last = failedSink.Events.Last();
            Assert.AreEqual((ReportRunStage.Export, ReportRunStageStatus.Failed), (last.Stage, last.Status)); Assert.IsTrue(last.Note.Length > 0); Assert.IsFalse(last.Note.Contains(root), "The note never carries the path.");
        }
        finally
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
                catch (IOException) { Thread.Sleep(200); }
                catch (UnauthorizedAccessException) { Thread.Sleep(200); }
            }
        }
    }
}
