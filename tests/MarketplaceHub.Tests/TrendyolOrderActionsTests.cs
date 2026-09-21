using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class TrendyolOrderActionsTests
{
    [TestMethod]
    public async Task UnsuppliedRequiresExplicitApprovalAndLineId()
    {
        var handler=new Handler();var http=new HttpClient(handler);
        var settings=new TrendyolSettings("42","key","secret","seller-user-agent");
        var client=new TrendyolOrderActionClient(settings,http);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(()=>client.MarkUnsuppliedAsync(99,[new TrendyolUnsuppliedLine(0,1)],500,false));
        Assert.AreEqual(0,handler.CallCount);
    }

    sealed class Handler:HttpMessageHandler
    {
        public int CallCount;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken){CallCount++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{}")});}
    }
}
