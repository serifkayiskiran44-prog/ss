using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Regression coverage for issue #309: opening a legacy SyncJobs table (created before
/// the ShopId/ErrorClass columns existed) used to fail with "no such column: ShopId"
/// because SyncStore.Open() built a (Channel,ShopId,...) unique index before the
/// constructor's own EnsureColumn calls had a chance to add the column.
[TestClass]
public sealed class SyncSchemaMigrationTests
{
    static string NewLegacyDatabase()
    {
        var root = Path.Combine(Path.GetTempPath(), "sync-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "catalog.db");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        // Pre-ShopId/ErrorClass schema, as it existed before those columns were added.
        command.CommandText = """
            CREATE TABLE SyncJobs(
                Id TEXT PRIMARY KEY,
                Channel TEXT NOT NULL,
                Operation TEXT NOT NULL,
                EntityId TEXT NOT NULL,
                Version TEXT NOT NULL,
                Status INTEGER NOT NULL,
                FailureCount INTEGER NOT NULL,
                LastError TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL
            );
            INSERT INTO SyncJobs(Id,Channel,Operation,EntityId,Version,Status,FailureCount,LastError,UpdatedUtc)
            VALUES('legacy-1','etsy','stock-price','listing-1','v1',0,0,'','2026-01-01T00:00:00Z');
            """;
        command.ExecuteNonQuery();
        connection.Close();
        SqliteConnection.ClearAllPools();
        return root;
    }

    [TestMethod]
    public void OpeningLegacyDatabaseWithoutShopIdColumnDoesNotThrow()
    {
        var root = NewLegacyDatabase();
        try
        {
            var store = new SyncStore(root);
            var jobs = store.List();
            Assert.AreEqual(1, jobs.Count);
            Assert.AreEqual("default", jobs[0].ShopId, "Legacy rows must backfill to the default shop, not crash or lose data.");
            Assert.AreEqual("legacy-1", jobs[0].Id);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [TestMethod]
    public void UniqueKeyStillEnforcedAfterMigrationForNewRows()
    {
        var root = NewLegacyDatabase();
        try
        {
            var store = new SyncStore(root);
            var first = store.Enqueue(new SyncRequest("etsy", "stock-price", "listing-2", "v1", "shop-a"));
            var second = store.Enqueue(new SyncRequest("etsy", "stock-price", "listing-2", "v1", "shop-a"));
            var thirdOtherShop = store.Enqueue(new SyncRequest("etsy", "stock-price", "listing-2", "v1", "shop-b"));

            Assert.AreEqual(first.Id, second.Id, "Same channel/shop/operation/entity/version must be one job.");
            Assert.AreNotEqual(first.Id, thirdOtherShop.Id, "Different shop must be a separate job.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [TestMethod]
    public void LegacyDuplicateRowsThatWouldViolateTheNewUniqueKeyAreNotDeleted()
    {
        var root = Path.Combine(Path.GetTempPath(), "sync-schema-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "catalog.db");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE SyncJobs(
                    Id TEXT PRIMARY KEY, Channel TEXT NOT NULL, Operation TEXT NOT NULL, EntityId TEXT NOT NULL,
                    Version TEXT NOT NULL, Status INTEGER NOT NULL, FailureCount INTEGER NOT NULL, LastError TEXT NOT NULL, UpdatedUtc TEXT NOT NULL
                );
                INSERT INTO SyncJobs VALUES('dup-1','etsy','stock-price','listing-1','v1',0,0,'','2026-01-01T00:00:00Z');
                INSERT INTO SyncJobs VALUES('dup-2','etsy','stock-price','listing-1','v1',0,0,'','2026-01-02T00:00:00Z');
                """;
            command.ExecuteNonQuery();
            connection.Close();
        }
        SqliteConnection.ClearAllPools();
        try
        {
            var store = new SyncStore(root);
            Assert.AreEqual(2, store.List().Count, "Legacy duplicate rows must be preserved, not silently deleted, when they collide with the new key.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [TestMethod]
    public void RepeatedOpenDoesNotThrowOnAlreadyMigratedDatabase()
    {
        var root = NewLegacyDatabase();
        try
        {
            _ = new SyncStore(root);
            _ = new SyncStore(root);
            var store = new SyncStore(root);
            Assert.AreEqual(1, store.List().Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
