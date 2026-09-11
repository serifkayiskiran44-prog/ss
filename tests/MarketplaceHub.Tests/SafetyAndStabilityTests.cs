using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
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
}
