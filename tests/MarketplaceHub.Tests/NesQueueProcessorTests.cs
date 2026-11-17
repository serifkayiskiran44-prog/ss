using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class NesQueueProcessorTests
{
    sealed class ManualTimeProvider(DateTimeOffset current) : TimeProvider
    {
        DateTimeOffset value = current;
        public override DateTimeOffset GetUtcNow() => value;
        public void Advance(TimeSpan amount) => value = value.Add(amount);
    }

    sealed class RecordingOperations : INesQueueOperations
    {
        public List<string> Calls { get; } = [];
        public bool ThrowUnknownOnSend { get; set; }
        public CancellationTokenSource? CancelDuringSend { get; set; }
        public int UploadFailuresRemaining { get; set; }
        public int SendCount { get; private set; }
        public int UploadCount { get; private set; }

        public Task<NesPreparedInvoice> PrepareAsync(NesQueueItem item, OrderSnapshot order, CancellationToken token)
        { Calls.Add(item.OrderId + ":prepare"); return Task.FromResult(new NesPreparedInvoice("draft", Encoding.UTF8.GetBytes("<xml/>"), "MBA2026000000001")); }
        public Task<NesUploadResult> SendAsync(NesQueueItem item, OrderSnapshot order, NesPreparedInvoice prepared, CancellationToken token)
        { Calls.Add(item.OrderId + ":send"); SendCount++; if (CancelDuringSend is { } exit) { exit.Cancel(); token.ThrowIfCancellationRequested(); } if (ThrowUnknownOnSend) throw new NesUnknownResultException("Sonuç bilinmiyor"); return Task.FromResult(new NesUploadResult("11111111-1111-1111-1111-111111111111", "EAR2026000000001", null)); }
        public Task<NesMatchedInvoice?> FindRemoteAsync(NesQueueItem item, CancellationToken token)
        { Calls.Add(item.OrderId + ":match"); return Task.FromResult<NesMatchedInvoice?>(new("11111111-1111-1111-1111-111111111111", "EAR2026000000001", "Approved", DateTimeOffset.Parse("2026-09-21T10:00:00Z"))); }
        public Task<NesDownloadedInvoice> DownloadAsync(NesQueueItem item, CancellationToken token)
        { Calls.Add(item.OrderId + ":download"); return Task.FromResult(new NesDownloadedInvoice("invoice.pdf", "SHA", Encoding.ASCII.GetBytes("%PDF-test"))); }
        public Task UploadMarketplaceAsync(NesQueueItem item, NesDownloadedInvoice pdf, CancellationToken token)
        { Calls.Add(item.OrderId + ":upload"); UploadCount++; if (UploadFailuresRemaining-- > 0) throw new NesQueueHttpException(503, "Geçici hata"); return Task.CompletedTask; }
    }

    [TestMethod]
    public async Task ProcessorCompletesStepsInOrderAndThenClaimsSecondOrder()
    {
        using var temp = new TemporaryDirectory();
        var queue = Seed(temp.Path, "1", "2");
        var operations = new RecordingOperations();
        var processor = Processor(temp.Path, queue, operations, TimeProvider.System);

        while (await processor.ProcessNextAsync(CancellationToken.None)) { }

        CollectionAssert.AreEqual(new[] { "1:prepare", "1:send", "1:match", "1:download", "1:upload", "2:prepare", "2:send", "2:match", "2:download", "2:upload" }, operations.Calls);
        Assert.IsTrue(queue.List().All(x => x.State == NesQueueState.Completed));
    }

    [TestMethod]
    public async Task UnknownSendResultIsNeverAutomaticallySentAgain()
    {
        using var temp = new TemporaryDirectory();
        var queue = Seed(temp.Path, "1");
        var operations = new RecordingOperations { ThrowUnknownOnSend = true };
        var processor = Processor(temp.Path, queue, operations, TimeProvider.System);

        while (await processor.ProcessNextAsync(CancellationToken.None)) { }
        Assert.AreEqual(NesQueueState.Unknown, queue.List().Single().State);
        Assert.IsFalse(await processor.ProcessNextAsync(CancellationToken.None));
        Assert.AreEqual(1, operations.SendCount);
    }

    /// Exit or log-off while the invoice POST is in flight: NES may already have accepted it, so it must never be sent again on its own.
    [TestMethod]
    public async Task ExitDuringTheInvoiceSendLeavesTheItemUnknownAndNeverSendsItAgain()
    {
        using var temp = new TemporaryDirectory();
        var queue = Seed(temp.Path, "1");
        using var exit = new CancellationTokenSource();
        var operations = new RecordingOperations { CancelDuringSend = exit };
        var processor = Processor(temp.Path, queue, operations, TimeProvider.System);

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(async () => { while (await processor.ProcessNextAsync(exit.Token)) { } });

        Assert.AreEqual(NesQueueState.Unknown, queue.List().Single().State);
        operations.CancelDuringSend = null;
        Assert.IsFalse(await processor.ProcessNextAsync(CancellationToken.None), "The next launch must not pick the item up by itself.");
        Assert.AreEqual(1, operations.SendCount);
    }

    [TestMethod]
    public async Task ExitBeforeTheInvoiceSendStartsOnlyReleasesTheItem()
    {
        using var temp = new TemporaryDirectory();
        var queue = Seed(temp.Path, "1");
        var operations = new RecordingOperations();
        var processor = Processor(temp.Path, queue, operations, TimeProvider.System);
        await processor.ProcessNextAsync(CancellationToken.None);                       // prepare -> the item now waits at the send step
        using var exit = new CancellationTokenSource();
        exit.Cancel();

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => processor.ProcessNextAsync(exit.Token));

        Assert.AreEqual(0, operations.SendCount);
        while (await processor.ProcessNextAsync(CancellationToken.None)) { }
        Assert.AreEqual(1, operations.SendCount);
        Assert.AreEqual(NesQueueState.Completed, queue.List().Single().State);
    }

    [TestMethod]
    public async Task MarketplaceUploadFailureRetriesUploadWithoutSendingAnotherInvoice()
    {
        using var temp = new TemporaryDirectory();
        var queue = Seed(temp.Path, "1");
        var operations = new RecordingOperations { UploadFailuresRemaining = 1 };
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-21T10:00:00Z"));
        var processor = Processor(temp.Path, queue, operations, clock);

        while (await processor.ProcessNextAsync(CancellationToken.None) && queue.List().Single().State != NesQueueState.RetryWaiting) { }
        clock.Advance(TimeSpan.FromMinutes(2));
        while (await processor.ProcessNextAsync(CancellationToken.None)) { }

        Assert.AreEqual(1, operations.SendCount);
        Assert.AreEqual(2, operations.UploadCount);
        Assert.AreEqual(NesQueueState.Completed, queue.List().Single().State);
    }

    static NesQueueProcessor Processor(string directory, NesInvoiceQueueStore queue, INesQueueOperations operations, TimeProvider clock) =>
        new(queue, new OrdersStore(directory), new AuditStore(directory), operations, clock);

    static NesInvoiceQueueStore Seed(string directory, params string[] ids)
    {
        var orders = new OrdersStore(directory);
        var queue = new NesInvoiceQueueStore(directory);
        var index = 0;
        foreach (var id in ids)
        {
            orders.SaveBatch([new OrderSnapshot { Marketplace = "Trendyol", ShopId = "411150", OrderId = id, RawStatus = "Created", Currency = "TRY", BillingName = "Alıcı", BillingAddress = "Adres", Items = [new() { Title = "Ürün", Sku = "SKU", Quantity = 1, UnitPrice = 120m, VatRate = 20m }], Shipments = [new() { Id = (1000 + int.Parse(id)).ToString(), State = "Preparing" }] }]);
            queue.Enqueue(new("Trendyol", "411150", id, (1000 + int.Parse(id)).ToString(), NesDocumentKind.EArchive, true, "", "hash-" + id), DateTimeOffset.Parse("2026-09-21T10:00:00Z").AddSeconds(index++));
        }
        return queue;
    }
}
