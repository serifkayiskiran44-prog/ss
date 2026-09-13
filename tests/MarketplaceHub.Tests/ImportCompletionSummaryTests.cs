using System;
using System.Globalization;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #828 (DESIGN: Import completion summary). Five outcomes from the terminal result only; counts, duration and
// source revision on every one; next actions that fit the outcome and exist as routes; no address, no payload.
[TestClass]
public sealed class ImportCompletionSummaryTests
{
    static readonly DateTime T0 = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);
    static bool Routes(string key) => key is "products" or "xml" or "diagnostics";

    static ImportCompletionInput Input(ImportSummary result = null, string error = "", bool cancelled = false, int selected = 10, int rejected = 0, int warnings = 0, bool complete = true) =>
        new("Tedarikçi A", 3, "abcdef0123456789", complete, selected, rejected, warnings, result, error, cancelled, T0, T0.AddSeconds(2.5));

    [TestMethod]
    public void SuccessCountsWhatWasWrittenAndOffersTheProductPool()
    {
        var c = ImportCompletionSummary.Compose(Input(new ImportSummary(6, 3, 1), warnings: 2), Routes);

        Assert.AreEqual(ImportOutcome.Success, c.Outcome); Assert.AreEqual(SeverityLevel.Success, c.Level);
        Assert.AreEqual(9, c.Applied); Assert.AreEqual(6, c.Added); Assert.AreEqual(3, c.Updated); Assert.AreEqual(1, c.Unchanged); Assert.AreEqual(0, c.Rejected); Assert.AreEqual(2, c.Warnings);
        StringAssert.Contains(c.CountsLine, "9 uygulandı"); StringAssert.Contains(c.CountsLine, "1 aynı"); StringAssert.Contains(c.CountsLine, "2 uyarılı");
        Assert.AreEqual(TimeSpan.FromSeconds(2.5), c.Duration);
        Assert.AreEqual("Süre: " + 2.5.ToString("0.#", CultureInfo.CurrentCulture) + " sn", c.DurationLine);
        Assert.AreEqual("Tedarikçi A · eşleme rev. 3 · akış abcdef01", c.Revision, "Name, mapping revision and a hash prefix -- never the address.");
        CollectionAssert.AreEqual(new[] { ImportNextActionKind.OpenProducts, ImportNextActionKind.ShowWarnings }, c.NextActions.Select(a => a.Kind).ToArray());
    }

    [TestMethod]
    public void PartialNamesTheRefusedRowsAndFailureAndCancelWriteNothing()
    {
        var partial = ImportCompletionSummary.Compose(Input(new ImportSummary(4, 0, 0), selected: 6, rejected: 2), Routes);
        Assert.AreEqual(ImportOutcome.Partial, partial.Outcome); Assert.AreEqual(SeverityLevel.Warning, partial.Level);
        Assert.AreEqual(4, partial.Applied); Assert.AreEqual(2, partial.Rejected);
        Assert.AreEqual(ImportNextActionKind.ShowRejected, partial.NextActions[0].Kind);

        var failure = ImportCompletionSummary.Compose(Input(error: "Birden fazla satır aynı ürüne eşleşiyor.", selected: 6), Routes);
        Assert.AreEqual(ImportOutcome.Failure, failure.Outcome); Assert.AreEqual(SeverityLevel.Blocking, failure.Level);
        Assert.AreEqual(0, failure.Applied); Assert.AreEqual(6, failure.Rejected, "The transaction rolled back: the whole selection was not written.");
        StringAssert.Contains(failure.Detail, "aynı ürüne");
        CollectionAssert.AreEqual(new[] { ImportNextActionKind.Retry, ImportNextActionKind.OpenDiagnostics }, failure.NextActions.Select(a => a.Kind).ToArray());

        var cancelled = ImportCompletionSummary.Compose(Input(cancelled: true, selected: 6), Routes);
        Assert.AreEqual(ImportOutcome.Cancelled, cancelled.Outcome);
        Assert.AreEqual(0, cancelled.Applied); Assert.AreEqual(6, cancelled.Rejected);
        Assert.AreEqual("Aktarım iptal edildi", cancelled.Headline);
        Assert.AreEqual(ImportNextActionKind.Retry, cancelled.NextActions.Single().Kind);
    }

    [TestMethod]
    public void NoOpCoversTheAlreadyAppliedFeedAndAnAllIdenticalSelection()
    {
        var already = ImportCompletionSummary.Compose(Input(new ImportSummary(0, 0, 0, AlreadyApplied: true, FeedHash: "abcdef0123456789")), Routes);
        Assert.AreEqual(ImportOutcome.NoOp, already.Outcome); Assert.AreEqual(SeverityLevel.Info, already.Level);
        Assert.AreEqual("Değişiklik yok", already.Headline); StringAssert.Contains(already.Detail, "daha önce");
        Assert.AreEqual(ImportNextActionKind.Reread, already.NextActions[0].Kind);

        var identical = ImportCompletionSummary.Compose(Input(new ImportSummary(0, 0, 10)), Routes);
        Assert.AreEqual(ImportOutcome.NoOp, identical.Outcome); Assert.AreEqual(10, identical.Unchanged); StringAssert.Contains(identical.Detail, "aynı");
    }

    [TestMethod]
    public void RevisionSaysPartialSelectionWithoutAFeedHashAndActionsNeedTheirRoute()
    {
        var partialSelection = ImportCompletionSummary.Compose(Input(new ImportSummary(1, 0, 0), complete: false), Routes);
        StringAssert.Contains(partialSelection.Revision, "kısmi seçim");
        Assert.IsFalse(partialSelection.Revision.Contains("abcdef"), "A partial selection applied no feed hash, so none is quoted.");

        var noRoutes = ImportCompletionSummary.Compose(Input(error: "x"), _ => false);
        Assert.AreEqual(ImportNextActionKind.Retry, noRoutes.NextActions.Single().Kind, "Without a diagnostics route only the page-local action remains.");
    }

    [TestMethod]
    public void ErrorDetailIsRedactedAndAPayloadOrStackTraceIsNeverShown()
    {
        var secret = ImportCompletionSummary.Compose(Input(error: "https://ali:gizli@supplier.example.com/feed.xml?token=ABC123 reddetti"), Routes);
        Assert.IsFalse(secret.Detail.Contains("gizli") || secret.Detail.Contains("ABC123"), secret.Detail);

        var payload = ImportCompletionSummary.Compose(Input(error: "<?xml version=\"1.0\"?><Products><Product><Code>S1</Code></Product></Products>"), Routes);
        Assert.IsFalse(payload.Detail.Contains("<Products"), payload.Detail); StringAssert.Contains(payload.Detail, "tanılama");

        var trace = ImportCompletionSummary.Compose(Input(error: "Object reference\n   at TrMarketplaceHubDesktop.Catalog.CatalogStore.ImportCore(XmlSource s)"), Routes);
        Assert.IsFalse(trace.Detail.Contains("ImportCore"), trace.Detail);

        var blank = ImportCompletionSummary.Compose(Input(result: null), Routes);
        Assert.AreEqual(ImportOutcome.Failure, blank.Outcome); StringAssert.Contains(blank.Detail, "Sonuç alınamadı");
        Assert.AreEqual("1 dk 5 sn", ImportCompletionSummary.FormatDuration(TimeSpan.FromSeconds(65)));
    }
}
