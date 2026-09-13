using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #890 (DESIGN: Cancellation outcome messaging). A cancel click is a request; the words a surface shows come from
// the job's real state: the request waiting for a check, a stage that must finish its unit first, the job stopped
// and what stayed safe, or a job that had already reached its end — before the request or in spite of it.
[TestClass]
public sealed class CancellationOutcomeTests
{
    static readonly DateTime T0 = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void TheFourWordsFollowTheJobsStateNotTheClick()
    {
        Assert.AreEqual(CancellationPhase.NotRequested, CancellationOutcome.Describe(new CancellationFacts(null, false, false), "Havuz değişmedi.").Phase);
        Assert.AreEqual("", CancellationVerdict.None.Line);

        // Early or mid: the request is in and the running stage checks the token — it will stop at its next check.
        var requested = CancellationOutcome.Describe(new CancellationFacts(T0, false, false, StageCancellable: true, StageLabel: "İndir"), "Havuz değişmedi.");
        Assert.AreEqual(CancellationPhase.Requested, requested.Phase); Assert.AreEqual(CancellationOutcome.RequestedWord, requested.Headline); Assert.IsFalse(requested.IsTerminal);

        // A stage that cannot be interrupted finishes its unit first.
        var safePoint = CancellationOutcome.Describe(new CancellationFacts(T0, false, false, StageCancellable: false, StageLabel: "Oku"), "Havuz değişmedi.");
        Assert.AreEqual(CancellationPhase.StoppingAtSafePoint, safePoint.Phase); Assert.AreEqual(CancellationOutcome.SafePointWord, safePoint.Headline); StringAssert.Contains(safePoint.Detail, "Oku aşaması bölünemez");

        // Acknowledged: the job stopped; the surface says what stayed safe.
        var cancelled = CancellationOutcome.Describe(new CancellationFacts(T0, true, false), "Havuz değişmedi.");
        Assert.AreEqual(CancellationPhase.Cancelled, cancelled.Phase); Assert.AreEqual(CancellationOutcome.CancelledWord, cancelled.Headline); Assert.AreEqual("Havuz değişmedi.", cancelled.Detail); Assert.IsTrue(cancelled.IsTerminal); Assert.AreEqual(SeverityLevel.Warning, cancelled.Level);

        // Late: the job had ended before the request — the result stands.
        var late = CancellationOutcome.Describe(new CancellationFacts(T0, false, true, T0.AddSeconds(-1)), "Havuz değişmedi.");
        Assert.AreEqual(CancellationPhase.CompletedBeforeCancel, late.Phase); Assert.AreEqual(CancellationOutcome.CompletedWord, late.Headline); StringAssert.Contains(late.Detail, "bitmişti"); Assert.IsTrue(late.IsTerminal); Assert.AreEqual(SeverityLevel.Success, late.Level);

        // The race: requested, never acknowledged, and the job reached its end after the request — it completed in spite of it.
        var race = CancellationOutcome.Describe(new CancellationFacts(T0, false, true, T0.AddSeconds(2)), "Havuz değişmedi.");
        Assert.AreEqual(CancellationPhase.CompletedBeforeCancel, race.Phase); StringAssert.Contains(race.Detail, "son güvenli noktadan sonra");

        // An acknowledged stop outranks an end stamp (a cancelled job also ends).
        Assert.AreEqual(CancellationPhase.Cancelled, CancellationOutcome.Describe(new CancellationFacts(T0, true, true, T0.AddSeconds(1)), "Dosya yazılmadı.").Phase);

        // Nothing partial leaks through the guarantee sentence.
        var leaky = CancellationOutcome.Describe(new CancellationFacts(T0, true, false), "Dosya yazılmadı; token=abc123 ve ayse@example.com");
        Assert.IsFalse(leaky.Line.Contains("abc123") || leaky.Line.Contains("example.com"), leaky.Line);
    }

    [TestMethod]
    public void ASyncJobIsCancelledBeforeItLeavesAndFinishesWhatIsOnTheWire()
    {
        var pending = CancellationOutcome.ForSyncJob(SyncStatus.Pending, cancelled: true);
        Assert.AreEqual(CancellationPhase.Cancelled, pending.Phase); StringAssert.Contains(pending.Detail, "istek gitmedi");
        var running = CancellationOutcome.ForSyncJob(SyncStatus.Running, cancelled: true);
        Assert.AreEqual(CancellationPhase.StoppingAtSafePoint, running.Phase); StringAssert.Contains(running.Detail, "geri alınamaz");
        var done = CancellationOutcome.ForSyncJob(SyncStatus.Succeeded, cancelled: false);
        Assert.AreEqual(CancellationPhase.CompletedBeforeCancel, done.Phase); StringAssert.Contains(done.Detail, "bitmişti");
        Assert.AreEqual(CancellationPhase.CompletedBeforeCancel, CancellationOutcome.ForSyncJob(SyncStatus.Failed, cancelled: false).Phase);
        Assert.AreEqual(CancellationPhase.Cancelled, CancellationOutcome.ForSyncJob(SyncStatus.Cancelled, cancelled: false).Phase);
        Assert.AreEqual(CancellationPhase.Requested, CancellationOutcome.ForSyncJob(SyncStatus.Pending, cancelled: false).Phase, "a row that could not be changed asks for a refresh");
    }

    [TestMethod]
    public void TheImportAndReportProgressStatesKeepTheRequestApartFromTheAcknowledgement()
    {
        var import = new ImportProgressState();
        import.Apply(new(ImportProgressStage.Download, ImportProgressStatus.Running, AtUtc: T0));
        Assert.IsNull(import.CancelRequestedUtc); Assert.IsFalse(import.CancelAcknowledged);
        import.RequestCancel(T0.AddSeconds(1));
        Assert.AreEqual(T0.AddSeconds(1), import.CancelRequestedUtc);
        Assert.AreEqual(ImportProgressStatus.Running, import[ImportProgressStage.Download].Status, "a request does not rewrite the stage; the pipeline answers it");
        var facts = import.CancellationFacts(T0.AddSeconds(2));
        Assert.AreEqual(CancellationPhase.Requested, CancellationOutcome.Describe(facts, "Havuz değişmedi.").Phase);
        Assert.IsTrue(ImportProgressState.IsCancellable(ImportProgressStage.Download)); Assert.IsFalse(ImportProgressState.IsCancellable(ImportProgressStage.Read), "a parse cannot be interrupted"); Assert.IsTrue(ImportProgressState.IsCancellable(ImportProgressStage.Apply), "the apply is one transaction that rolls back");
        import.Apply(new(ImportProgressStage.Download, ImportProgressStatus.Done, AtUtc: T0.AddSeconds(2))); import.Apply(new(ImportProgressStage.Read, ImportProgressStatus.Running, AtUtc: T0.AddSeconds(2)));
        Assert.AreEqual(CancellationPhase.StoppingAtSafePoint, CancellationOutcome.Describe(import.CancellationFacts(T0.AddSeconds(3)), "Havuz değişmedi.").Phase);
        import.Apply(new(ImportProgressStage.Read, ImportProgressStatus.Cancelled, AtUtc: T0.AddSeconds(4)));
        Assert.IsTrue(import.CancelAcknowledged);
        Assert.AreEqual(CancellationPhase.Cancelled, CancellationOutcome.Describe(import.CancellationFacts(T0.AddSeconds(5)), "Havuz değişmedi.").Phase);
        // Retry clears the request with the stages it resets.
        import.Retry(); Assert.IsNull(import.CancelRequestedUtc); Assert.IsFalse(import.CancelAcknowledged);
        // The race: every stage done after the request.
        var done = new ImportProgressState();
        foreach (var stage in ImportProgressState.Stages) { done.Apply(new(stage.Stage, ImportProgressStatus.Running, AtUtc: T0)); done.Apply(new(stage.Stage, ImportProgressStatus.Done, AtUtc: T0.AddSeconds(1))); }
        done.RequestCancel(T0.AddMilliseconds(500));
        var verdict = CancellationOutcome.Describe(done.CancellationFacts(T0.AddSeconds(2)), "Havuz değişmedi.");
        Assert.AreEqual(CancellationPhase.CompletedBeforeCancel, verdict.Phase); StringAssert.Contains(verdict.Detail, "son güvenli noktadan sonra");

        var report = new ReportRunProgressState();
        report.Apply(new(ReportRunStage.Query, ReportRunStageStatus.Running, AtUtc: T0));
        report.RequestCancel(T0.AddSeconds(1));
        Assert.AreEqual(ReportRunStageStatus.Running, report[ReportRunStage.Query].Status);
        Assert.AreEqual(CancellationPhase.Requested, CancellationOutcome.Describe(report.CancellationFacts(T0.AddSeconds(1)), "Dosya yazılmadı.").Phase);
        Assert.IsTrue(ReportRunProgressState.IsCancellable(ReportRunStage.Query)); Assert.IsFalse(ReportRunProgressState.IsCancellable(ReportRunStage.Generate)); Assert.IsTrue(ReportRunProgressState.IsCancellable(ReportRunStage.Export));
        report.Apply(new(ReportRunStage.Query, ReportRunStageStatus.Cancelled, AtUtc: T0.AddSeconds(2)));
        Assert.IsTrue(report.CancelAcknowledged);
        Assert.AreEqual(CancellationPhase.Cancelled, CancellationOutcome.Describe(report.CancellationFacts(T0.AddSeconds(3)), "Dosya yazılmadı.").Phase);
        var finished = new ReportRunProgressState();
        foreach (var stage in ReportRunProgressState.Stages) { finished.Apply(new(stage.Stage, ReportRunStageStatus.Running, AtUtc: T0)); finished.Apply(new(stage.Stage, ReportRunStageStatus.Done, AtUtc: T0.AddSeconds(1))); }
        finished.RequestCancel(T0.AddSeconds(5));
        Assert.AreEqual(CancellationPhase.CompletedBeforeCancel, CancellationOutcome.Describe(finished.CancellationFacts(T0.AddSeconds(6)), "Dosya yazılmadı.").Phase);
    }
}
