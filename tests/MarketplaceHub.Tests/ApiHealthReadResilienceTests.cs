using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

/// Coverage for #2628: a malformed persisted timestamp in ApiHealth must not
/// crash List()/Summary(), and ShouldDefer must fail CLOSED (defer) on a
/// corrupt row rather than silently allowing a network retry we have no
/// reliable rate-limit information for.
[TestClass]
public sealed class ApiHealthReadResilienceTests
{
    static ApiHealthStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "apihealth-resilience-" + Guid.NewGuid().ToString("N"));
        return new ApiHealthStore(root);
    }

    static void InsertRawRow(string root, string channel, string shopId, string lastAttempt, string updated, string? backoffUntil = null, string? lastSuccess = null, string? rateLimitReset = null, string state = "HEALTHY")
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "health.db") }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO ApiHealth(Channel,ShopId,State,AuthStatus,ErrorClass,HttpStatus,LastAttemptUtc,LastSuccessUtc,LastError,RateLimitRemaining,RateLimitLimit,RateLimitResetUtc,RetryAfterSeconds,BackoffUntilUtc,UpdatedUtc) VALUES($channel,$shop,$state,'UNKNOWN','None',NULL,$attempt,$success,'',NULL,NULL,$reset,NULL,$backoff,$updated)";
        cmd.Parameters.AddWithValue("$channel", channel); cmd.Parameters.AddWithValue("$shop", shopId); cmd.Parameters.AddWithValue("$state", state);
        cmd.Parameters.AddWithValue("$attempt", lastAttempt); cmd.Parameters.AddWithValue("$updated", updated);
        cmd.Parameters.AddWithValue("$success", (object?)lastSuccess ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$reset", (object?)rateLimitReset ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$backoff", (object?)backoffUntil ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    static string Now => DateTimeOffset.UtcNow.ToString("O");

    [TestMethod]
    public void ValidRowsWorkNormally()
    {
        var store = NewStore(out var root);
        try
        {
            store.Observe("etsy", "shop1", new ApiHealthObservation { State = "HEALTHY" });
            Assert.AreEqual(1, store.List().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MalformedUpdatedUtcDoesNotDropOtherRows()
    {
        var store = NewStore(out var root);
        try
        {
            store.Observe("etsy", "shop1", new ApiHealthObservation { State = "HEALTHY" });
            InsertRawRow(root, "trendyol", "shop2", Now, "not-a-date");

            var list = store.List();
            Assert.AreEqual(1, list.Count);
            Assert.AreEqual("etsy", list[0].Channel);

            var corrupt = store.CorruptRecords();
            Assert.AreEqual(1, corrupt.Count);
            Assert.AreEqual("trendyol", corrupt[0].Channel);
            Assert.AreEqual("shop2", corrupt[0].ShopId);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MalformedLastAttemptUtcIsIsolated()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "trendyol", "shop2", "garbage", Now);
            Assert.AreEqual(0, store.List().Count);
            Assert.AreEqual(1, store.CorruptRecords().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MalformedLastSuccessUtcIsIsolated()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "trendyol", "shop2", Now, Now, lastSuccess: "xx");
            Assert.AreEqual(0, store.List().Count);
            Assert.AreEqual(1, store.CorruptRecords().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MalformedRateLimitResetUtcIsIsolated()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "trendyol", "shop2", Now, Now, rateLimitReset: "xx");
            Assert.AreEqual(0, store.List().Count);
            Assert.AreEqual(1, store.CorruptRecords().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MalformedBackoffUntilUtcIsIsolated()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "trendyol", "shop2", Now, Now, backoffUntil: "xx");
            Assert.AreEqual(0, store.List().Count);
            Assert.AreEqual(1, store.CorruptRecords().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ShouldDeferFailsClosedOnCorruptBackoffRow()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "trendyol", "shop2", Now, Now, backoffUntil: "corrupt-value");
            Assert.IsTrue(store.ShouldDefer("trendyol", "shop2"), "A corrupt backoff row must defer, never fail-open into a retry.");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ShouldDeferReturnsFalseWhenNoRowExists()
    {
        var store = NewStore(out var root);
        try { Assert.IsFalse(store.ShouldDefer("etsy", "unknown-shop")); }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ShouldDeferWorksNormallyForAHealthyRow()
    {
        var store = NewStore(out var root);
        try
        {
            store.Observe("etsy", "shop1", new ApiHealthObservation { State = "RATE_LIMITED", BackoffUntilUtc = DateTimeOffset.UtcNow.AddMinutes(5) });
            Assert.IsTrue(store.ShouldDefer("etsy", "shop1"));

            store.Observe("etsy", "shop1", new ApiHealthObservation { State = "HEALTHY" });
            Assert.IsFalse(store.ShouldDefer("etsy", "shop1"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void SummaryExcludesCorruptRowsInsteadOfCrashing()
    {
        var store = NewStore(out var root);
        try
        {
            store.Observe("etsy", "shop1", new ApiHealthObservation { State = "HEALTHY" });
            InsertRawRow(root, "trendyol", "shop2", "junk", Now);

            var summary = store.Summary();
            Assert.AreEqual(1, summary.Total);
            Assert.AreEqual(1, summary.Healthy);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void GetReturnsNullForACorruptRowIdentity()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "trendyol", "shop2", "junk", Now);
            Assert.IsNull(store.Get("trendyol", "shop2"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestartPreservesCorruptRowDetection()
    {
        var root = Path.Combine(Path.GetTempPath(), "apihealth-resilience-" + Guid.NewGuid().ToString("N"));
        try
        {
            new ApiHealthStore(root);
            InsertRawRow(root, "trendyol", "shop2", "junk", Now);

            var reopened = new ApiHealthStore(root);
            Assert.AreEqual(0, reopened.List().Count);
            Assert.AreEqual(1, reopened.CorruptRecords().Count);
            Assert.IsTrue(reopened.ShouldDefer("trendyol", "shop2"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MultipleCorruptRowsAreAllReportedIndependently()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "a", "s1", "1", Now);
            InsertRawRow(root, "b", "s2", Now, "2");
            InsertRawRow(root, "c", "s3", Now, Now, backoffUntil: "3");

            var corrupt = store.CorruptRecords();
            Assert.AreEqual(3, corrupt.Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DiagnosticsNeverContainRawLastError()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "trendyol", "shop2", "junk", Now);
            var corrupt = store.CorruptRecords().Single();
            StringAssert.DoesNotMatch(corrupt.Reason, new System.Text.RegularExpressions.Regex("junk"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void WrongShopIdentityIsNeverConfusedWithAnother()
    {
        var store = NewStore(out var root);
        try
        {
            store.Observe("etsy", "shop1", new ApiHealthObservation { State = "RATE_LIMITED", BackoffUntilUtc = DateTimeOffset.UtcNow.AddMinutes(5) });
            InsertRawRow(root, "etsy", "shop2", "junk", Now);

            Assert.IsTrue(store.ShouldDefer("etsy", "shop1"));
            Assert.IsTrue(store.ShouldDefer("etsy", "shop2"), "shop2's own corrupt row must defer independently of shop1's healthy state.");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
