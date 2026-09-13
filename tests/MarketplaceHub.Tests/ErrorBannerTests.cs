using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #816 (DESIGN: Recoverable error banner actions). One rule set decides whether a failure can be retried, what
// the banner says, which actions it offers, and that a repeat becomes a count rather than a second banner.
[TestClass]
public sealed class ErrorBannerTests
{
    [TestMethod]
    public void RetryableFailuresOfferRetryOnlyWhenTheSurfaceCanReRunTheAction()
    {
        foreach (var error in new Exception[] { new HttpRequestException("boom"), new TimeoutException(), new TaskCanceledException(), new IOException("disk") })
        {
            Assert.AreEqual(ErrorRecovery.Retryable, ErrorBanner.Classify(error), error.GetType().Name);
            var withRetry = ErrorBanner.Describe(error, retryAvailable: true);
            var withoutRetry = ErrorBanner.Describe(error, retryAvailable: false);
            Assert.IsTrue(withRetry.CanRetry, $"{error.GetType().Name}: retryable and the surface can re-run.");
            Assert.IsFalse(withoutRetry.CanRetry, $"{error.GetType().Name}: a retry button with nothing behind it is a lie.");
            Assert.AreEqual(NotificationSeverity.Warning, withRetry.Severity, "A temporary failure is a warning, not an error.");
            Assert.IsTrue(withRetry.CanOpenDiagnostics);
        }
        var wrapped = new AggregateException(new HttpRequestException("inner"));
        Assert.AreEqual(ErrorRecovery.Retryable, ErrorBanner.Classify(wrapped), "The wrapper is not the failure.");
    }

    [TestMethod]
    public void TerminalFailuresShowTheInstructionAndNeverOfferRetry()
    {
        var validation = ErrorBanner.Describe(new InvalidOperationException("Önce ürün seç."), retryAvailable: true);
        Assert.AreEqual(ErrorRecovery.Terminal, validation.Recovery);
        Assert.IsFalse(validation.CanRetry, "Retrying the same input cannot help.");
        Assert.AreEqual("Önce ürün seç.", validation.Text, "A validation message is the instruction; it is shown as it is.");
        Assert.AreEqual(NotificationSeverity.Error, validation.Severity);

        var unknown = ErrorBanner.Describe(new NullReferenceException("Object reference not set to an instance of an object."), retryAvailable: true);
        Assert.AreEqual(ErrorRecovery.Terminal, unknown.Recovery);
        Assert.IsFalse(unknown.Text.Contains("Object reference"), "An internal message is not an instruction; the operator gets a sentence and a pointer to diagnostics.");
        StringAssert.Contains(unknown.Text, "Tanılama");
    }

    [TestMethod]
    public void ADuplicateBecomesACountNotASecondBanner()
    {
        var first = ErrorBanner.Describe(new HttpRequestException("x"), retryAvailable: false);
        var again = ErrorBanner.Merge(first, ErrorBanner.Describe(new HttpRequestException("y"), retryAvailable: true));

        Assert.AreEqual(2, again.Count, "Same recovery, same text: one banner, counted.");
        Assert.IsTrue(again.CanRetry, "A later occurrence that can be retried upgrades the standing banner.");

        var different = ErrorBanner.Merge(first, ErrorBanner.Describe(new InvalidOperationException("Önce ürün seç."), retryAvailable: false));
        Assert.AreEqual(1, different.Count, "Different news replaces the banner.");
        Assert.AreEqual(ErrorRecovery.Terminal, different.Recovery);
        Assert.AreEqual(1, ErrorBanner.Merge(null, first).Count);
    }

    [TestMethod]
    public void TheSourceActionIsOfferedOnlyWhenTheSurfaceKnowsWhereTheFixLives()
    {
        var withSource = ErrorBanner.Describe(new IOException("feed"), retryAvailable: false, sourceRoute: "xml", sourceLabel: "XML kaynağına git");
        Assert.AreEqual("xml", withSource.SourceRoute);
        Assert.AreEqual("XML kaynağına git", withSource.SourceLabel);

        var withoutSource = ErrorBanner.Describe(new IOException("feed"), retryAvailable: false);
        Assert.AreEqual("", withoutSource.SourceRoute, "No invented destination.");
    }

    [TestMethod]
    public void SecretsPiiAndRawPayloadsNeverReachTheBanner()
    {
        var secret = ErrorBanner.Describe(new InvalidOperationException("Reddedildi: Authorization: Bearer abc.def · ali@example.com · C:\\Users\\serif\\x"), retryAvailable: false);
        foreach (var forbidden in new[] { "abc.def", "ali@example.com", "\\serif\\" })
            Assert.IsFalse(secret.Text.Contains(forbidden, StringComparison.Ordinal), $"'{forbidden}' reached the banner: {secret.Text}");

        var payload = ErrorBanner.Describe(new InvalidOperationException("{\"error\":\"invalid_grant\",\"error_description\":\"expired\"}"), retryAvailable: false);
        Assert.AreEqual(StatusTooltip.RawPayloadHidden, payload.Text, "A body is evidence, not a message.");

        var novel = ErrorBanner.Describe(new ArgumentException(new string('a', 2000)), retryAvailable: false);
        Assert.IsTrue(novel.Text.Length <= ErrorBanner.MaxTextLength);
        StringAssert.EndsWith(novel.Text, "…");
    }
}
