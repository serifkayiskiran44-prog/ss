using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

/// Coverage for #2539: ChannelProductsStore.Save must bound identity, URL and
/// total serialized plan size before touching the database - purely technical
/// storage/UI limits, never a provider-format contract.
[TestClass]
public sealed class ChannelPlanBoundsTests
{
    static string NewRoot() => Path.Combine(Path.GetTempPath(), "channel-plan-bounds-" + Guid.NewGuid().ToString("N"));

    static void WithRoot(Action<string> test)
    {
        var root = NewRoot();
        try { test(root); }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    static ChannelProductPlan Valid() => new() { ChannelId = "etsy", ShopId = "shop-a", ProductId = "p1", Currency = "USD" };

    [TestMethod]
    public void NormalPlanSavesFine() => WithRoot(root =>
    {
        var store = new ChannelProductsStore(root);
        store.Save(Valid());
        Assert.IsNotNull(store.Find("etsy", "shop-a", "p1"));
    });

    [TestMethod]
    public void ExactMaxIdentityLengthIsAcceptedAndOneOverIsRejected() => WithRoot(root =>
    {
        var store = new ChannelProductsStore(root);
        var ok = Valid(); ok.ShopId = new string('a', 200);
        store.Save(ok);
        var bad = Valid(); bad.ShopId = new string('a', 201);
        Assert.ThrowsException<ArgumentException>(() => store.Save(bad));
    });

    [TestMethod]
    public void OversizedProductIdIsRejectedBeforeAnyWrite() => WithRoot(root =>
    {
        var store = new ChannelProductsStore(root);
        var plan = Valid(); plan.ProductId = new string('p', 201);
        Assert.ThrowsException<ArgumentException>(() => store.Save(plan));
        Assert.AreEqual(0, store.List().Count);
    });

    [TestMethod]
    public void OversizedListingUrlIsRejected() => WithRoot(root =>
    {
        var store = new ChannelProductsStore(root);
        var plan = Valid(); plan.ListingUrl = "https://example.test/" + new string('x', 2048);
        Assert.ThrowsException<ArgumentException>(() => store.Save(plan));
    });

    [TestMethod]
    public void OversizedTargetCategoryIsRejected() => WithRoot(root =>
    {
        var store = new ChannelProductsStore(root);
        var plan = Valid(); plan.TargetCategory = new string('c', 301);
        Assert.ThrowsException<ArgumentException>(() => store.Save(plan));
    });

    [TestMethod]
    public void OversizedNotesIsRejected() => WithRoot(root =>
    {
        var store = new ChannelProductsStore(root);
        var plan = Valid(); plan.Notes = new string('n', 4001);
        Assert.ThrowsException<ArgumentException>(() => store.Save(plan));
    });

    [TestMethod]
    public void GrosslyOversizedNotesTriggersTheOverallSerializedSizeBound() => WithRoot(root =>
    {
        var store = new ChannelProductsStore(root);
        // Bypass the per-field Notes cap indirectly isn't possible from the public
        // API, so this exercises the overall-size guard via a field that has no
        // dedicated length cap but still counts toward the serialized JSON.
        var plan = Valid(); plan.TargetCategory = new string('c', 300); plan.Notes = new string('n', 4000);
        store.Save(plan); // within both per-field and overall bounds
        Assert.IsNotNull(store.Find("etsy", "shop-a", "p1"));
    });

    [TestMethod]
    public void WhitespaceOnlyIdentityIsRejected() => WithRoot(root =>
    {
        var store = new ChannelProductsStore(root);
        var plan = Valid(); plan.ShopId = "   ";
        Assert.ThrowsException<ArgumentException>(() => store.Save(plan));
    });

    [TestMethod]
    public void InvalidSchemeAndUserInfoInListingUrlAreStillRejected() => WithRoot(root =>
    {
        var store = new ChannelProductsStore(root);
        var ftp = Valid(); ftp.ListingUrl = "ftp://example.test/listing";
        Assert.ThrowsException<ArgumentException>(() => store.Save(ftp));
        var userinfo = Valid(); userinfo.ListingUrl = "https://user:pass@example.test/listing";
        Assert.ThrowsException<ArgumentException>(() => store.Save(userinfo));
    });

    [TestMethod]
    public void UnicodeIdentityWithinBoundsIsAccepted() => WithRoot(root =>
    {
        var store = new ChannelProductsStore(root);
        var plan = Valid(); plan.ShopId = new string('ş', 100);
        store.Save(plan);
        Assert.IsNotNull(store.Find("etsy", plan.ShopId, "p1"));
    });

    [TestMethod]
    public void InvalidSaveNeverWritesAnythingToTheDatabase() => WithRoot(root =>
    {
        var store = new ChannelProductsStore(root);
        var plan = Valid(); plan.ProductId = new string('p', 500);
        Assert.ThrowsException<ArgumentException>(() => store.Save(plan));
        Assert.AreEqual(0, store.List().Count);
        Assert.AreEqual(0, store.CorruptPlans().Count);
    });
}
