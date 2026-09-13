using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #692 (SOURCE HEALTH: XML URL erişilebilirlik ve latency ölçümü): URL reachability, timeout, latency,
// status classification and safe diagnostics, all reusing the existing ApiHealthClassifier rather than a
// second classification scheme.
[TestClass]
public sealed class XmlSourceHealthCheckerTests
{
    static XmlSource Source(string location) => new() { Id = Guid.NewGuid().ToString("N"), Name = "Test", Location = location };

    [TestMethod]
    public async Task HealthyResponseIsClassifiedReadyWithMeasuredLatency()
    {
        using var http = new HttpClient(new FixedResponseHandler(HttpStatusCode.OK, delayMs: 30));
        var result = await XmlSourceHealthChecker.CheckAsync(http, Source("https://example.test/feed.xml"));

        Assert.AreEqual("HEALTHY", result.State);
        Assert.AreEqual(200, result.HttpStatus);
        Assert.IsTrue(result.LatencyMs >= 25, $"Expected measured latency to reflect the artificial 30ms delay, got {result.LatencyMs}ms.");  // 25: Task.Delay/timer resolution can fire ~1ms early on the CI runner (measured 29ms)
    }

    [TestMethod]
    public async Task NotFoundIsClassifiedAsClientErrorNotAGenericFailure()
    {
        using var http = new HttpClient(new FixedResponseHandler(HttpStatusCode.NotFound));
        var result = await XmlSourceHealthChecker.CheckAsync(http, Source("https://example.test/missing.xml"));

        Assert.AreEqual("CLIENT_ERROR", result.State);
        Assert.AreEqual(404, result.HttpStatus);
    }

    [TestMethod]
    public async Task ServerErrorIsClassifiedDistinctlyFromClientError()
    {
        using var http = new HttpClient(new FixedResponseHandler(HttpStatusCode.InternalServerError));
        var result = await XmlSourceHealthChecker.CheckAsync(http, Source("https://example.test/feed.xml"));

        Assert.AreEqual("SERVER_ERROR", result.State);
        Assert.AreEqual(500, result.HttpStatus);
    }

    [TestMethod]
    public async Task ASlowFeedPastTheBoundedTimeoutIsClassifiedAsTimeoutNotLeftHanging()
    {
        using var http = new HttpClient(new NeverRespondingHandler());
        var result = await XmlSourceHealthChecker.CheckAsync(http, Source("https://example.test/slow.xml"), timeout: TimeSpan.FromMilliseconds(100));

        Assert.AreEqual("TIMEOUT", result.State);
        Assert.IsNull(result.HttpStatus);
    }

    [TestMethod]
    public async Task ExternalCancellationPropagatesInsteadOfBeingMisreportedAsATimeout()
    {
        using var http = new HttpClient(new NeverRespondingHandler());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // HttpClient's own cancellation surfaces as the more specific TaskCanceledException, which is-a
        // OperationCanceledException; the production catch clause (a type check, not Assert.ThrowsException's
        // exact-type match) correctly treats it as such and lets it propagate uncaught.
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() =>
            XmlSourceHealthChecker.CheckAsync(http, Source("https://example.test/feed.xml"), cts.Token));
    }

    [TestMethod]
    public async Task AnExistingReadableLocalFileIsHealthyWithoutAnyNetworkCall()
    {
        var path = Path.Combine(Path.GetTempPath(), "xml-health-" + Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(path, "<Products/>");
        try
        {
            using var http = new HttpClient(new ThrowingHandler());
            var result = await XmlSourceHealthChecker.CheckAsync(http, Source(path));

            Assert.AreEqual("HEALTHY", result.State);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task AMissingLocalFileIsClassifiedAsClientErrorNotSilentlyHealthy()
    {
        var path = Path.Combine(Path.GetTempPath(), "xml-health-missing-" + Guid.NewGuid().ToString("N") + ".xml");
        using var http = new HttpClient(new ThrowingHandler());

        var result = await XmlSourceHealthChecker.CheckAsync(http, Source(path));

        Assert.AreNotEqual("HEALTHY", result.State);
    }

    [TestMethod]
    public async Task AnEmptyLocationIsNotConfiguredRatherThanAttemptingARequest()
    {
        using var http = new HttpClient(new ThrowingHandler());
        var result = await XmlSourceHealthChecker.CheckAsync(http, Source(""));

        Assert.AreEqual("NOT_CONFIGURED", result.State);
    }

    sealed class FixedResponseHandler(HttpStatusCode status, int delayMs = 0) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (delayMs > 0) await Task.Delay(delayMs, cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent("") };
        }
    }

    sealed class NeverRespondingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("A local-file check must never make a network call.");
    }
}
