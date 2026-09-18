using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class EtsyLiveSafetyTests
{
    static EtsyListingUpdatePreview Preview(long listingId, int quantity, decimal price, string currency) =>
        new("product-1", listingId, quantity, price, currency, DateTime.UtcNow);

    static CatalogProduct Current(EtsyListingUpdatePreview preview) => new()
    {
        Id = preview.ProductId,
        UpdatedUtc = preview.ProductUpdatedUtc,
        Stock = preview.Quantity,
        Price = preview.Price,
        Currency = preview.Currency,
        Name = "Ürün",
        Sku = "SKU-1",
    };

    [TestMethod]
    public async Task CurrencyMismatchBetweenRemoteListingAndPreviewSendsZeroWrites()
    {
        var requests = new List<HttpRequestMessage>();
        using var http = new HttpClient(new FakeEtsyHandler(requests, listingCurrency: "EUR", listingState: "active"));
        var service = new EtsyListingSyncService(new EtsyShopClient(http));
        var preview = Preview(456, 5, 10.00m, "USD");
        var current = Current(preview);
        var credentials = new EtsyCredentials("k", "s", "t", "123");

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await service.DispatchAsync(credentials, current, preview, true));

        Assert.AreEqual(1, requests.Count, "Only the remote GET should happen; no PATCH on currency mismatch.");
        Assert.AreEqual("GET", requests[0].Method.Method);
    }

    [TestMethod]
    public async Task MatchingCurrencyDispatchesTheUpdate()
    {
        var requests = new List<HttpRequestMessage>();
        using var http = new HttpClient(new FakeEtsyHandler(requests, listingCurrency: "USD", listingState: "active"));
        var service = new EtsyListingSyncService(new EtsyShopClient(http));
        var preview = Preview(456, 5, 10.00m, "USD");
        var current = Current(preview);
        var credentials = new EtsyCredentials("k", "s", "t", "123");

        var result = await service.DispatchAsync(credentials, current, preview, true);

        Assert.AreEqual(456, result.ListingId);
        Assert.AreEqual(4, requests.Count);
        Assert.AreEqual("GET", requests[0].Method.Method);
        Assert.AreEqual("/v3/application/listings/456",requests[0].RequestUri!.AbsolutePath);
        Assert.AreEqual("PUT", requests[3].Method.Method);
        Assert.AreEqual("/v3/application/listings/456/inventory",requests[3].RequestUri!.AbsolutePath);
    }

    [TestMethod]
    public async Task ZeroStockDeactivatesInsteadOfPatchingStockOutQuantity()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new FakeEtsyHandler(requests, listingCurrency: "USD", listingState: "active");
        using var http = new HttpClient(handler);
        var service = new EtsyListingSyncService(new EtsyShopClient(http));
        var preview = Preview(456, 0, 10.00m, "USD");
        Assert.IsTrue(preview.Deactivate);
        var current = Current(preview);
        var credentials = new EtsyCredentials("k", "s", "t", "123");

        var result = await service.DispatchAsync(credentials, current, preview, true);

        Assert.AreEqual(456, result.ListingId);
        Assert.AreEqual(3, requests.Count);
        Assert.AreEqual("PATCH", requests[2].Method.Method);
        var body = handler.Bodies[2];
        StringAssert.Contains(body, "state=inactive");
        Assert.IsFalse(body.Contains("quantity"), "Deactivation must not also send a stock-out quantity PATCH.");
    }

    [TestMethod]
    public async Task AlreadyInactiveZeroStockDoesNotWriteAgain()
    {
        var requests = new List<HttpRequestMessage>();
        using var http = new HttpClient(new FakeEtsyHandler(requests, listingCurrency: "USD", listingState: "inactive"));
        var service = new EtsyListingSyncService(new EtsyShopClient(http));
        var preview = Preview(456, 0, 10.00m, "USD");
        var current = Current(preview);
        var credentials = new EtsyCredentials("k", "s", "t", "123");

        var result = await service.DispatchAsync(credentials, current, preview, true);

        Assert.AreEqual(456, result.ListingId);
        Assert.AreEqual(1, requests.Count, "Already-inactive listing must not be PATCHed again.");
    }

    [TestMethod]
    public async Task ConcurrentAuthorizedCallsAreSerializedNotRaced()
    {
        var flight = new AsyncSingleFlight<int>();
        var concurrentInside = 0;
        var maxConcurrentInside = 0;
        var runs = 0;
        async Task<int> Action()
        {
            var current = Interlocked.Increment(ref concurrentInside);
            Interlocked.Exchange(ref maxConcurrentInside, Math.Max(maxConcurrentInside, current));
            Interlocked.Increment(ref runs);
            await Task.Delay(30);
            Interlocked.Decrement(ref concurrentInside);
            return 1;
        }

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => flight.RunAsync(Action)));

        Assert.AreEqual(1, maxConcurrentInside, "Concurrent callers must never run the refresh action at the same time.");
        Assert.AreEqual(8, runs);
    }
    [TestMethod] public async Task DirectLifecycleCallsRejectWrongShopBeforeAnyWrite()
    {
        foreach(var publish in new[]{true,false})
        {
            var requests=new List<HttpRequestMessage>(); using var http=new HttpClient(new FakeEtsyHandler(requests,"USD","active",999)); var client=new EtsyShopClient(http);
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(async()=> { if(publish)await client.PublishListingAsync(new("k","s","t","123"),456); else await client.DeactivateListingAsync(new("k","s","t","123"),456); });
            Assert.AreEqual(1,requests.Count); Assert.AreEqual(HttpMethod.Get,requests[0].Method);
        }
    }

    sealed class FakeEtsyHandler(List<HttpRequestMessage> requests, string listingCurrency, string listingState, long shopId=123) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Add(request);
            Bodies.Add(request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult() ?? "");
            if(request.RequestUri!.AbsolutePath.EndsWith("/inventory")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent("{\"products\":[{\"sku\":\"SKU-1\",\"property_values\":[],\"offerings\":[{\"price\":{\"amount\":1000,\"divisor\":100,\"currency_code\":\"USD\"},\"quantity\":5,\"is_enabled\":true,\"readiness_state_id\":7}]}],\"price_on_property\":[],\"quantity_on_property\":[],\"sku_on_property\":[]}") });
            if (request.Method == HttpMethod.Get)
            {
                var json = $$"""{"listing_id":456,"shop_id":{{shopId}},"title":"Demo","description":"d","state":"{{listingState}}","quantity":5,"price":{"amount":1000,"divisor":100,"currency_code":"{{listingCurrency}}"},"skus":["SKU-1"]}""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"listing_id\":456}") });
        }
    }
}
