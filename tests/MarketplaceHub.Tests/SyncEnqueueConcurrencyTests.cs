using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for #2606: concurrent Enqueue calls for the exact same identity
/// key must never throw a unique-constraint exception - all callers must
/// receive the same single canonical job row.
[TestClass]
public sealed class SyncEnqueueConcurrencyTests
{
    static SyncStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "sync-enqueue-" + Guid.NewGuid().ToString("N"));
        return new SyncStore(root);
    }

    static SyncRequest Request(string entity = "p1", string shop = "shop1", string version = "1") => new("etsy", "update", entity, version, shop);

    [TestMethod]
    public void TwentyParallelEnqueuesForTheSameKeyProduceExactlyOneRowAndOneId()
    {
        var store = NewStore(out var root);
        try
        {
            var tasks = Enumerable.Range(0, 20).Select(_ => Task.Run(() => store.Enqueue(Request()))).ToArray();
            Task.WaitAll(tasks);

            var ids = tasks.Select(t => t.Result.Id).Distinct().ToList();
            Assert.AreEqual(1, ids.Count, "All concurrent enqueues for the same key must return the same job id.");
            Assert.AreEqual(1, store.List().Count(j => j.EntityId == "p1"));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DifferentIdentityFieldsProduceSeparateJobs()
    {
        var store = NewStore(out var root);
        try
        {
            var a = store.Enqueue(Request(entity: "p1"));
            var b = store.Enqueue(Request(entity: "p2"));
            var c = store.Enqueue(Request(entity: "p1", shop: "shop2"));
            var d = store.Enqueue(Request(entity: "p1", version: "2"));

            var ids = new[] { a.Id, b.Id, c.Id, d.Id };
            Assert.AreEqual(4, ids.Distinct().Count());
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ExistingRowInEveryTerminalStateReturnsTheSameOwnerRowNotADuplicate()
    {
        var store = NewStore(out var root);
        try
        {
            // Pending
            var pending = store.Enqueue(Request(entity: "pending"));
            Assert.AreEqual(pending.Id, store.Enqueue(Request(entity: "pending")).Id);

            // Running
            var running = store.Enqueue(Request(entity: "running"));
            store.TryStart(running.Id);
            Assert.AreEqual(running.Id, store.Enqueue(Request(entity: "running")).Id);

            // Succeeded
            var succeeded = store.Enqueue(Request(entity: "succeeded"));
            store.TryStart(succeeded.Id, out var gen1);
            store.Succeed(succeeded.Id, gen1);
            Assert.AreEqual(succeeded.Id, store.Enqueue(Request(entity: "succeeded")).Id);

            // Failed
            var failed = store.Enqueue(Request(entity: "failed"));
            store.Fail(failed.Id, "boom");
            Assert.AreEqual(failed.Id, store.Enqueue(Request(entity: "failed")).Id);

            // Cancelled
            var cancelled = store.Enqueue(Request(entity: "cancelled"));
            store.Cancel(cancelled.Id);
            Assert.AreEqual(cancelled.Id, store.Enqueue(Request(entity: "cancelled")).Id);

            Assert.AreEqual(5, store.List().Count);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void IdempotencyIsPreservedAcrossRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "sync-enqueue-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = new SyncStore(root).Enqueue(Request());
            var reopened = new SyncStore(root);
            var second = reopened.Enqueue(Request());
            Assert.AreEqual(first.Id, second.Id);
            Assert.AreEqual(1, reopened.List().Count);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ChannelAndShopIdentityAreNormalizedForDeduplication()
    {
        var store = NewStore(out var root);
        try
        {
            var a = store.Enqueue(new SyncRequest("ETSY", "update", "p1", "1", "  shop1  "));
            var b = store.Enqueue(new SyncRequest("etsy", "update", "p1", "1", "shop1"));
            Assert.AreEqual(a.Id, b.Id);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
