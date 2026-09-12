using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class WorkerStateStoreTests
{
    [TestMethod]
    public void WorkerStateHasSingleCursorAndRejectsStaleOwnerAdvance()
    {
        var root = Path.Combine(Path.GetTempPath(), "marketplacehub-worker-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new WorkerStateStore(root);
            Assert.AreEqual("281", store.Get().Cursor);
            Assert.IsTrue(store.TryClaim("owner-a", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1)));
            Assert.IsFalse(store.TryClaim("owner-b", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1)));
            Assert.ThrowsException<InvalidOperationException>(() => store.Advance("owner-b", "282", "tests=1;publish=verified"));
            store.Advance("owner-a", "282", "tests=1;publish=verified");
            Assert.AreEqual("282", store.Get().Cursor);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
