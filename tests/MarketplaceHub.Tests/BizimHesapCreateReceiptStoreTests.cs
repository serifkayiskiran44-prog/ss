using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class BizimHesapCreateReceiptStoreTests
{
    [TestMethod]
    public void Sent_or_uncertain_attempt_blocks_duplicate_but_rejected_attempt_can_retry()
    {
        var root = Path.Combine(Path.GetTempPath(), "bizimhesap-receipts-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new BizimHesapCreateReceiptStore(root);
            store.Begin("account", "product", "payload", DateTimeOffset.UtcNow);
            Assert.IsTrue(store.HasBlockingAttempt("account", "product", "payload"));
            store.Complete("account", "product", "payload", BizimHesapCreateAttemptStatus.Rejected, "", "API reddetti", DateTimeOffset.UtcNow);
            Assert.IsFalse(store.HasBlockingAttempt("account", "product", "payload"));
            store.Begin("account", "product", "payload", DateTimeOffset.UtcNow);
            store.Complete("account", "product", "payload", BizimHesapCreateAttemptStatus.NeedsReconciliation, "", "Okuma bekliyor", DateTimeOffset.UtcNow);
            Assert.IsTrue(store.HasBlockingAttempt("account", "product", "payload"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
