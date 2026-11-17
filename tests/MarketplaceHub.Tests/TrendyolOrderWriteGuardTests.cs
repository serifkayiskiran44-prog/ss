using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

/// The legacy single-account trendyol.bin must never be used to write for an order that belongs to another Trendyol shop.
[TestClass]
public sealed class TrendyolOrderWriteGuardTests
{
    string dir = "";
    CountingHandler handler = null!;

    [TestInitialize]
    public void Setup() { dir = Path.Combine(Path.GetTempPath(), "trendyol-guard-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); handler = new(); }

    [TestCleanup]
    public void Cleanup() { SqliteConnection.ClearAllPools(); if (Directory.Exists(dir)) Directory.Delete(dir, true); }

    [TestMethod]
    public void GuardAcceptsOnlyTheShopTheCredentialsBelongTo()
    {
        var settings = new TrendyolSettings("101", "key", "secret", "agent");
        TrendyolOrderWriteGuard.Require(settings, "101");
        Assert.ThrowsException<InvalidOperationException>(() => TrendyolOrderWriteGuard.Require(settings, "202"));
        Assert.ThrowsException<InvalidOperationException>(() => TrendyolOrderWriteGuard.Require(settings, ""));
    }

    [TestMethod]
    public async Task QueueMarketplaceUploadForAnotherShopFailsBeforeAnyHttpCall()
    {
        new TrendyolSettingsStore(Path.Combine(dir, "trendyol.bin")).Save(new("101", "key", "secret", "agent"));
        using var operations = new NesQueueOperations(dir, new HttpClient(handler));
        var item = new NesQueueItem { Marketplace = "Trendyol", ShopId = "202", OrderId = "1", PackageId = "55", InvoiceNumber = "INV1" };

        await Assert.ThrowsExceptionAsync<NesQueueValidationException>(() => operations.UploadMarketplaceAsync(item, new NesDownloadedInvoice("x.pdf", "", new byte[] { 1 }), CancellationToken.None));

        Assert.AreEqual(0, handler.Calls);
    }

    sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }
    }
}
