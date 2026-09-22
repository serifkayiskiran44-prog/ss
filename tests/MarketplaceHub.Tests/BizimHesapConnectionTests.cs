using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Text.Json;
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
    public async Task ReadProductsAsync_reads_bizimhesap_data_products_wrapper()
    {
        var body = "{\"resultCode\":0,\"errorText\":\"\",\"data\":{\"products\":[{\"id\":\"42\",\"code\":\"SKU-1\",\"barcode\":\"B-1\",\"title\":\"Ürün\",\"price\":10.5,\"quantity\":7.0}]}}";
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        using var http = new HttpClient(handler);

        var row = (await new BizimHesapConnection(http).ReadProductsAsync(Settings())).Single();

        Assert.AreEqual("SKU-1", row.Code);
        Assert.AreEqual(7, row.Quantity);
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

    [TestMethod]
    public void BuildAddProductRequest_uses_net_price_and_collection_field_names()
    {
        using var request = BizimHesapConnection.BuildAddProductRequest(Settings(), new BizimHesapCreateProduct("SKU-1", "BARKOD-1", "Ürün", 100m, 20m, 5, "TRY"));
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual("https://bizimhesap.com/api/b2b/addproduct", request.RequestUri!.ToString());
        using var body = JsonDocument.Parse(request.Content!.ReadAsStream());
        Assert.AreEqual("SKU-1", body.RootElement.GetProperty("id").GetString());
        Assert.AreEqual(100m, body.RootElement.GetProperty("price").GetDecimal());
        Assert.AreEqual("TL", body.RootElement.GetProperty("currency").GetString());
        Assert.AreEqual(20m, body.RootElement.GetProperty("taxRate").GetDecimal());
    }

    [TestMethod]
    public async Task AddProductAsync_returns_success_result_without_exposing_credentials()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"resultCode\":0,\"errorText\":\"\",\"data\":{\"id\":\"remote-42\"}}")
        });
        using var http = new HttpClient(handler);

        var result = await new BizimHesapConnection(http).AddProductAsync(Settings(), new("SKU-1", "BARKOD-1", "Ürün", 100m, 20m, 5, "TRY"));

        Assert.AreEqual(0, result.ResultCode);
        Assert.AreEqual("remote-42", result.RemoteId);
    }

    [TestMethod]
    public async Task AddProductAsync_surfaces_sanitized_api_error_text()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"resultCode\":12,\"errorText\":\"Ürün kodu reddedildi\"}")
        });
        using var http = new HttpClient(handler);

        var error = await Assert.ThrowsExceptionAsync<BizimHesapProductRejectedException>(() =>
            new BizimHesapConnection(http).AddProductAsync(Settings(), new("SKU-1", "BARKOD-1", "Ürün", 100m, 20m, 5, "TRY")));

        StringAssert.Contains(error.Message, "Ürün kodu reddedildi");
        Assert.IsFalse(error.Message.Contains("token-value", StringComparison.Ordinal));
    }
}
