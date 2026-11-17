using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TrMarketplaceHubDesktop.Hepsiburada;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class HepsiburadaApiTests
{
    sealed record SeenRequest(string Host, string PathAndQuery, string? AuthorizationScheme, string UserAgent);

    sealed class RecordingHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        readonly Queue<HttpResponseMessage> responses = new(responses);
        public List<SeenRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new(request.RequestUri!.Host, request.RequestUri.PathAndQuery,
                request.Headers.Authorization?.Scheme, string.Join(" ", request.Headers.UserAgent.Select(x => x.ToString()))));
            return Task.FromResult(responses.Count > 0
                ? responses.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }

    static HepsiburadaCredentials Credentials(string merchant = "merchant-a", HepsiburadaEnvironment environment = HepsiburadaEnvironment.Production) =>
        new(merchant, "test-service-key", environment, "MonoBridge/1");

    static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    [TestMethod]
    public async Task MerchantReadUsesBasicAuthMerchantPathAndUserAgent()
    {
        var handler = new RecordingHandler(Json("""{"data":[{"merchantId":"merchant-a","merchantSku":"SKU-1","barcode":"8690000000001","hbSku":"HB1"}],"totalPages":1,"number":0}"""));
        using var client = new HepsiburadaApiClient(Credentials(), new HttpClient(handler));

        var rows = await client.GetMerchantProductsAsync();

        Assert.AreEqual("mpop.hepsiburada.com", handler.Requests.Single().Host);
        Assert.AreEqual("/product/api/products/all-products-of-merchant/merchant-a?page=0&size=1000", handler.Requests.Single().PathAndQuery);
        Assert.AreEqual("Basic", handler.Requests.Single().AuthorizationScheme);
        Assert.AreEqual("MonoBridge/1", handler.Requests.Single().UserAgent);
        Assert.AreEqual("HB1", rows.Single().HepsiburadaSku);
    }

    [TestMethod]
    public async Task SitEnvironmentUsesOnlySitHost()
    {
        var handler = new RecordingHandler(Json("""{"data":[],"totalPages":0,"number":0}"""));
        using var client = new HepsiburadaApiClient(Credentials(environment: HepsiburadaEnvironment.Sit), new HttpClient(handler));
        await client.GetMerchantProductsAsync();
        Assert.AreEqual("mpop-sit.hepsiburada.com", handler.Requests.Single().Host);
    }

    [TestMethod]
    public async Task MerchantProductsReadEveryPageWithoutDuplicates()
    {
        var first = Json("""{"data":[{"merchantId":"merchant-a","merchantSku":"SKU-1","barcode":"1","hbSku":"HB1"}],"totalPages":2,"number":0}""");
        var second = Json("""{"data":[{"merchantId":"merchant-a","merchantSku":"SKU-2","barcode":"2","hbSku":"HB2"}],"totalPages":2,"number":1}""");
        var handler = new RecordingHandler(first, second);
        using var client = new HepsiburadaApiClient(Credentials(), new HttpClient(handler));

        var rows = await client.GetMerchantProductsAsync();

        CollectionAssert.AreEqual(new[] { "HB1", "HB2" }, rows.Select(x => x.HepsiburadaSku).ToArray());
        Assert.AreEqual(2, handler.Requests.Count);
        StringAssert.EndsWith(handler.Requests[1].PathAndQuery, "page=1&size=1000");
    }

    [TestMethod]
    public async Task DuplicateRemoteIdentityFailsClosed()
    {
        var handler = new RecordingHandler(Json("""{"data":[{"merchantId":"merchant-a","merchantSku":"SKU-1","barcode":"1","hbSku":"HB1"},{"merchantId":"merchant-a","merchantSku":"SKU-2","barcode":"2","hbSku":"HB1"}],"totalPages":1,"number":0}"""));
        using var client = new HepsiburadaApiClient(Credentials(), new HttpClient(handler));
        await Assert.ThrowsExceptionAsync<System.IO.InvalidDataException>(() => client.GetMerchantProductsAsync());
    }

    [TestMethod]
    public async Task ConnectionTestRejectsAResponseOwnedByAnotherMerchant()
    {
        var handler = new RecordingHandler(Json("""{"data":[{"merchantId":"merchant-b","merchantSku":"SKU-1","barcode":"1","hbSku":"HB1"}],"totalPages":1,"number":0}"""));
        using var client = new HepsiburadaApiClient(Credentials(), new HttpClient(handler));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => client.TestReadOnlyAsync());
    }

    [TestMethod]
    public async Task InvalidOrOversizedResponseFailsClosed()
    {
        using (var invalid = new HepsiburadaApiClient(Credentials(), new HttpClient(new RecordingHandler(Json("not-json")))))
            await Assert.ThrowsExceptionAsync<System.IO.InvalidDataException>(() => invalid.GetMerchantProductsAsync());
        using (var oversized = new HepsiburadaApiClient(Credentials(), new HttpClient(new RecordingHandler(Json(new string('x', 8 * 1024 * 1024 + 1))))))
            await Assert.ThrowsExceptionAsync<System.IO.InvalidDataException>(() => oversized.GetMerchantProductsAsync());
    }

    [TestMethod]
    public async Task UnauthorizedErrorDoesNotRevealServiceKey()
    {
        var credentials = Credentials();
        using var client = new HepsiburadaApiClient(credentials, new HttpClient(new RecordingHandler(Json("{}", HttpStatusCode.Unauthorized))));
        var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => client.GetMerchantProductsAsync());
        StringAssert.Contains(error.Message, "401");
        Assert.IsFalse(error.ToString().Contains(credentials.ServiceKey, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ListingsUseAccountScopedListingHostAndMapCommercialFields()
    {
        var handler = new RecordingHandler(Json("""{"listings":[{"merchantId":"merchant-a","merchantSku":"SKU-1","hbSku":"HB1","barcode":"869","availableStock":7,"price":125.50,"dispatchTime":2}]}"""));
        using var client = new HepsiburadaApiClient(Credentials(), new HttpClient(handler));

        var listing = (await client.GetListingsAsync()).Single();

        Assert.AreEqual("listing-external.hepsiburada.com", handler.Requests.Single().Host);
        Assert.AreEqual("/listings/merchantid/merchant-a?offset=0&limit=1000", handler.Requests.Single().PathAndQuery);
        Assert.AreEqual("HB1", listing.HepsiburadaSku);
        Assert.AreEqual("869", listing.Barcode);
        Assert.AreEqual(7, listing.Stock);
        Assert.AreEqual(125.50m, listing.Price);
        Assert.AreEqual(2, listing.DispatchTime);
    }

    [TestMethod]
    public async Task CancellationStopsBeforeSending()
    {
        var handler = new RecordingHandler(Json("{}"));
        using var client = new HepsiburadaApiClient(Credentials(), new HttpClient(handler));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => client.GetMerchantProductsAsync(cancellation.Token));
        Assert.AreEqual(0, handler.Requests.Count);
    }
}
