using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #276 (P0 AUDIT: Etsy canlı fiyat/para birimi ... sıfır stok güvenliği): re-verifies, on the current SHA,
// two of the three originally-audited claims against the REAL dispatch path (EtsyListingSyncService.DispatchAsync
// -> EtsyShopClient), not just a standalone synthetic helper (EtsyLiveSafetyGate already has its own unit test,
// but that class never makes an HTTP call, so it cannot prove "the real path makes zero network writes on
// mismatch"). Code reading found both claims already fixed on this SHA; these tests are the missing proof.
// The third claim (AuthorizedAsync's OAuth refresh single-flight) is deliberately NOT exercised here:
// CredentialStore reads/writes a fixed machine-wide %LocalAppData%\MonoBridgeDesktop\credentials.bin path with
// no test-root override, so driving it from a test would risk clobbering this machine's real stored Etsy
// credentials. That is flagged as a real testability gap, not silently worked around.
[TestClass]
public sealed class EtsyLiveDispatchSafetyTests
{
    static CatalogProduct Product(decimal price, int stock, string currency = "USD") => new()
    {
        Id = "p1", EtsyListingId = "456", Active = true, Price = price, Stock = stock, Currency = currency,
        SourceId = "src", Sku = "SKU-1", UpdatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    static EtsyCredentials Credentials() => new("key", "secret", "token", "123");

    [TestMethod]
    public void DispatchBlocksOnRemoteShopCurrencyMismatchAndSendsZeroPatchRequests()
    {
        var product = Product(100m, 5);
        var requests = new List<HttpRequestMessage>();
        using var http = new HttpClient(new ShopAndListingHandler(requests, shopCurrency: "EUR")); // shop is EUR, product/preview is USD
        var client = new EtsyShopClient(http);
        var service = new EtsyListingSyncService(client);
        var preview = service.CreatePreview(product);

        var ex = Assert.ThrowsException<InvalidOperationException>(() =>
            service.DispatchAsync(Credentials(), product, preview, approved: true).GetAwaiter().GetResult());

        StringAssert.Contains(ex.Message, "CURRENCY_MISMATCH");
        Assert.AreEqual(1, requests.Count, "Only the shop-currency GET may be sent; the mismatch must be caught before any PATCH is attempted.");
        Assert.IsFalse(requests.Any(r => r.Method.Method == "PATCH"), "A currency mismatch must result in zero write requests to Etsy.");
    }

    [TestMethod]
    public void DispatchSendsExactQuantityAndPriceWhenCurrencyMatches()
    {
        var product = Product(12.50m, 7);
        var requests = new List<HttpRequestMessage>();
        var handler = new ShopAndListingHandler(requests, shopCurrency: "USD");
        using var http = new HttpClient(handler);
        var client = new EtsyShopClient(http);
        var service = new EtsyListingSyncService(client);
        var preview = service.CreatePreview(product);

        var result = service.DispatchAsync(Credentials(), product, preview, approved: true).GetAwaiter().GetResult();

        Assert.AreEqual(456, result.ListingId);
        Assert.AreEqual(2, requests.Count);
        Assert.AreEqual("PATCH", requests[1].Method.Method);
        StringAssert.Contains(handler.Bodies[1], "quantity=7");
        StringAssert.Contains(handler.Bodies[1], "price=12.50");
        Assert.IsFalse(handler.Bodies[1].Contains("state="), "A healthy-stock update must not touch listing state.");
    }

    [TestMethod]
    public void DispatchDeactivatesTheListingInsteadOfHardRejectingWhenStockReachesZero()
    {
        var product = Product(12.50m, 0); // sellable stock depleted -- must deactivate, not throw or sell nothing silently.
        var requests = new List<HttpRequestMessage>();
        var handler = new ShopAndListingHandler(requests, shopCurrency: "USD");
        using var http = new HttpClient(handler);
        var client = new EtsyShopClient(http);
        var service = new EtsyListingSyncService(client);
        var preview = service.CreatePreview(product); // must not throw for Stock == 0 -- zero is a valid, expected state.

        Assert.IsTrue(preview.DeactivatesListing);
        var result = service.DispatchAsync(Credentials(), product, preview, approved: true).GetAwaiter().GetResult();

        Assert.AreEqual(456, result.ListingId);
        StringAssert.Contains(handler.Bodies[1], "quantity=0");
        StringAssert.Contains(handler.Bodies[1], "state=inactive");
    }

    sealed class ShopAndListingHandler(List<HttpRequestMessage> requests, string shopCurrency) : HttpMessageHandler
    {
        public readonly List<string> Bodies = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Add(request);
            Bodies.Add(request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult() ?? "");
            var json = request.Method == HttpMethod.Get ? $"{{\"currency_code\":\"{shopCurrency}\"}}" : "{\"listing_id\":456}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
