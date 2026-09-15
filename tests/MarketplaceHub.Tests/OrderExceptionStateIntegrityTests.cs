using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

/// Coverage for #2661: OrderExceptionStore.Severity/Status must be a closed,
/// validated set on write and read - never free text, never silently trusted
/// when corrupt - and SetStatus must support a compare-and-swap guard so a
/// concurrent decision race can't silently clobber another decision.
[TestClass]
public sealed class OrderExceptionStateIntegrityTests
{
    static string NewRoot() => Path.Combine(Path.GetTempPath(), "orderexception-state-" + Guid.NewGuid().ToString("N"));

    static void WithRoot(Action<string> test)
    {
        var root = NewRoot();
        try { test(root); }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    static void InsertRaw(string root, string id, string severity, string status)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "order-exceptions.db") }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO OrderExceptions(Id,Marketplace,ShopId,OrderId,Type,EventKey,Severity,Message,Status,CreatedUtc,UpdatedUtc) VALUES($id,'etsy','shop1','o1','MissingSku',$event,$severity,'msg',$status,$created,$updated)";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$event", "item:" + id); cmd.Parameters.AddWithValue("$severity", severity); cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O")); cmd.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    static OrderExceptionRecord Record(string order = "o1") => new() { Marketplace = "etsy", ShopId = "shop1", OrderId = order, Type = "MissingSku", EventKey = "item:x", Message = "SKU eksik." };

    [TestMethod]
    public void AllSupportedSeverityAndStatusValuesRoundTrip() => WithRoot(root =>
    {
        var store = new OrderExceptionStore(root);
        var n = 0;
        foreach (var severity in OrderExceptionStore.Severities)
            foreach (var status in OrderExceptionStore.Statuses)
            {
                var record = Record("o" + n++); record.Severity = severity; record.Status = status;
                var saved = store.Save(record);
                Assert.AreEqual(severity, saved.Severity);
                Assert.AreEqual(status, saved.Status);
            }
        Assert.AreEqual(0, store.CorruptExceptions().Count);
    });

    [TestMethod]
    public void SaveRejectsUnsupportedSeverityBeforeAnyMutation() => WithRoot(root =>
    {
        var store = new OrderExceptionStore(root);
        var record = Record(); record.Severity = "Urgent";
        Assert.ThrowsException<ArgumentException>(() => store.Save(record));
        Assert.AreEqual(0, store.List().Count);
    });

    [TestMethod]
    public void SaveRejectsUnsupportedStatusBeforeAnyMutation() => WithRoot(root =>
    {
        var store = new OrderExceptionStore(root);
        var record = Record(); record.Status = "Ignored";
        Assert.ThrowsException<ArgumentException>(() => store.Save(record));
        Assert.AreEqual(0, store.List().Count);
    });

    [TestMethod]
    public void SetStatusRejectsUnsupportedTargetAndLeavesRowUnchanged() => WithRoot(root =>
    {
        var store = new OrderExceptionStore(root);
        var saved = store.Save(Record());
        Assert.ThrowsException<ArgumentException>(() => store.SetStatus(saved.Id, "Archived"));
        Assert.AreEqual("Pending", store.Find(saved.Id)!.Status);
    });

    [DataTestMethod]
    [DataRow("critical")]
    [DataRow("CRITICAL")]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("Urgent")]
    public void UnrecognizedOrWronglyCasedSeverityIsQuarantined(string rawSeverity) => WithRoot(root =>
    {
        var store = new OrderExceptionStore(root);
        InsertRaw(root, "bad1", rawSeverity, "Pending");
        Assert.AreEqual(0, store.List().Count);
        Assert.AreEqual(1, store.CorruptExceptions().Count);
    });

    [DataTestMethod]
    [DataRow("pending")]
    [DataRow("Approved")]
    [DataRow("")]
    public void UnrecognizedOrWronglyCasedStatusIsQuarantined(string rawStatus) => WithRoot(root =>
    {
        var store = new OrderExceptionStore(root);
        InsertRaw(root, "bad1", "Warning", rawStatus);
        Assert.AreEqual(0, store.List().Count);
        Assert.AreEqual(1, store.CorruptExceptions().Count);
    });

    [TestMethod]
    public void CorruptRowDoesNotHideHealthyRowsForTheSameOrder() => WithRoot(root =>
    {
        var store = new OrderExceptionStore(root);
        store.Save(Record());
        InsertRaw(root, "bad1", "Unknown", "Pending");
        Assert.AreEqual(1, store.List().Count);
        Assert.AreEqual(1, store.CorruptExceptions().Count);
    });

    [TestMethod]
    public void ValidSeverityWithCorruptStatusAndViceVersaAreBothQuarantined() => WithRoot(root =>
    {
        var store = new OrderExceptionStore(root);
        InsertRaw(root, "bad-status", "Warning", "Unknown");
        InsertRaw(root, "bad-severity", "Unknown", "Pending");
        Assert.AreEqual(0, store.List().Count);
        Assert.AreEqual(2, store.CorruptExceptions().Count);
    });

    [TestMethod]
    public void ConcurrentDecisionRaceCannotClobberAnAlreadyRejectedException() => WithRoot(root =>
    {
        var store = new OrderExceptionStore(root);
        var saved = store.Save(Record());
        store.SetStatus(saved.Id, "Pending", "Rejected"); // first decision wins
        // A second, stale decision (still believing the row is Pending) must be
        // rejected rather than silently overwriting the first decision's outcome.
        Assert.ThrowsException<InvalidOperationException>(() => store.SetStatus(saved.Id, "Pending", "Resolved"));
        Assert.AreEqual("Rejected", store.Find(saved.Id)!.Status);
    });

    [TestMethod]
    public void SetStatusWithoutExpectedStatusStillWorksForBackwardCompatibility() => WithRoot(root =>
    {
        var store = new OrderExceptionStore(root);
        var saved = store.Save(Record());
        store.SetStatus(saved.Id, "Resolved");
        Assert.AreEqual("Resolved", store.Find(saved.Id)!.Status);
    });

    [TestMethod]
    public void RestartKeepsSeverityStatusCorruptionDeterministic() => WithRoot(root =>
    {
        _ = new OrderExceptionStore(root);
        InsertRaw(root, "bad1", "Unknown", "Pending");
        var reopened = new OrderExceptionStore(root);
        Assert.AreEqual(0, reopened.List().Count);
        Assert.AreEqual(1, reopened.CorruptExceptions().Count);
    });
}
