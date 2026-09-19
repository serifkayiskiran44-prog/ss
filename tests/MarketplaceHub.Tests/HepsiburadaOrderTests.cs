using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TrMarketplaceHubDesktop.Hepsiburada;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class HepsiburadaOrderTests
{
    [TestMethod]
    public async Task PaidOrderReadUsesMerchantScopedProductionEndpointAndProjectsIdentity()
    {
        var handler = new Handler("""
        {"totalCount":1,"items":[{"id":"line-1","merchantId":"merchant-a","orderNumber":"order-1","orderDate":"2026-09-19T10:00:00Z","lastStatusUpdateDate":"2026-09-19T10:05:00Z","status":"Open","sku":"HB1","merchantSku":"SKU1","productBarcode":"8690000000001","name":"Ürün","quantity":2,"price":{"amount":25.5,"currency":"TRY"},"shippingAddress":{"addressDetail":"Adres","city":"İstanbul","town":"Kadıköy"},"customerName":"Müşteri"}]}
        """);
        using var http = new HttpClient(handler);
        var credentials = Credentials();
        using var client = new HepsiburadaApiClient(credentials, http);
        var order = (await new HepsiburadaOrderReader(client).ReadAsync(credentials, "connection-a", DateTime.MinValue)).Single();

        Assert.AreEqual("https://oms-external.hepsiburada.com/orders/merchantid/merchant-a?offset=0&limit=50", handler.Requests.Single());
        Assert.AreEqual("hepsiburada", order.Marketplace);
        Assert.AreEqual("connection-a", order.ConnectionId);
        Assert.AreEqual("merchant-a", order.ShopId);
        Assert.AreEqual("order-1", order.OrderId);
        Assert.AreEqual("HB1", order.Items.Single().ProductId);
        Assert.AreEqual(2, order.Items.Single().Quantity);
        Assert.AreEqual(51m, order.Total);
    }

    [TestMethod]
    public async Task ResponseOwnedByAnotherMerchantIsRejected()
    {
        var handler = new Handler("""
        {"totalCount":1,"items":[{"id":"line-1","merchantId":"merchant-b","orderNumber":"order-1","orderDate":"2026-09-19T10:00:00Z","sku":"HB1","name":"Ürün","quantity":1}]}
        """);
        using var client = new HepsiburadaApiClient(Credentials(), new HttpClient(handler));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => client.GetOrdersAsync(DateTime.MinValue));
    }

    [TestMethod]
    public async Task InvalidQuantityIsRejectedBeforeSharedStockFlow()
    {
        var handler = new Handler("""
        {"totalCount":1,"items":[{"id":"line-1","merchantId":"merchant-a","orderNumber":"order-1","orderDate":"2026-09-19T10:00:00Z","sku":"HB1","name":"Ürün","quantity":0}]}
        """);
        using var client = new HepsiburadaApiClient(Credentials(), new HttpClient(handler));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => client.GetOrdersAsync(DateTime.MinValue));
    }

    static HepsiburadaCredentials Credentials() => new("merchant-a", "service-key", HepsiburadaEnvironment.Production, "MonoBridge/1");

    sealed class Handler(string json) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
