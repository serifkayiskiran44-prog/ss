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
public sealed class HepsiburadaMetadataTests
{
    sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        readonly Queue<HttpResponseMessage> queue = new(responses);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(queue.Dequeue());
    }

    sealed class RecordingDelay : IHepsiburadaDelay
    {
        public List<TimeSpan> Delays { get; } = [];
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) { Delays.Add(delay); return Task.CompletedTask; }
    }

    static HepsiburadaCredentials Credentials() => new("merchant-a", "secret", HepsiburadaEnvironment.Production, "MonoBridge/1");
    static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    [TestMethod]
    public async Task CategoriesExposeOnlyPublishableLeafEntriesToTheWorkspace()
    {
        var response = Json("""{"data":[{"categoryId":10,"name":"Kap","paths":"Ev > Kap","leaf":true,"available":true,"status":"ACTIVE"},{"categoryId":11,"name":"Kapalı","paths":"Ev > Kapalı","leaf":true,"available":false,"status":"ACTIVE"}],"totalPages":1,"number":0}""");
        using var client = new HepsiburadaApiClient(Credentials(), new HttpClient(new QueueHandler(response)));

        var categories = await client.GetCategoriesAsync();

        CollectionAssert.AreEqual(new long[] { 10 }, categories.Where(x => x.Publishable).Select(x => x.Id).ToArray());
    }

    [TestMethod]
    public async Task CategoryAttributesFlattenGroupsAndKeepControlledValues()
    {
        var response = Json("""{"data":{"baseAttributes":[{"id":"Material","name":"Malzeme","mandatory":true,"multiValue":false,"type":"enum","values":[{"id":"PL","name":"Plastik"}]}],"attributes":[],"variantAttributes":[]}}""");
        using var client = new HepsiburadaApiClient(Credentials(), new HttpClient(new QueueHandler(response)));

        var attribute = (await client.GetCategoryAttributesAsync(10)).Single();

        Assert.AreEqual("Material", attribute.Id);
        Assert.IsTrue(attribute.Mandatory);
        Assert.AreEqual(HepsiburadaAttributeKind.List, attribute.Kind);
        Assert.AreEqual("Plastik", attribute.Values.Single().Name);
    }

    [TestMethod]
    public async Task ExtremeRateLimitResetUsesBoundedDelay()
    {
        var limited = Json("{}", HttpStatusCode.TooManyRequests);
        limited.Headers.TryAddWithoutValidation("Retry-After", "999999999");
        var success = Json("""{"data":[{"hbSku":"HB1","merchantSku":"SKU1","rank":2,"winningPrice":100,"ownPrice":105}]}""");
        var delay = new RecordingDelay();
        using var client = new HepsiburadaApiClient(Credentials(), new HttpClient(new QueueHandler(limited, success)), delay);

        var rows = await client.GetBuyboxAsync(["HB1"]);

        Assert.AreEqual("HB1", rows.Single().HepsiburadaSku);
        Assert.IsTrue(delay.Delays.Single() <= TimeSpan.FromSeconds(30));
        Assert.IsTrue(delay.Delays.Single() > TimeSpan.Zero);
    }

    [TestMethod]
    public async Task ConflictingDuplicateCategoryIdsFailClosed()
    {
        var response = Json("""{"data":[{"categoryId":10,"name":"A","paths":"A","leaf":true,"available":true,"status":"ACTIVE"},{"categoryId":10,"name":"B","paths":"B","leaf":true,"available":true,"status":"ACTIVE"}],"totalPages":1,"number":0}""");
        using var client = new HepsiburadaApiClient(Credentials(), new HttpClient(new QueueHandler(response)));
        await Assert.ThrowsExceptionAsync<System.IO.InvalidDataException>(() => client.GetCategoriesAsync());
    }
}
