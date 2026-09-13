using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #822 (DESIGN: Import workflow stepper). Six stages derived from the flow's real state: forward only when the
// state behind a stage is real, back any time, a stale preview blocks apply, cancel and restart are honest, and no
// stage is a live marketplace write.
[TestClass]
public sealed class ImportStepperTests
{
    static ImportFlowState Fresh() => new() { HasSource = true, SourceSaved = true, XmlLoaded = true, MappingReady = true, PreviewExists = true, PreviewFresh = true };

    static ImportStep At(ImportFlowState s, ImportStage stage) => ImportStepper.Compute(s).Single(x => x.Stage == stage);

    [TestMethod]
    public void ForwardIsAllowedOnlyWhenTheStateBehindAStageIsReal()
    {
        var noSource = new ImportFlowState();
        Assert.AreEqual(ImportStage.Source, ImportStepper.Current(noSource));
        Assert.IsFalse(At(noSource, ImportStage.Mapping).CanJump, "No source, no mapping to jump to.");
        Assert.IsFalse(At(noSource, ImportStage.Preview).CanJump);

        var unread = new ImportFlowState { HasSource = true };
        Assert.AreEqual(ImportStage.Mapping, ImportStepper.Current(unread));
        Assert.AreEqual(ImportStageStatus.Blocked, At(unread, ImportStage.Mapping).Status, "Mapping cannot be verified before the XML is read.");
        Assert.IsFalse(At(unread, ImportStage.Preview).CanJump, "No read, no preview.");

        var readAndMapped = new ImportFlowState { HasSource = true, XmlLoaded = true, MappingReady = true };
        Assert.AreEqual(ImportStage.Preview, ImportStepper.Current(readAndMapped));
        Assert.IsTrue(At(readAndMapped, ImportStage.Preview).CanJump);
        Assert.IsFalse(At(readAndMapped, ImportStage.Apply).CanJump, "Apply needs a preview first.");
    }

    [TestMethod]
    public void BackIsAlwaysAllowedAndEarlierStagesReadAsDone()
    {
        var steps = ImportStepper.Compute(Fresh());

        Assert.AreEqual(ImportStage.Apply, ImportStepper.Current(Fresh()));
        foreach (var stage in new[] { ImportStage.Source, ImportStage.Mapping, ImportStage.Preview })
        {
            Assert.IsTrue(steps.Single(x => x.Stage == stage).CanJump, $"{stage}: going back is always possible.");
            Assert.AreEqual(ImportStageStatus.Done, steps.Single(x => x.Stage == stage).Status, $"{stage} is behind the current stage.");
        }
        Assert.AreEqual(ImportStageStatus.Current, steps.Single(x => x.Stage == ImportStage.Apply).Status);
        Assert.AreEqual(ImportStageStatus.Pending, steps.Single(x => x.Stage == ImportStage.Result).Status);
    }

    [TestMethod]
    public void AValidationFailureBlocksTheStagesAfterItAndSaysWhy()
    {
        var bad = new ImportFlowState { HasSource = true, XmlLoaded = true, MappingReady = false, MappingProblem = "SKU alanı eşlenmedi." };

        Assert.AreEqual(ImportStage.Validation, ImportStepper.Current(bad));
        var validation = At(bad, ImportStage.Validation);
        Assert.AreEqual(ImportStageStatus.Blocked, validation.Status);
        StringAssert.Contains(validation.Reason, "SKU alanı eşlenmedi.");
        Assert.AreEqual(ImportStageStatus.Pending, At(bad, ImportStage.Preview).Status);
        Assert.IsFalse(At(bad, ImportStage.Preview).CanJump, "A failed mapping does not let you preview past it.");
        Assert.IsFalse(At(bad, ImportStage.Apply).CanJump);
    }

    [TestMethod]
    public void AStalePreviewIsShownAsStaleAndBlocksApply()
    {
        var stale = Fresh() with { PreviewFresh = false };

        var preview = At(stale, ImportStage.Preview);
        Assert.AreEqual(ImportStageStatus.Stale, preview.Status);
        StringAssert.Contains(preview.Reason, "yeniden önizleyin");
        Assert.IsTrue(preview.CanJump, "Re-previewing is the way forward.");
        Assert.AreEqual(ImportStageStatus.Pending, At(stale, ImportStage.Apply).Status, "Apply waits for a fresh preview -- the same rule the import command enforces.");
        Assert.IsFalse(At(stale, ImportStage.Apply).CanJump);
        Assert.AreEqual(ImportStage.Preview, ImportStepper.Current(stale));
    }

    [TestMethod]
    public void RunningCancelledAndFailedAppliesAreHonestAndRestartGoesBackToTheRead()
    {
        var running = Fresh() with { ApplyRunning = true };
        Assert.AreEqual(ImportStageStatus.Running, At(running, ImportStage.Apply).Status);
        Assert.IsFalse(At(running, ImportStage.Apply).CanJump);

        var cancelled = Fresh() with { ApplyCancelled = true, ResultStatus = "İptal edildi" };
        Assert.AreEqual(ImportStageStatus.Blocked, At(cancelled, ImportStage.Result).Status);
        StringAssert.Contains(At(cancelled, ImportStage.Result).Reason, "iptal");

        var failed = Fresh() with { ResultFailed = true, ResultStatus = "Dosya okunamadı" };
        Assert.AreEqual(ImportStageStatus.Blocked, At(failed, ImportStage.Result).Status);
        StringAssert.Contains(At(failed, ImportStage.Result).Reason, "Dosya okunamadı");

        var done = Fresh() with { ResultStatus = "12 yeni / 3 güncel / 0 aynı" };
        Assert.AreEqual(ImportStage.Result, ImportStepper.Current(done));
        Assert.AreEqual(ImportStageStatus.Done, At(done, ImportStage.Apply).Status);
        Assert.AreEqual(ImportStageStatus.Done, At(done, ImportStage.Result).Status);

        var restarted = ImportStepper.Restart(done);
        Assert.IsTrue(restarted.HasSource, "Restart keeps the source.");
        Assert.IsFalse(restarted.XmlLoaded || restarted.PreviewExists || restarted.ResultStatus.Length > 0, "…and forgets everything after it.");
        Assert.AreEqual(ImportStage.Mapping, ImportStepper.Current(restarted), "Back to the read.");
    }

    [TestMethod]
    public void NoStageIsALiveMarketplaceWriteAndTheApplyLabelSaysLocal()
    {
        foreach (var (stage, label) in ImportStepper.Stages)
            Assert.IsFalse(ImportStepper.IsLiveWrite(stage), $"{stage} must not be a live write.");
        StringAssert.Contains(ImportStepper.Stages.Single(x => x.Stage == ImportStage.Apply).Label, "yerel");
        Assert.AreEqual(6, ImportStepper.Stages.Count);
        CollectionAssert.AreEqual(Enum.GetValues<ImportStage>(), ImportStepper.Stages.Select(x => x.Stage).ToArray(), "Every stage, in order.");
    }
}
