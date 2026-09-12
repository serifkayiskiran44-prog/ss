using System;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #779 (DB: Busy/locked database retry policy). SqliteConnectionPolicy already sets PRAGMA busy_timeout on
// every connection -- SQLite's own engine already waits/retries internally for that window before ever
// throwing. What was missing: once that window genuinely expires (a persistent lock), the resulting
// SqliteException fell into MainWindow.Safe's generic "İşlem tamamlanamadı" bucket, indistinguishable from
// a corrupt file or a permissions error -- giving the user no signal that simply retrying might work.
[TestClass]
public sealed class SqliteBusyDiagnosticsTests
{
    [TestMethod]
    public void ARealSqliteBusyExceptionIsRecognized()
    {
        var path = Path.Combine(Path.GetTempPath(), "busy-diag-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var holder = new SqliteConnection($"Data Source={path}"); holder.Open();
            using (var create = holder.CreateCommand()) { create.CommandText = "CREATE TABLE T(Id INTEGER)"; create.ExecuteNonQuery(); }
            using var holderTx = holder.BeginTransaction();
            using (var lockCmd = holder.CreateCommand()) { lockCmd.Transaction = holderTx; lockCmd.CommandText = "INSERT INTO T VALUES(1)"; lockCmd.ExecuteNonQuery(); }
            // holderTx is left open (uncommitted) with a reserved write lock on the file.

            // Microsoft.Data.Sqlite applies its own "Default Timeout" connection-string property (30s by
            // default) to sqlite3_busy_timeout internally, which silently overrides a PRAGMA busy_timeout
            // issued after Open() -- and DefaultTimeout=0 means "wait forever" (ADO.NET CommandTimeout
            // convention), not "fail immediately". DefaultTimeout=1 keeps this test fast and deterministic
            // instead of the real app's 15s policy.
            using var contender = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 1 }.ToString());
            contender.Open();

            SqliteException? caught = null;
            try { using var cmd = contender.CreateCommand(); cmd.CommandText = "INSERT INTO T VALUES(2)"; cmd.ExecuteNonQuery(); }
            catch (SqliteException ex) { caught = ex; }

            Assert.IsNotNull(caught, "Expected a genuine SQLITE_BUSY/LOCKED exception from the contending connection.");
            Assert.IsTrue(SqliteBusyDiagnostics.IsBusyOrLocked(caught!));
            StringAssert.Contains(SqliteBusyDiagnostics.Describe(caught!), "kullanılıyor");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public void AnUnrelatedSqliteExceptionIsNotMisclassifiedAsBusy()
    {
        var path = Path.Combine(Path.GetTempPath(), "busy-diag-notfound-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var connection = new SqliteConnection($"Data Source={path}"); connection.Open();
            SqliteException? caught = null;
            try { using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT * FROM NoSuchTable"; cmd.ExecuteScalar(); }
            catch (SqliteException ex) { caught = ex; }

            Assert.IsNotNull(caught);
            Assert.IsFalse(SqliteBusyDiagnostics.IsBusyOrLocked(caught!));
        }
        finally { SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }

    [TestMethod]
    public void ANonSqliteExceptionIsNeverMisclassifiedAsBusy()
    {
        Assert.IsFalse(SqliteBusyDiagnostics.IsBusyOrLocked(new InvalidOperationException("unrelated")));
    }
}
