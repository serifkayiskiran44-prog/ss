using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #2560 (API HEALTH: bounded/paged health query and UI cancellation for large-DB scale safety). List, Get and
// Summary must never materialize the whole table: Get is a direct single-row lookup, List pages with a deterministic
// order (no row skipped or repeated across pages even when several share the same UpdatedUtc), Summary is one SQL
// aggregate, a search string is matched literally (a caller's own '%' or '_' never acts as a wildcard), an overlong
// query is bounded rather than crashing, and the async path honors cancellation without corrupting state.
[TestClass]
public sealed class ApiHealthPaginationTests
{
    static void Seed(ApiHealthStore health, int count, DateTimeOffset baseTime)
    {
        for (var i = 0; i < count; i++)
            health.Observe("etsy", "S" + i.ToString("D5"), new ApiHealthObservation { State = i % 7 == 0 ? "AUTH_ERROR" : "HEALTHY", AuthStatus = "VALID", ObservedUtc = baseTime }); // many rows share the same UpdatedUtc on purpose
    }

    [TestMethod]
    public void ListPagesDeterministicallyOverManyTiedTimestampsGetIsADirectLookupAndSummaryIsOneAggregate()
    {
        var root = Path.Combine(Path.GetTempPath(), "apihealth-page-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var health = new ApiHealthStore(root);
            var now = DateTimeOffset.UtcNow;
            Seed(health, 1000, now); // 1000 rows, most sharing the exact same UpdatedUtc -- the classic tie case

            // Paging through with a small page size touches every row exactly once, in a stable order.
            var seen = new System.Collections.Generic.HashSet<(string, string)>();
            var pageSize = 37; var offset = 0;
            while (true)
            {
                var page = health.List(null, null, pageSize, offset);
                if (page.Count == 0) break;
                foreach (var row in page) Assert.IsTrue(seen.Add((row.Channel, row.ShopId)), $"{row.Channel}/{row.ShopId} appeared twice across pages");
                offset += pageSize;
                Assert.IsTrue(page.Count <= pageSize);
            }
            Assert.AreEqual(1000, seen.Count, "every row was visited exactly once");
            Assert.IsTrue(health.List(null, null, 10000, 0).Count <= ApiHealthStore.MaxPageSize, "the page size is bounded even when a huge limit is requested");

            // Get is a single-row lookup -- correct regardless of how many rows exist.
            var direct = health.Get("etsy", "S00042"); Assert.IsNotNull(direct); Assert.AreEqual("S00042", direct!.ShopId);
            Assert.IsNull(health.Get("etsy", "S99999"));

            // Summary is one aggregate call, correct against the seeded mix (1000/7 = 142 remainder 6 -> rows 0,7,14... are AUTH_ERROR: 143 of them).
            var summary = health.Summary(now);
            Assert.AreEqual(1000, summary.Total); Assert.AreEqual(143, summary.AuthErrors); Assert.AreEqual(857, summary.Healthy);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void ALiteralSearchAnOverlongQueryAndCancellationAreEachHandledSafely()
    {
        var root = Path.Combine(Path.GetTempPath(), "apihealth-search-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var health = new ApiHealthStore(root);
            health.Observe("etsy", "50%_off", new ApiHealthObservation { State = "HEALTHY" });
            health.Observe("etsy", "normal-shop", new ApiHealthObservation { State = "HEALTHY" });

            // A search containing '%' and '_' is literal: it must find the shop that actually has those characters, and not the other one as if they were wildcards.
            Assert.AreEqual(1, health.List("50%_off").Count); Assert.AreEqual("50%_off", health.List("50%_off").Single().ShopId);
            Assert.AreEqual(0, health.List("50X_off").Count, "an unescaped '%' would have matched here; a literal search must not");

            // An overlong query is bounded, not rejected with a crash.
            var overlong = new string('a', 5000);
            var result = health.List(overlong); Assert.AreEqual(0, result.Count, "no shop matches, and the call itself completed");

            // Cancellation: a token cancelled before the call throws promptly and leaves nothing applied.
            using var cts = new CancellationTokenSource(); cts.Cancel();
            Assert.ThrowsExceptionAsync<OperationCanceledException>(() => health.ListAsync(null, null, null, 0, cts.Token)).GetAwaiter().GetResult();
            Assert.ThrowsExceptionAsync<OperationCanceledException>(() => health.SummaryAsync(null, cts.Token)).GetAwaiter().GetResult();

            // The async path returns the same data as the sync path when not cancelled.
            var asyncRows = health.ListAsync(null, null, null, 0, CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(health.List().Count, asyncRows.Count);
            var asyncSummary = health.SummaryAsync(null, CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(health.Summary().Total, asyncSummary.Total);
        }
        finally { Cleanup(root); }
    }

    static void Cleanup(string root)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
            catch (IOException) { Thread.Sleep(300); }
            catch (UnauthorizedAccessException) { Thread.Sleep(300); }
        }
    }
}
