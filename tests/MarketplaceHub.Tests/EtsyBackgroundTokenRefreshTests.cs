using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class EtsyBackgroundTokenRefreshTests
{
    string root = "";

    [TestInitialize] public void Setup() { root = Path.Combine(Path.GetTempPath(), "etsy-refresh-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); }
    [TestCleanup] public void Cleanup() { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }

    [TestMethod]
    public async Task ExpiredAccessTokenIsRefreshedAndTheRotatedTokensAreSavedBeforeReadingOrders()
    {
        var connection = new MarketplaceConnectionStore(root).Save("etsy", "123", "Etsy A", true);
        var vault = new MarketplaceCredentialVault(root);
        vault.Save(connection.Id, connection.Channel, connection.ShopId, new EtsyCredentials("key", "secret", "old-access", "123", "old-refresh", DateTimeOffset.UtcNow.AddHours(-2)));
        var seen = new List<string>();
        var registry = MarketplaceAdapterRegistry.CreateDefault(root, () => new HttpClient(new Handler(request =>
        {
            seen.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath.EndsWith("/oauth/token", StringComparison.Ordinal))
                return Json("{\"access_token\":\"new-access\",\"refresh_token\":\"new-refresh\",\"expires_in\":3600,\"token_type\":\"Bearer\"}");
            return Json("{\"count\":0,\"results\":[]}");
        })));

        await registry.Get("etsy").ReadOrdersAsync(connection.Id, DateTime.MinValue, CancellationToken.None);

        Assert.IsTrue(seen[0].EndsWith("/oauth/token", StringComparison.Ordinal), "The token must be refreshed before the first order request.");
        var saved = vault.Load<EtsyCredentials>(connection.Id, connection.Channel, connection.ShopId)!;
        Assert.AreEqual("new-access", saved.Token);
        Assert.AreEqual("new-refresh", saved.RefreshToken, "Etsy rotates the refresh token; losing it would end the connection.");
    }

    static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request));
    }
}
