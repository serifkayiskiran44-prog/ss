using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class EtsyMetadataTests
{
    static EtsyCredentials Credentials(string token = "42.fixture-token", string shopId = "123") =>
        new("fixture-key", "fixture-secret", token, shopId);

    [TestMethod]
    public async Task ShopUsesOfficialRouteAndValidatesReturnedIdentity()
    {
        var handler = new QueueHandler(_ => Json("""{"shop_id":123,"user_id":42,"shop_name":"Atölye","currency_code":"TRY"}"""));
        using var http = new HttpClient(handler);

        var shop = await new EtsyMetadataClient(http).GetShopAsync(Credentials());

        Assert.AreEqual(new EtsyShopInfo(123, "Atölye", "TRY", 42), shop);
        var request = handler.Requests.Single();
        Assert.AreEqual("https://openapi.etsy.com/v3/application/shops/123", request.Url);
        Assert.AreEqual("fixture-key:fixture-secret", request.ApiKey);
        Assert.AreEqual("Bearer 42.fixture-token", request.Authorization);
    }

    [TestMethod]
    public async Task ShopRejectsConfiguredShopAndTokenOwnerMismatches()
    {
        using var wrongShopHttp = new HttpClient(new QueueHandler(_ => Json("""{"shop_id":999,"user_id":42,"shop_name":"Atölye","currency_code":"TRY"}""")));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => new EtsyMetadataClient(wrongShopHttp).GetShopAsync(Credentials()));

        using var wrongUserHttp = new HttpClient(new QueueHandler(_ => Json("""{"shop_id":123,"user_id":99,"shop_name":"Atölye","currency_code":"TRY"}""")));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => new EtsyMetadataClient(wrongUserHttp).GetShopAsync(Credentials()));
    }

    [TestMethod]
    public async Task LegacyShortTestTokenWithoutNumericPrefixRemainsCompatible()
    {
        using var http = new HttpClient(new QueueHandler(_ => Json("""{"shop_id":123,"user_id":99,"shop_name":"Fixture","currency_code":"USD"}""")));

        var shop = await new EtsyMetadataClient(http).GetShopAsync(Credentials("token123"));

        Assert.AreEqual(99L, shop.UserId);
    }

    [TestMethod]
    public async Task SellerTaxonomyIsFlattenedWithStablePathsAndPublicHeaders()
    {
        var handler = new QueueHandler(_ => Json("""
            {"count":1,"results":[{"id":10,"name":"Home","parent_id":null,"children":[
              {"id":11,"name":"Décor","parent_id":10,"children":[{"id":12,"name":"Vases","parent_id":11,"children":[]}]}
            ]}]}
            """));
        using var http = new HttpClient(handler);

        var nodes = await new EtsyMetadataClient(http).GetSellerTaxonomyAsync(Credentials());

        CollectionAssert.AreEqual(new[] { "Home", "Home > Décor", "Home > Décor > Vases" }, nodes.Select(x => x.Path).ToArray());
        Assert.AreEqual(10L, nodes[1].ParentId);
        Assert.AreEqual("https://openapi.etsy.com/v3/application/seller-taxonomy/nodes", handler.Requests.Single().Url);
        Assert.IsNull(handler.Requests.Single().Authorization, "Public taxonomy must use the required API key without leaking a bearer token.");
    }

    [TestMethod]
    public async Task PropertiesUseOfficialSchemaForValuesAndScales()
    {
        var handler = new QueueHandler(_ => Json("""
            {"count":1,"results":[{"property_id":200,"name":"color","display_name":"Renk","is_required":true,
             "supports_attributes":true,"supports_variations":false,
             "possible_values":[{"value_id":201,"name":"Mavi","scale_id":301}],
             "scales":[{"scale_id":301,"display_name":"Ton","description":""}]}]}
            """));
        using var http = new HttpClient(handler);

        var properties = await new EtsyMetadataClient(http).GetPropertiesAsync(Credentials(), 10);

        var property = properties.Single();
        Assert.AreEqual(new EtsyNamedValue(201, "Mavi"), property.Values.Single());
        Assert.AreEqual(new EtsyNamedValue(301, "Ton"), property.Scales.Single());
        Assert.IsTrue(property.Required);
        Assert.AreEqual("https://openapi.etsy.com/v3/application/seller-taxonomy/nodes/10/properties", handler.Requests.Single().Url);
    }

    [TestMethod]
    public async Task ShopMetadataVerifiesOwnershipAndPaginatesReadinessProfiles()
    {
        var handler = new QueueHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v3/application/shops/123" => Json("""{"shop_id":123,"user_id":42,"shop_name":"Atölye","currency_code":"TRY"}"""),
            "/v3/application/shops/123/readiness-state-definitions" when request.RequestUri.Query == "?limit=100&offset=0" =>
                Json("""{"count":2,"results":[{"shop_id":123,"readiness_state_id":501,"readiness_state":"made_to_order","processing_days_display_label":"1-3 days"}]}"""),
            "/v3/application/shops/123/readiness-state-definitions" when request.RequestUri.Query == "?limit=100&offset=1" =>
                Json("""{"count":2,"results":[{"shop_id":123,"readiness_state_id":502,"readiness_state":"ready_to_ship","processing_days_display_label":"Ready"}]}"""),
            _ => throw new AssertFailedException(request.RequestUri.ToString())
        });
        using var http = new HttpClient(handler);

        var profiles = await new EtsyMetadataClient(http).GetProcessingProfilesAsync(Credentials());

        CollectionAssert.AreEqual(new long[] { 501, 502 }, profiles.Select(x => x.Id).ToArray());
        Assert.AreEqual("made_to_order", profiles[0].ReadinessState);
        Assert.IsTrue(handler.Requests.All(x => x.Authorization == "Bearer 42.fixture-token"));
    }

    [TestMethod]
    public async Task ShippingProfilesAndSectionsUseDocumentedFields()
    {
        var handler = new QueueHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v3/application/shops/123" => Json("""{"shop_id":123,"user_id":42,"shop_name":"Atölye","currency_code":"TRY"}"""),
            "/v3/application/shops/123/shipping-profiles" => Json("""{"count":1,"results":[{"shipping_profile_id":601,"user_id":42,"title":"Türkiye"}]}"""),
            "/v3/application/shops/123/sections" => Json("""{"count":1,"results":[{"shop_section_id":701,"user_id":42,"title":"Seramik"}]}"""),
            _ => throw new AssertFailedException(request.RequestUri.ToString())
        });
        using var http = new HttpClient(handler);
        var client = new EtsyMetadataClient(http);

        var shipping = await client.GetShippingProfilesAsync(Credentials());
        var sections = await client.GetSectionsAsync(Credentials());

        Assert.AreEqual(new EtsyShippingProfile(601, "Türkiye"), shipping.Single());
        Assert.AreEqual(new EtsyShopSection(701, "Seramik"), sections.Single());
    }

    [TestMethod]
    public async Task ShippingProfileFromDifferentShopOwnerIsRejected()
    {
        var handler = new QueueHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v3/application/shops/123" => Json("""{"shop_id":123,"user_id":42,"shop_name":"Atölye","currency_code":"TRY"}"""),
            "/v3/application/shops/123/shipping-profiles" => Json("""{"count":1,"results":[{"shipping_profile_id":601,"user_id":99,"title":"Başka hesap"}]}"""),
            _ => throw new AssertFailedException(request.RequestUri.ToString())
        });
        using var http = new HttpClient(handler);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => new EtsyMetadataClient(http).GetShippingProfilesAsync(Credentials()));
    }

    [TestMethod]
    public async Task InvalidMetadataAndCancellationAreRejected()
    {
        using var invalidHttp = new HttpClient(new QueueHandler(_ => Json("""{"shop_id":123,"user_id":42,"shop_name":"","currency_code":"TRY"}""")));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => new EtsyMetadataClient(invalidHttp).GetShopAsync(Credentials()));

        using var canceledHttp = new HttpClient(new CancellationHandler());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => new EtsyMetadataClient(canceledHttp).GetShopAsync(Credentials(), cancellation.Token));
    }

    [DataTestMethod]
    [DataRow(401, "Kimlik doğrulanamadı")]
    [DataRow(403, "Erişim reddedildi")]
    [DataRow(429, "İstek sınırına ulaşıldı")]
    public async Task HttpFailuresUseSafeActionableMessages(int statusCode, string expected)
    {
        using var http = new HttpClient(new QueueHandler(_ => new HttpResponseMessage((HttpStatusCode)statusCode)
        {
            Content = new StringContent("secret remote payload")
        }));

        var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => new EtsyMetadataClient(http).GetShopAsync(Credentials()));

        StringAssert.Contains(error.Message, expected);
        StringAssert.Contains(error.Message, statusCode.ToString());
        Assert.IsFalse(error.Message.Contains("secret remote payload", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task OAuthPreservesActualGrantedScopesAndLegacyRefreshScopes()
    {
        var handler = new QueueHandler(_ => Json("""{"access_token":"42.new-token","token_type":"Bearer","expires_in":3600,"refresh_token":"42.new-refresh","scope":"shops_r listings_r"}"""));
        using var http = new HttpClient(handler);
        var refreshed = await new EtsyOAuth(http).RefreshAsync(Credentials() with { RefreshToken = "42.old-refresh" });
        CollectionAssert.AreEqual(new[] { "shops_r", "listings_r" }, refreshed.GrantedScopes!.ToArray());

        var legacy = Credentials() with { RefreshToken = "42.old-refresh", GrantedScopes = new[] { "shops_r" } };
        using var legacyHttp = new HttpClient(new QueueHandler(_ => Json("""{"access_token":"42.next-token","token_type":"Bearer","expires_in":3600,"refresh_token":"42.next-refresh"}""")));
        var legacyRefreshed = await new EtsyOAuth(legacyHttp).RefreshAsync(legacy);
        CollectionAssert.AreEqual(new[] { "shops_r" }, legacyRefreshed.GrantedScopes!.ToArray());
    }

    static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    sealed record RecordedRequest(string Url, string? ApiKey, string? Authorization);

    sealed class QueueHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.RequestUri!.ToString(),
                request.Headers.TryGetValues("x-api-key", out var values) ? values.Single() : null,
                request.Headers.Authorization?.ToString()));
            return Task.FromResult(respond(request));
        }
    }

    sealed class CancellationHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new AssertFailedException();
        }
    }
}
