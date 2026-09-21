using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class NesQueueWorkspaceTests
{
    [TestMethod]
    public void BatchPreviewEnablesApprovalOnlyWhenAtLeastOneRowIsReady()
    {
        var request = new NesQueueRequest("Trendyol", "411150", "2", "20", NesDocumentKind.EArchive, true, "", "hash-2");
        var blocked = new NesQueuePlan([new("1", "Blocked", "VKN eksik", null)]);
        var mixed = new NesQueuePlan([new("1", "Blocked", "VKN eksik", null), new("2", "Ready", "Hazır", request)]);
        Assert.IsFalse(NesBatchInvoiceViewState.From(blocked).CanApprove);
        Assert.IsTrue(NesBatchInvoiceViewState.From(mixed).CanApprove);
        Assert.AreEqual(1, NesBatchInvoiceViewState.From(mixed).ReadyCount);
    }

    [TestMethod]
    public void RetrySelectionIncludesOnlyRetryWaitingRows()
    {
        var selected = NesQueueSelectionPolicy.Retryable([
            Item("1", NesQueueState.RetryWaiting), Item("2", NesQueueState.Unknown), Item("3", NesQueueState.Blocked)]);
        CollectionAssert.AreEqual(new[] { "1" }, selected.Select(x => x.Id).ToArray());
    }

    static NesQueueItem Item(string id, NesQueueState state) => new() { Id = id, Marketplace = "Trendyol", ShopId = "411150", OrderId = id, State = state };
}
