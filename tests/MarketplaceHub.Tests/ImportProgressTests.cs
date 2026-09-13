using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #827 (DESIGN: Import progress stage breakdown). Stages advance only on real events; a known total gives a
// percentage and an unknown one an indeterminate bar with a count; fast and slow stages are recorded alike;
// cancel lands on the running stage; retry keeps what finished; notes never carry the source address.
[TestClass]
public sealed class ImportProgressTests
{
    static readonly DateTime T0 = new(2026, 9, 13, 10, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void AKnownTotalGivesAPercentageAndAnUnknownOneGivesACountAndAnIndeterminateBar()
    {
        var state = new ImportProgressState();
        state.Apply(new(ImportProgressStage.Download, ImportProgressStatus.Running, Done: 0, Total: null, AtUtc: T0));
        state.Apply(new(ImportProgressStage.Download, ImportProgressStatus.Running, Done: 48_000, Total: null, AtUtc: T0.AddSeconds(2)));

        var download = state.Snapshot(T0.AddSeconds(3)).Single(s => s.Stage == ImportProgressStage.Download);
        Assert.IsNull(download.Percent, "No total, no percentage -- nothing is estimated.");
        Assert.IsTrue(download.IsIndeterminate);
        Assert.AreEqual(48_000.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), download.Counter);
        Assert.AreEqual(TimeSpan.FromSeconds(3), download.Elapsed, "Elapsed is measured while it runs.");

        state.Apply(new(ImportProgressStage.Apply, ImportProgressStatus.Running, Done: 250, Total: 1_000, AtUtc: T0));
        var apply = state[ImportProgressStage.Apply];
        Assert.AreEqual(25d, apply.Percent);
        Assert.IsFalse(apply.IsIndeterminate);
        StringAssert.Contains(apply.Counter, "/");
    }

    [TestMethod]
    public void AFastStageAndASlowStageAreRecordedAlikeAndDoneFillsTheTotal()
    {
        var state = new ImportProgressState();
        state.Apply(new(ImportProgressStage.Parse, ImportProgressStatus.Running, AtUtc: T0));
        state.Apply(new(ImportProgressStage.Parse, ImportProgressStatus.Done, Total: 12, AtUtc: T0.AddMilliseconds(80)));
        state.Apply(new(ImportProgressStage.Preview, ImportProgressStatus.Running, Done: 0, Total: 12, AtUtc: T0.AddSeconds(1)));
        state.Apply(new(ImportProgressStage.Preview, ImportProgressStatus.Done, Total: 12, AtUtc: T0.AddSeconds(61)));

        var parse = state[ImportProgressStage.Parse];
        Assert.AreEqual(ImportProgressStatus.Done, parse.Status);
        Assert.AreEqual(TimeSpan.FromMilliseconds(80), parse.Elapsed, "Eighty milliseconds is still a stage.");
        Assert.AreEqual(12, parse.Done); Assert.AreEqual(100d, parse.Percent, "Done means all of the total.");
        Assert.AreEqual(TimeSpan.FromSeconds(60), state[ImportProgressStage.Preview].Elapsed);
    }

    [TestMethod]
    public void CancellationLandsOnTheRunningStageAndLeavesLaterStagesPending()
    {
        var state = new ImportProgressState();
        state.Apply(new(ImportProgressStage.Download, ImportProgressStatus.Done, AtUtc: T0));
        state.Apply(new(ImportProgressStage.Apply, ImportProgressStatus.Running, Done: 10, Total: 100, AtUtc: T0));

        state.Cancel(T0.AddSeconds(4));

        Assert.AreEqual(ImportProgressStatus.Cancelled, state[ImportProgressStage.Apply].Status);
        Assert.AreEqual(TimeSpan.FromSeconds(4), state[ImportProgressStage.Apply].Elapsed);
        Assert.AreEqual("İptal edildi", state[ImportProgressStage.Apply].Note);
        Assert.AreEqual(ImportProgressStatus.Done, state[ImportProgressStage.Download].Status, "What finished stays finished.");
        Assert.IsNull(state.Running);
        Assert.AreEqual(ImportProgressStage.Apply, state.Failed);
        state.Cancel(T0.AddSeconds(5));
        Assert.AreEqual(ImportProgressStatus.Cancelled, state[ImportProgressStage.Apply].Status, "Cancelling with nothing running changes nothing.");
    }

    [TestMethod]
    public void RetryResetsTheFailedStageAndEverythingAfterItKeepingWhatFinishedBefore()
    {
        var state = new ImportProgressState();
        foreach (var stage in new[] { ImportProgressStage.Download, ImportProgressStage.Read, ImportProgressStage.Parse })
            state.Apply(new(stage, ImportProgressStatus.Done, AtUtc: T0));
        state.Apply(new(ImportProgressStage.Validate, ImportProgressStatus.Running, AtUtc: T0));
        state.Apply(new(ImportProgressStage.Validate, ImportProgressStatus.Failed, AtUtc: T0.AddSeconds(1), Note: "Zorunlu XML alanları bulunamadı: Sku"));
        Assert.AreEqual(ImportProgressStatus.Pending, state[ImportProgressStage.Preview].Status);

        var restart = state.Retry();

        Assert.AreEqual(ImportProgressStage.Validate, restart, "Retry starts where it failed.");
        Assert.AreEqual(ImportProgressStatus.Pending, state[ImportProgressStage.Validate].Status);
        Assert.AreEqual("", state[ImportProgressStage.Validate].Note);
        Assert.AreEqual(ImportProgressStatus.Done, state[ImportProgressStage.Parse].Status, "The parse before it is kept.");
        Assert.IsNull(state.Retry(), "Nothing failed, nothing to retry.");

        // Starting an earlier stage again resets everything after it: a re-read invalidates preview and apply.
        state.Apply(new(ImportProgressStage.Apply, ImportProgressStatus.Done, AtUtc: T0));
        state.Apply(new(ImportProgressStage.Read, ImportProgressStatus.Running, AtUtc: T0.AddSeconds(2)));
        Assert.AreEqual(ImportProgressStatus.Pending, state[ImportProgressStage.Apply].Status);
        Assert.IsFalse(state.IsComplete);
    }

    [TestMethod]
    public void NotesAreRedactedAndTheStageTableIsCompleteAndOrdered()
    {
        var state = new ImportProgressState();
        state.Apply(new(ImportProgressStage.Download, ImportProgressStatus.Failed, AtUtc: T0, Note: "https://ali:gizli@supplier.example.com/feed.xml?token=ABC reddetti"));

        var note = state[ImportProgressStage.Download].Note;
        Assert.IsFalse(note.Contains("gizli") || note.Contains("ABC"), "A note never quotes a credential: " + note);
        CollectionAssert.AreEqual(Enum.GetValues<ImportProgressStage>(), ImportProgressState.Stages.Select(s => s.Stage).ToArray(), "Six stages, in pipeline order.");
        StringAssert.Contains(ImportProgressState.Stages.Last().Label, "yerel", "Apply writes the local pool and says so.");
        Assert.AreEqual("iptal", ImportProgressState.StatusWord(ImportProgressStatus.Cancelled));
        Assert.AreEqual(6, state.Snapshot(T0).Count);
    }
}
