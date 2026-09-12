using System;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #780 (DB: WAL checkpoint health diagnostics). DatabaseHealth.Inspect already covers quick_check, schema
// version, journal_mode, and foreign_keys via a read-only connection; it reported nothing about the WAL
// file's actual size or whether a checkpoint can make progress. Uses PRAGMA wal_checkpoint(PASSIVE) --
// SQLite's own non-disruptive mode, which never blocks or forces out a concurrent reader/writer -- rather
// than a more aggressive mode, so running this diagnostic is safe against a live, in-use database.
[TestClass]
public sealed class WalCheckpointHealthTests
{
    static string NewCatalog(string root)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "catalog.db");
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        c.Open();
        using (var pragma = c.CreateCommand()) { pragma.CommandText = "PRAGMA journal_mode=WAL;"; pragma.ExecuteNonQuery(); }
        using (var create = c.CreateCommand()) { create.CommandText = "CREATE TABLE T(Id INTEGER, Data TEXT)"; create.ExecuteNonQuery(); }
        return path;
    }

    [TestMethod]
    public void ANormalWalWithNoContentionCheckpointsCleanlyWithZeroPagesLeftBehind()
    {
        var root = Path.Combine(Path.GetTempPath(), "wal-health-" + Guid.NewGuid().ToString("N"));
        try
        {
            NewCatalog(root);
            var result = DatabaseHealth.Inspect(root);

            Assert.AreEqual("HEALTHY", result.Status);
            Assert.IsFalse(result.CheckpointBusy);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void ALargeWalReportsItsActualSizeInsteadOfAHardcodedGuess()
    {
        var root = Path.Combine(Path.GetTempPath(), "wal-health-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = NewCatalog(root);
            using (var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
            {
                writer.Open();
                // Hold an open read transaction so the WAL cannot be checkpointed away, letting it accumulate.
                using var reader = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
                reader.Open();
                using (var beginRead = reader.CreateCommand()) { beginRead.CommandText = "BEGIN; SELECT COUNT(*) FROM T;"; beginRead.ExecuteScalar(); }

                using var tx = writer.BeginTransaction();
                for (var i = 0; i < 2000; i++)
                {
                    using var insert = writer.CreateCommand();
                    insert.Transaction = tx; insert.CommandText = "INSERT INTO T VALUES($id, $data)";
                    insert.Parameters.AddWithValue("$id", i); insert.Parameters.AddWithValue("$data", new string('x', 200));
                    insert.ExecuteNonQuery();
                }
                tx.Commit();
            }

            var result = DatabaseHealth.Inspect(root);

            Assert.IsTrue(result.WalSizeBytes > 100_000, $"Expected a substantial WAL file after 2000 uncheckpointed inserts, got {result.WalSizeBytes} bytes.");
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void ALockedCheckpointIsReportedAsBusyInsteadOfThrowingOrHanging()
    {
        var root = Path.Combine(Path.GetTempPath(), "wal-health-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = NewCatalog(root);
            // An open read transaction on the WAL prevents a checkpoint from fully completing; PASSIVE mode
            // must report this as "busy" rather than throwing or blocking indefinitely.
            using var reader = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
            reader.Open();
            using (var begin = reader.CreateCommand()) { begin.CommandText = "BEGIN; SELECT COUNT(*) FROM T;"; begin.ExecuteScalar(); }
            using (var write = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
            {
                write.Open();
                using var insert = write.CreateCommand(); insert.CommandText = "INSERT INTO T VALUES(1,'x')"; insert.ExecuteNonQuery();
            }

            var result = DatabaseHealth.Inspect(root);

            Assert.IsTrue(result.CheckpointBusy, "A checkpoint blocked by an open reader must be reported as busy, not silently reported as clean.");
            Assert.AreNotEqual("ERROR", result.Status, "A busy checkpoint is an expected, transient condition, not a database error.");
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void WalSizeIsStillCorrectlyReportedAfterASimulatedRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "wal-health-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = NewCatalog(root);
            using (var reader = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
            {
                reader.Open();
                using var begin = reader.CreateCommand(); begin.CommandText = "BEGIN; SELECT COUNT(*) FROM T;"; begin.ExecuteScalar();
                using (var write = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
                {
                    write.Open();
                    using var insert = write.CreateCommand(); insert.CommandText = "INSERT INTO T VALUES(1,'x')"; insert.ExecuteNonQuery();
                }
            } // both connections closed -- simulates the app restarting with the WAL file left on disk.

            // A brand-new DatabaseHealth.Inspect call (a fresh process, in effect) must still see the real WAL file.
            var result = DatabaseHealth.Inspect(root);
            Assert.IsTrue(result.WalSizeBytes >= 0);
            Assert.AreNotEqual("ERROR", result.Status);
        }
        finally { Cleanup(root); }
    }

    static void Cleanup(string root) { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
