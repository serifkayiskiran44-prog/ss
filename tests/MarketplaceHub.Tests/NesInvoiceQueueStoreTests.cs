using System;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class NesInvoiceQueueStoreTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-21T10:00:00Z");

    static NesQueueRequest Request(string orderId = "11600461863") => new(
        "Trendyol", "411150", orderId, "4154085881", NesDocumentKind.EArchive,
        false, "", "order-hash");

    [TestMethod]
    public void DuplicateOrderIsEnqueuedOnlyOnce()
    {
        using var temp = new TemporaryDirectory();
        var store = new NesInvoiceQueueStore(temp.Path);

        var first = store.Enqueue(Request(), Now);
        var second = store.Enqueue(Request(), Now.AddMinutes(1));

        Assert.AreEqual(first.Id, second.Id);
        Assert.AreEqual(1, store.List().Count);
    }

    [TestMethod]
    public void OnlyOneWorkerCanClaimTheNextOrder()
    {
        using var temp = new TemporaryDirectory();
        var store = new NesInvoiceQueueStore(temp.Path);
        var item = store.Enqueue(Request("1"), Now);

        Assert.IsTrue(store.TryClaimNext(Now, TimeSpan.FromMinutes(2), out var claimed));
        Assert.IsFalse(store.TryClaimNext(Now, TimeSpan.FromMinutes(2), out _));
        Assert.AreEqual(item.Id, claimed!.Id);
        Assert.IsFalse(string.IsNullOrWhiteSpace(claimed.LeaseToken));
    }

    [TestMethod]
    public void WrongLeaseCannotAdvanceAQueueItem()
    {
        using var temp = new TemporaryDirectory();
        var store = new NesInvoiceQueueStore(temp.Path);
        store.Enqueue(Request("1"), Now);
        store.TryClaimNext(Now, TimeSpan.FromMinutes(2), out var claimed);

        Assert.ThrowsException<InvalidOperationException>(() =>
            store.Advance(claimed!.Id, "wrong-token", NesQueueStep.SendInvoice,
                NesQueueState.Ready, Now));
    }

    [TestMethod]
    public void ExpiredLeaseCanBeClaimedByAnotherWorker()
    {
        using var temp = new TemporaryDirectory();
        var store = new NesInvoiceQueueStore(temp.Path);
        store.Enqueue(Request("1"), Now);
        Assert.IsTrue(store.TryClaimNext(Now, TimeSpan.FromMinutes(2), out var first));

        Assert.IsTrue(store.TryClaimNext(Now.AddMinutes(3), TimeSpan.FromMinutes(2), out var second));
        Assert.AreEqual(first!.Id, second!.Id);
        Assert.AreNotEqual(first.LeaseToken, second.LeaseToken);
    }

    [TestMethod]
    public void ExpiredLeaseAtSendStepRequiresReconciliationInsteadOfResend()
    {
        using var temp = new TemporaryDirectory();
        var store = new NesInvoiceQueueStore(temp.Path);
        store.Enqueue(Request("send-crash"), Now);
        store.TryClaimNext(Now, TimeSpan.FromMinutes(2), out var prepare);
        store.Advance(prepare!.Id, prepare.LeaseToken, NesQueueStep.SendInvoice, NesQueueState.Ready, Now);
        store.TryClaimNext(Now, TimeSpan.FromMinutes(2), out _);

        Assert.IsFalse(store.TryClaimNext(Now.AddMinutes(3), TimeSpan.FromMinutes(2), out _));
        var saved = store.List().Single();
        Assert.AreEqual(NesQueueState.Unknown, saved.State);
        StringAssert.Contains(saved.LastError, "mutabakat");
    }

    [TestMethod]
    public void RetryDelayIsCappedAtTwentyFourHoursAndErrorIsRedacted()
    {
        using var temp = new TemporaryDirectory();
        var store = new NesInvoiceQueueStore(temp.Path);
        store.Enqueue(Request("1"), Now);
        store.TryClaimNext(Now, TimeSpan.FromMinutes(2), out var claimed);

        store.ScheduleRetry(claimed!.Id, claimed.LeaseToken, TimeSpan.FromDays(10),
            "Bearer secret-token failed", Now);

        var saved = store.List().Single();
        Assert.AreEqual(Now.AddHours(24), saved.NextAttemptUtc);
        Assert.AreEqual(NesQueueState.RetryWaiting, saved.State);
        Assert.IsFalse(saved.LastError.Contains("secret-token", StringComparison.Ordinal));
        Assert.AreEqual("", saved.LeaseToken);
    }

    [TestMethod]
    public void MalformedRowsAreSkippedAndReportedWithoutPayload()
    {
        using var temp = new TemporaryDirectory();
        var store = new NesInvoiceQueueStore(temp.Path);
        var good = store.Enqueue(Request("good"), Now);

        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = System.IO.Path.Combine(temp.Path, "nes-invoice-queue.db")
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE nes_invoice_queue SET state=999 WHERE id=$id";
            command.Parameters.AddWithValue("$id", good.Id);
            command.ExecuteNonQuery();
        }

        Assert.AreEqual(0, store.List().Count);
        var corrupt = store.CorruptRows().Single();
        Assert.AreEqual(good.Id, corrupt.Id);
        StringAssert.Contains(corrupt.Reason, "state");
        Assert.IsFalse(corrupt.Reason.Contains("order-hash", StringComparison.Ordinal));
    }
}
