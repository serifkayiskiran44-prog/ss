using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class BizimHesapConnectionTests
{
    sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(reply(request));
        }
    }

    static BizimHesapSettings Settings() => new("firm-1", "token-value");

    [TestMethod]
    public async Task ReadProductsAsync_sends_token_only_to_verified_https_host()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        using var http = new HttpClient(handler);

        var rows = await new BizimHesapConnection(http).ReadProductsAsync(Settings());

        Assert.AreEqual(0, rows.Count);
        Assert.IsNotNull(handler.Request);
        Assert.AreEqual("https://bizimhesap.com/api/b2b/products?page=1&size=100", handler.Request.RequestUri!.ToString());
        CollectionAssert.AreEqual(new[] { "token-value" }, handler.Request.Headers.GetValues("Token").ToArray());
    }

    [TestMethod]
    public async Task ReadProductsAsync_rejects_malformed_json_without_echoing_token()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not-json") });
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => new BizimHesapConnection(http).ReadProductsAsync(Settings()));

        Assert.IsFalse(error.Message.Contains("token-value", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ReadWarehousesAsync_classifies_rate_limit_without_echoing_token()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => new BizimHesapConnection(http).ReadWarehousesAsync(Settings()));

        StringAssert.Contains(error.Message, "429");
        Assert.IsFalse(error.Message.Contains("token-value", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Invalid_settings_fail_before_network_call()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("network must not be called"));
        using var http = new HttpClient(handler);

        await Assert.ThrowsExceptionAsync<ArgumentException>(() => new BizimHesapConnection(http).ReadProductsAsync(new("", "token")));
    }
}
