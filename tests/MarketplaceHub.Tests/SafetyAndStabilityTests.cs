using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class SafetyAndStabilityTests
{
    [TestMethod]
    public void AuditRedactionMasksCredentialsAndPii()
    {
        var safe = TrMarketplaceHubDesktop.AuditStore.Sanitize("Authorization: Bearer abc password=secret user@example.com +905321234567");
        Assert.IsFalse(safe.Contains("abc", StringComparison.Ordinal));
        Assert.IsFalse(safe.Contains("secret", StringComparison.Ordinal));
        Assert.IsFalse(safe.Contains("user@example.com", StringComparison.Ordinal));
        Assert.IsFalse(safe.Contains("905321234567", StringComparison.Ordinal));
    }

    [TestMethod]
    public void StabilityProbeMeasuresEveryIteration()
    {
        var calls = 0;
        var result = TrMarketplaceHubDesktop.StabilityProbe.Run(3, () => calls++);
        Assert.AreEqual(3, calls);
        Assert.AreEqual(3, result.Iterations);
        Assert.IsTrue(result.Elapsed >= TimeSpan.Zero);
    }

    [TestMethod]
    public void CapabilityAuditRejectsUnsupportedOperationsAndPreservesBlockedChannels()
    {
        var audit = TrMarketplaceHubDesktop.MarketplaceCapabilityAudit.Run();

        Assert.IsTrue(audit.IsValid, string.Join("; ", audit.Errors));
        Assert.IsTrue(audit.Rows.All(row => row.Capabilities.All(operation =>
            TrMarketplaceHubDesktop.MarketplaceConnectionCatalog.Get(row.Channel).Capabilities.Supports(operation))));
        Assert.IsTrue(audit.Rows.Where(row => row.LiveApiBlocked).All(row => row.Decision == "LIVE_API_BLOCKED"));
        Assert.IsTrue(audit.Rows.Any(row => row.Channel == "navlungo" && row.Decision == "LIVE_API_BLOCKED"));
    }

    [TestMethod]
    public void EtsyListingClientUsesShopScopedReadAndExactUpdatePayload()
    {
        var requests = new List<HttpRequestMessage>();
        using var http = new HttpClient(new RecordingHandler(requests));
        var client = new TrMarketplaceHubDesktop.EtsyShopClient(http);
        var credentials = new TrMarketplaceHubDesktop.EtsyCredentials("key", "secret", "token", "123");

        var listing = client.GetListingAsync(credentials, 456).GetAwaiter().GetResult();
        var updated = client.UpdateSimpleListingAsync(credentials, 456, 7, 12.50m).GetAwaiter().GetResult();

        Assert.AreEqual(456, listing.ListingId);
        Assert.AreEqual(456, updated.ListingId);
        Assert.AreEqual("GET", requests[0].Method.Method);
        Assert.AreEqual("/v3/application/shops/123/listings/456", requests[0].RequestUri!.AbsolutePath);
        Assert.AreEqual("PATCH", requests[1].Method.Method);
        Assert.AreEqual("/v3/application/shops/123/listings/456", requests[1].RequestUri!.AbsolutePath);
        StringAssert.Contains(requestBodies[1], "quantity=7");
        StringAssert.Contains(requestBodies[1], "price=12.50");
    }

    [TestMethod]
    public void EbayTimeoutIsReportedWithoutLeakingCredentialDetails()
    {
        using var http = new HttpClient(new TimeoutHandler());
        var connection = new TrMarketplaceHubDesktop.EbayConnection(http);
        var settings = new TrMarketplaceHubDesktop.EbaySettings("client", "secret", "runame", "https://example.test/callback", true);
        var tokens = new TrMarketplaceHubDesktop.EbayTokens("access", DateTimeOffset.UtcNow.AddMinutes(5), "refresh", DateTimeOffset.UtcNow.AddDays(1));

        var error = Assert.ThrowsException<InvalidOperationException>(() => connection.GetOrdersAsync(settings, tokens, cancellationToken: new CancellationTokenSource(50).Token).GetAwaiter().GetResult());
        StringAssert.Contains(error.Message, "zaman aşımı");
        Assert.IsFalse(error.Message.Contains("access", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void SecondaryConnectorSnapshotExposesPartialAndBlockedStates()
    {
        var rows = TrMarketplaceHubDesktop.SecondaryConnectorAudit.Snapshot();

        Assert.AreEqual(6, rows.Count);
        Assert.AreEqual("PARTIAL", rows.Single(x => x.Channel == "allegro").Status);
        Assert.IsTrue(rows.Where(x => x.Channel is "joom" or "wish" or "fruugo" or "navlungo")
            .All(x => x.Status == "LIVE_API_BLOCKED" && x.Detail.Contains("HTTP isteği oluşturulmaz")));
    }

    [TestMethod]
    public void BackupRestoreValidatesHashAndKeepsPreRestoreRollbackDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "marketplacehub-drill-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(root, "backup.zip");
        try
        {
            var data = Path.Combine(root, "data");
            Directory.CreateDirectory(data);
            _ = new TrMarketplaceHubDesktop.Catalog.CatalogStore(data).Products();
            File.WriteAllText(Path.Combine(data, "marker.txt"), "before");
            var service = new TrMarketplaceHubDesktop.DataBackupService(data);
            service.Backup(backup);
            File.WriteAllText(Path.Combine(data, "marker.txt"), "changed");

            var manifest = service.Validate(backup);
            service.Restore(backup);

            Assert.IsTrue(manifest.Files.Any(x => x.Path == "marker.txt"));
            Assert.AreEqual("before", File.ReadAllText(Path.Combine(data, "marker.txt")));
            Assert.IsTrue(Directory.GetDirectories(root, "data.pre-restore-*").Length == 1);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static readonly List<string> requestBodies = new();

    private sealed class RecordingHandler(List<HttpRequestMessage> requests) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Add(request);
            requestBodies.Add(request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "");
            var json = request.Method == HttpMethod.Get
                ? "{\"listing_id\":456,\"title\":\"Demo\",\"description\":\"d\",\"state\":\"active\",\"quantity\":2,\"price\":{\"amount\":1250,\"divisor\":100,\"currency_code\":\"USD\"},\"skus\":[\"SKU-1\"]}"
                : "{\"listing_id\":456}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
