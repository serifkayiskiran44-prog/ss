using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

/// Coverage for #2630: ApiHealthStore.Observe must fence writes by ObservedUtc
/// per (Channel,ShopId) - a stale observation (one probe started earlier but
/// completed later than another) must never overwrite a newer state/backoff
/// decision.
[TestClass]
public sealed class ApiHealthObservationFencingTests
{
    static ApiHealthStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "apihealth-fencing-" + Guid.NewGuid().ToString("N"));
        return new ApiHealthStore(root);
    }

    static readonly DateTimeOffset T1 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset T2 = T1.AddSeconds(30);

    [TestMethod]
    public void NewerObservationPersistedFirstThenOlderOneArrivingLateIsDiscarded()
    {
        var store = NewStore(out var root);
        try
        {
            store.Observe("etsy", "shop1", new ApiHealthObservation { State = "RATE_LIMITED", ObservedUtc = T2, BackoffUntilUtc = T2.AddMinutes(5) });
            store.Observe("etsy", "shop1", new ApiHealthObservation { State = "HEALTHY", ObservedUtc = T1 });
            var record = store.Get("etsy", "shop1")!;
            Assert.AreEqual("RATE_LIMITED", record.State, "The stale (earlier-observed) probe must never overwrite the newer state.");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void StaleHealthyCannotEraseNewerRateLimitedBackoff()
    {
        var store = NewStore(out var root);
        try
        {
            store.Observe("etsy", "shop1", new ApiHealthObservation { State = "RATE_LIMITED", ObservedUtc = T2, BackoffUntilUtc = T2.AddMinutes(10) });
            store.Observe("etsy", "shop1", new ApiHealthObservation { State = "HEALTHY", ObservedUtc = T1 });
            var record = store.Get("etsy", "shop1")!;
            Assert.AreEqual(T2.AddMinutes(10), record.BackoffUntilUtc);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void StaleAuthErrorCannotRevertNewerHealthy()
    {
        var store = NewStore(out var root);
        try
        {
            store.Observe("etsy", "shop1", new ApiHealthObservation { State = "HEALTHY", ObservedUtc = T2 });
            store.Observe("etsy", "shop1", new ApiHealthObservation { State = "AUTH_ERROR", ObservedUtc = T1 });
            Assert.AreEqual("HEALTHY", store.Get("etsy", "shop1")!.State);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void LastSuccessUtcNeverMovesBackwardFromAStaleSuccess()
    {
        var store = NewStore(out var root);
        try
        {
            store.Observe("etsy", "shop1", new ApiHealthObservation { State = "HEALTHY", ObservedUtc = T2 });
            store.Observe("etsy", "shop1", new ApiHealthObservation { State = "HEALTHY", ObservedUtc = T1 });
            Assert.AreEqual(T2, store.Get("etsy", "shop1")!.LastSuccessUtc);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void SameObservationRetriedIsIdempotent()
    {
        var store = NewStore(out var root);
        try
        {
            store.Observe("etsy", "shop1", new ApiHealthObservation { State = "HEALTHY", ObservedUtc = T1 });
            store.Observe("etsy", "shop1", new ApiHealthObservation { State = "HEALTHY", ObservedUtc = T1, RateLimitRemaining = 42 });
            Assert.AreEqual(42, store.Get("etsy", "shop1")!.RateLimitRemaining);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DifferentShopsNeverFenceEachOther()
    {
        var store = NewStore(out var root);
        try
        {
            store.Observe("etsy", "shop-a", new ApiHealthObservation { State = "RATE_LIMITED", ObservedUtc = T2 });
            store.Observe("etsy", "shop-b", new ApiHealthObservation { State = "HEALTHY", ObservedUtc = T1 });
            Assert.AreEqual("HEALTHY", store.Get("etsy", "shop-b")!.State);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DifferentChannelsNeverFenceEachOther()
    {
        var store = NewStore(out var root);
        try
        {
            store.Observe("etsy", "shop1", new ApiHealthObservation { State = "RATE_LIMITED", ObservedUtc = T2 });
            store.Observe("ozon", "shop1", new ApiHealthObservation { State = "HEALTHY", ObservedUtc = T1 });
            Assert.AreEqual("HEALTHY", store.Get("ozon", "shop1")!.State);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestartPreservesFencingInformation()
    {
        var root = Path.Combine(Path.GetTempPath(), "apihealth-fencing-" + Guid.NewGuid().ToString("N"));
        try
        {
            new ApiHealthStore(root).Observe("etsy", "shop1", new ApiHealthObservation { State = "RATE_LIMITED", ObservedUtc = T2 });
            var reopened = new ApiHealthStore(root);
            reopened.Observe("etsy", "shop1", new ApiHealthObservation { State = "HEALTHY", ObservedUtc = T1 });
            Assert.AreEqual("RATE_LIMITED", reopened.Get("etsy", "shop1")!.State);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void NewerObservationStillAppliesNormallyInOrder()
    {
        var store = NewStore(out var root);
        try
        {
            store.Observe("etsy", "shop1", new ApiHealthObservation { State = "HEALTHY", ObservedUtc = T1 });
            store.Observe("etsy", "shop1", new ApiHealthObservation { State = "RATE_LIMITED", ObservedUtc = T2 });
            Assert.AreEqual("RATE_LIMITED", store.Get("etsy", "shop1")!.State);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
