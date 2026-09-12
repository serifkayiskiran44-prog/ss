using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

[TestClass]
public sealed class NavlungoConnectionTests
{
    static NavlungoSettings Settings(string clientId = "fb4cd4c9-5569-43ed-9698-e18de5b77f35") => new(clientId, "https://example.com/navlungo/callback", Sandbox: true);

    static Dictionary<string, string> CallbackFor(NavlungoAuthorizationPreparation pending, string clientId, string code = "27194b5b-88be-4bb6-bb0c-f6c1b4cde1c2")
        => new() { ["state"] = pending.State, ["client_id"] = clientId, ["code"] = code };

    [TestMethod]
    public void HandleCallbackAcceptsMatchingStateAndClientIdAndProducesTokenRequest()
    {
        var settings = Settings();
        var pending = NavlungoConnection.Begin(settings);

        var request = NavlungoConnection.HandleCallback(settings, pending, CallbackFor(pending, settings.ClientId));

        Assert.AreEqual(settings.ClientId, request.ClientId);
        Assert.AreEqual("27194b5b-88be-4bb6-bb0c-f6c1b4cde1c2", request.Code);
        Assert.AreEqual("openid offline_access", request.Scope);
    }

    [TestMethod]
    public void HandleCallbackRejectsWrongState()
    {
        var settings = Settings();
        var pending = NavlungoConnection.Begin(settings);
        var query = CallbackFor(pending, settings.ClientId);
        query["state"] = "tampered-state";

        Assert.ThrowsException<InvalidOperationException>(() => NavlungoConnection.HandleCallback(settings, pending, query));
    }

    [TestMethod]
    public void HandleCallbackRejectsMismatchedClientIdAsWrongAccount()
    {
        var settings = Settings();
        var pending = NavlungoConnection.Begin(settings);

        Assert.ThrowsException<InvalidOperationException>(() => NavlungoConnection.HandleCallback(settings, pending, CallbackFor(pending, "a-different-client-id")));
    }

    [TestMethod]
    public void HandleCallbackRejectsMissingCode()
    {
        var settings = Settings();
        var pending = NavlungoConnection.Begin(settings);
        var query = CallbackFor(pending, settings.ClientId);
        query.Remove("code");

        Assert.ThrowsException<InvalidOperationException>(() => NavlungoConnection.HandleCallback(settings, pending, query));
    }

    [TestMethod]
    public void HandleCallbackRejectsReplayOfAnAlreadyConsumedAuthorization()
    {
        var settings = Settings();
        var pending = NavlungoConnection.Begin(settings);
        var query = CallbackFor(pending, settings.ClientId);
        NavlungoConnection.HandleCallback(settings, pending, query);

        Assert.ThrowsException<InvalidOperationException>(() => NavlungoConnection.HandleCallback(settings, pending, query));
    }

    [TestMethod]
    public void BeginProducesOfficialBase64Sha256CodeChallengeNotBase64Url()
    {
        var settings = Settings();
        var pending = NavlungoConnection.Begin(settings);

        var expectedChallenge = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(pending.Verifier)));
        StringAssert.Contains(pending.AuthorizeUrl, Uri.EscapeDataString(expectedChallenge));
    }
}
