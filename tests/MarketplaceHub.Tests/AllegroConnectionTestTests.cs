using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

/// Coverage for #2658: AllegroConnection.TestReadOnlyAsync must verify offers
/// and orders as independent capabilities - one succeeding must never be
/// reported as if it verified the other.
[TestClass]
public sealed class AllegroConnectionTestTests
{
    sealed class RoutedHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? OnOffers;
        public Func<HttpRequestMessage, HttpResponseMessage>? OnOrders;
        public int OffersCalls, OrdersCalls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/sale/offers")) { OffersCalls++; return Task.FromResult(OnOffers!(request)); }
            if (path.Contains("/order/checkout-forms")) { OrdersCalls++; return Task.FromResult(OnOrders!(request)); }
            throw new InvalidOperationException("Unexpected path: " + path);
        }
    }

    static AllegroSettings Settings() => new("client", "secret", "https://example.test/callback", "token", "refresh", false);

    static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body) };

    [TestMethod]
    public async Task BothCapabilitiesVerifiedWhenBothProbesSucceed()
    {
        var handler = new RoutedHandler { OnOffers = _ => Json(HttpStatusCode.OK, "{\"offers\":[]}"), OnOrders = _ => Json(HttpStatusCode.OK, "{\"checkoutForms\":[]}") };
        var result = await new AllegroConnection(new HttpClient(handler)).TestReadOnlyAsync(Settings());
        Assert.AreEqual(AllegroCapabilityStatus.Verified, result.Offers);
        Assert.AreEqual(AllegroCapabilityStatus.Verified, result.Orders);
        Assert.IsTrue(result.FullyVerified);
    }

    [TestMethod]
    public async Task OrdersAccessDeniedDoesNotBecomeOverallSuccess()
    {
        var handler = new RoutedHandler { OnOffers = _ => Json(HttpStatusCode.OK, "{\"offers\":[]}"), OnOrders = _ => Json(HttpStatusCode.Forbidden, "") };
        var result = await new AllegroConnection(new HttpClient(handler)).TestReadOnlyAsync(Settings());
        Assert.AreEqual(AllegroCapabilityStatus.Verified, result.Offers);
        Assert.AreEqual(AllegroCapabilityStatus.BlockedOrUnavailable, result.Orders);
        Assert.IsFalse(result.FullyVerified, "Orders being blocked must never be reported as overall success.");
    }

    [TestMethod]
    public async Task OffersFailingDoesNotAffectOrdersResult()
    {
        var handler = new RoutedHandler { OnOffers = _ => Json(HttpStatusCode.Unauthorized, ""), OnOrders = _ => Json(HttpStatusCode.OK, "{\"checkoutForms\":[]}") };
        var result = await new AllegroConnection(new HttpClient(handler)).TestReadOnlyAsync(Settings());
        Assert.AreEqual(AllegroCapabilityStatus.BlockedOrUnavailable, result.Offers);
        Assert.AreEqual(AllegroCapabilityStatus.Verified, result.Orders);
    }

    [TestMethod]
    public async Task MalformedJsonOnOneCapabilityDoesNotAffectTheOther()
    {
        var handler = new RoutedHandler { OnOffers = _ => Json(HttpStatusCode.OK, "not-json"), OnOrders = _ => Json(HttpStatusCode.OK, "{\"checkoutForms\":[]}") };
        var result = await new AllegroConnection(new HttpClient(handler)).TestReadOnlyAsync(Settings());
        Assert.AreNotEqual(AllegroCapabilityStatus.Verified, result.Offers);
        Assert.AreEqual(AllegroCapabilityStatus.Verified, result.Orders);
    }

    [TestMethod]
    public async Task ServerErrorIsTransientNotBlocked()
    {
        var handler = new RoutedHandler { OnOffers = _ => Json(HttpStatusCode.InternalServerError, ""), OnOrders = _ => Json(HttpStatusCode.OK, "{\"checkoutForms\":[]}") };
        var result = await new AllegroConnection(new HttpClient(handler)).TestReadOnlyAsync(Settings());
        Assert.AreEqual(AllegroCapabilityStatus.TransientFailure, result.Offers);
    }

    [TestMethod]
    public async Task EmptyArraysAreStillVerifiedNotTreatedAsFailure()
    {
        var handler = new RoutedHandler { OnOffers = _ => Json(HttpStatusCode.OK, "{\"offers\":[]}"), OnOrders = _ => Json(HttpStatusCode.OK, "{\"checkoutForms\":[]}") };
        var result = await new AllegroConnection(new HttpClient(handler)).TestReadOnlyAsync(Settings());
        Assert.IsTrue(result.FullyVerified, "Zero records with a valid schema must still count as a verified capability.");
    }

    [TestMethod]
    public async Task BothProbesAreActuallyCalledExactlyOnce()
    {
        var handler = new RoutedHandler { OnOffers = _ => Json(HttpStatusCode.OK, "{\"offers\":[]}"), OnOrders = _ => Json(HttpStatusCode.OK, "{\"checkoutForms\":[]}") };
        _ = await new AllegroConnection(new HttpClient(handler)).TestReadOnlyAsync(Settings());
        Assert.AreEqual(1, handler.OffersCalls, "Orders capability must be probed via the real order-read endpoint, not skipped.");
        Assert.AreEqual(1, handler.OrdersCalls);
    }

    [TestMethod]
    public async Task RealCancellationPropagatesRatherThanBecomingACapabilityFailure()
    {
        var handler = new RoutedHandler { OnOffers = _ => Json(HttpStatusCode.OK, "{\"offers\":[]}"), OnOrders = _ => Json(HttpStatusCode.OK, "{\"checkoutForms\":[]}") };
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => new AllegroConnection(new HttpClient(handler)).TestReadOnlyAsync(Settings(), cts.Token));
    }

    [TestMethod]
    public void InvalidSettingsFailBeforeAnyNetworkCall()
    {
        var handler = new RoutedHandler { OnOffers = _ => throw new InvalidOperationException("must not be called"), OnOrders = _ => throw new InvalidOperationException("must not be called") };
        var invalid = new AllegroSettings("", "secret", "https://example.test/callback", "token", "refresh", false);
        Assert.ThrowsExceptionAsync<ArgumentException>(() => new AllegroConnection(new HttpClient(handler)).TestReadOnlyAsync(invalid));
    }
}
