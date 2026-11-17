using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class TrendyolActionLedgerTests
{
    string root = "";
    [TestInitialize] public void Setup() { root = Path.Combine(Path.GetTempPath(), "ty-ledger-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); }
    [TestCleanup] public void Cleanup() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    static readonly TrendyolSettings Settings = new("42", "key", "secret", "ua");

    [TestMethod]
    public async Task SecondIdenticalCancelIsRefusedWithoutAnotherLiveCall()
    {
        var handler = new Handler(HttpStatusCode.OK);
        var client = new TrendyolOrderActionClient(Settings, new HttpClient(handler), new TrendyolActionLedger(root));
        var lines = new[] { new TrendyolUnsuppliedLine(7, 1) };
        await client.MarkUnsuppliedAsync(99, lines, 500, true);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => client.MarkUnsuppliedAsync(99, lines, 500, true));
        Assert.AreEqual(1, handler.Calls);
        // survives a restart: a new ledger over the same folder still knows
        var again = new TrendyolOrderActionClient(Settings, new HttpClient(handler), new TrendyolActionLedger(root));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => again.MarkUnsuppliedAsync(99, lines, 500, true));
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public async Task RejectedByTrendyolCanBeRetriedButTimeoutStaysLocked()
    {
        var lines = new[] { new TrendyolUnsuppliedLine(7, 1) };
        var bad = new Handler(HttpStatusCode.BadRequest);
        var client = new TrendyolOrderActionClient(Settings, new HttpClient(bad), new TrendyolActionLedger(root));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => client.MarkUnsuppliedAsync(99, lines, 500, true));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => client.MarkUnsuppliedAsync(99, lines, 500, true));
        Assert.AreEqual(2, bad.Calls, "A definite rejection must stay retryable.");

        var slow = new Handler(null);
        var client2 = new TrendyolOrderActionClient(Settings, new HttpClient(slow), new TrendyolActionLedger(root));
        await Assert.ThrowsExceptionAsync<HttpRequestException>(() => client2.MarkUnsuppliedAsync(55, lines, 500, true));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => client2.MarkUnsuppliedAsync(55, lines, 500, true));
        Assert.AreEqual(1, slow.Calls, "After an unknown outcome the action must not be repeated blindly.");
    }

    [TestMethod]
    public async Task ClaimApprovalIsAlsoOncePerClaimItems()
    {
        var handler = new Handler(HttpStatusCode.OK);
        var client = new TrendyolOrderActionClient(Settings, new HttpClient(handler), new TrendyolActionLedger(root));
        await client.ApproveClaimAsync("c1", ["i1", "i2"], true);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => client.ApproveClaimAsync("c1", ["i2", "i1"], true));
        Assert.AreEqual(1, handler.Calls);
    }

    sealed class Handler(HttpStatusCode? status) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            if (status is null) throw new HttpRequestException("timeout");
            return Task.FromResult(new HttpResponseMessage(status.Value) { Content = new StringContent("{}") });
        }
    }
}
