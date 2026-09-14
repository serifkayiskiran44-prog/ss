using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

/// Coverage for #2543: a credential-bearing connector request must never
/// treat a 3xx response as success or silently follow it - the production
/// fix disables HttpClientHandler.AllowAutoRedirect on the connection-test
/// client (MarketplaceConnectionsPanel), which means a 3xx reaches the
/// connector unfollowed; these tests lock in that the connector itself
/// correctly treats any such response as a failed test, never a success,
/// for every redirect status code a malicious/compromised endpoint could
/// return.
[TestClass]
public sealed class AuthHttpRedirectSafetyTests
{
    sealed class FixedResponseHandler : HttpMessageHandler
    {
        readonly HttpStatusCode status; readonly string? location;
        public int RequestCount { get; private set; }
        public Uri? LastRequestUri { get; private set; }
        public FixedResponseHandler(HttpStatusCode status, string? location = null) { this.status = status; this.location = location; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++; LastRequestUri = request.RequestUri;
            var response = new HttpResponseMessage(status);
            if (location != null) response.Headers.Location = new Uri(location);
            return Task.FromResult(response);
        }
    }

    static EtsyCredentials Credentials() => new("key123", "secret123", "token123", "12345");

    [DataTestMethod]
    [DataRow(301)]
    [DataRow(302)]
    [DataRow(303)]
    [DataRow(307)]
    [DataRow(308)]
    public async Task RedirectStatusCodesAreTreatedAsFailureNotSuccess(int statusCode)
    {
        var handler = new FixedResponseHandler((HttpStatusCode)statusCode, "https://attacker.example.com/steal");
        using var client = new HttpClient(handler);
        var connector = new EtsyConnector(client);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => connector.TestAsync(Credentials()));
        Assert.AreEqual(1, handler.RequestCount, "Exactly one request must be sent - a 3xx must never trigger a second, followed request.");
    }

    [TestMethod]
    public async Task DirectSuccessStillWorks()
    {
        var handler = new FixedResponseHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        var connector = new EtsyConnector(client);
        // A 200 with no valid JSON body still fails at the parse stage, but must
        // not fail at the status-code stage - proving normal non-redirect traffic
        // is unaffected by the redirect-rejection logic.
        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => connector.TestAsync(Credentials()));
        StringAssert.Contains(ex.Message, "JSON");
    }

    [TestMethod]
    public async Task RedirectResponseNeverExposesCredentialsInExceptionMessage()
    {
        var handler = new FixedResponseHandler(HttpStatusCode.Found, "https://attacker.example.com/steal?token=token123");
        using var client = new HttpClient(handler);
        var connector = new EtsyConnector(client);

        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => connector.TestAsync(Credentials()));
        StringAssert.DoesNotMatch(ex.Message, new System.Text.RegularExpressions.Regex("token123|secret123|attacker\\.example\\.com"));
    }

    [TestMethod]
    public async Task RequestUriIsNeverMutatedByAFollowedRedirect()
    {
        var handler = new FixedResponseHandler(HttpStatusCode.MovedPermanently, "https://attacker.example.com/steal");
        using var client = new HttpClient(handler);
        var connector = new EtsyConnector(client);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => connector.TestAsync(Credentials()));
        Assert.AreEqual("openapi.etsy.com", handler.LastRequestUri!.Host, "The only request sent must still target the real Etsy host, never the redirect target.");
    }

    [TestMethod]
    public async Task AuthErrorStatusCodesAreStillDistinguishedFromRedirects()
    {
        var handler = new FixedResponseHandler(HttpStatusCode.Unauthorized);
        using var client = new HttpClient(handler);
        var connector = new EtsyConnector(client);
        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => connector.TestAsync(Credentials()));
        StringAssert.Contains(ex.Message, "401");
    }
}
