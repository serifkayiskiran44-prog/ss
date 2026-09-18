using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class EtsyCredentialChangesTests
{
    static EtsyCredentials Existing() => new("old-key", "old-secret", "42.old-token", "100", "42.old-refresh",
        DateTimeOffset.UtcNow.AddHours(1), "https://callback.example/etsy", new[] { "shops_r", "listings_w" });

    [TestMethod]
    public void AppChangeClearsUnchangedOldTokensAndGrantState()
    {
        var changed = EtsyCredentialChanges.Merge(Existing(), "new-key", "new-secret", "", "", "200", "https://new.example/etsy");

        Assert.AreEqual("new-key", changed.Key);
        Assert.AreEqual("", changed.Token);
        Assert.AreEqual("", changed.RefreshToken);
        Assert.IsNull(changed.ExpiresAt);
        Assert.IsNull(changed.GrantedScopes);
    }

    [TestMethod]
    public void TokenChangeClearsUnchangedOldRefreshToken()
    {
        var changed = EtsyCredentialChanges.Merge(Existing(), "", "", "42.new-token", "", "100", "https://callback.example/etsy");

        Assert.AreEqual("42.new-token", changed.Token);
        Assert.AreEqual("", changed.RefreshToken);
        Assert.IsNull(changed.ExpiresAt);
        Assert.IsNull(changed.GrantedScopes);
    }

    [TestMethod]
    public void ExplicitNewTokenAndRefreshAreKeptDuringAppChange()
    {
        var changed = EtsyCredentialChanges.Merge(Existing(), "new-key", "new-secret", "77.new-token", "77.new-refresh", "200", "https://new.example/etsy");

        Assert.AreEqual("77.new-token", changed.Token);
        Assert.AreEqual("77.new-refresh", changed.RefreshToken);
    }

    [TestMethod]
    public void BlankUnchangedFieldsPreserveExistingConnection()
    {
        var existing = Existing();
        var changed = EtsyCredentialChanges.Merge(existing, "", "", "", "", "100", "https://callback.example/etsy");

        Assert.AreEqual(existing, changed);
    }
}
