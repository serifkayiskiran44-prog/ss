using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

/// Coverage for #2613: a connection test result must only apply if the
/// connection is still enabled and still at the exact revision observed
/// when the test started - a late result from before a disable/reconfigure
/// must never revive or overwrite the current state.
[TestClass]
public sealed class ConnectionTestFenceTests
{
    static MarketplaceConnectionStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "connection-fence-" + Guid.NewGuid().ToString("N"));
        return new MarketplaceConnectionStore(root);
    }

    [TestMethod]
    public void NormalTestAppliesWhenRevisionMatchesAndEnabled()
    {
        var store = NewStore(out var root);
        try
        {
            var saved = store.Save("etsy", "shop1", "Etsy Mağazam", true);
            var outcome = store.RecordTest(saved.Id, saved.Revision, true);
            Assert.AreEqual(ConnectionTestApplyResult.Applied, outcome);
            Assert.AreEqual("CONNECTED_READ_ONLY", store.Get(saved.Id)!.Status);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void TestStartedThenDisabledThenSuccessNeverRevivesConnection()
    {
        var store = NewStore(out var root);
        try
        {
            var saved = store.Save("etsy", "shop1", "Etsy Mağazam", true);
            var testedRevision = saved.Revision;

            store.SetEnabled(saved.Id, false);

            var outcome = store.RecordTest(saved.Id, testedRevision, true);
            Assert.AreEqual(ConnectionTestApplyResult.Disabled, outcome);
            Assert.AreEqual("DISABLED", store.Get(saved.Id)!.Status);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void TestStartedThenMetadataChangedRejectsStaleResult()
    {
        var store = NewStore(out var root);
        try
        {
            var saved = store.Save("etsy", "shop1", "Etsy Mağazam", true);
            var testedRevision = saved.Revision;

            store.Save("etsy", "shop1", "Yeni Ad", true, saved.Id);

            var outcome = store.RecordTest(saved.Id, testedRevision, true);
            Assert.AreEqual(ConnectionTestApplyResult.Stale, outcome);
            Assert.AreEqual("NOT_CONFIGURED", store.Get(saved.Id)!.Status);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void OnlyTheNewestOfTwoParallelTestsIsAccepted()
    {
        var store = NewStore(out var root);
        try
        {
            var saved = store.Save("etsy", "shop1", "Etsy Mağazam", true);
            var firstProbeRevision = saved.Revision;

            // A reconfigure happens between the two probes starting and finishing.
            var reconfigured = store.Save("etsy", "shop1", "Etsy Mağazam v2", true, saved.Id);
            var secondProbeRevision = reconfigured.Revision;

            var staleOutcome = store.RecordTest(saved.Id, firstProbeRevision, true);
            var freshOutcome = store.RecordTest(saved.Id, secondProbeRevision, true);

            Assert.AreEqual(ConnectionTestApplyResult.Stale, staleOutcome);
            Assert.AreEqual(ConnectionTestApplyResult.Applied, freshOutcome);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void UnknownConnectionIdReturnsNotFound()
    {
        var store = NewStore(out var root);
        try
        {
            var outcome = store.RecordTest("does-not-exist", 1, true);
            Assert.AreEqual(ConnectionTestApplyResult.NotFound, outcome);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void FailedTestStillAppliesWhenRevisionMatchesAndEnabled()
    {
        var store = NewStore(out var root);
        try
        {
            var saved = store.Save("etsy", "shop1", "Etsy Mağazam", true);
            var outcome = store.RecordTest(saved.Id, saved.Revision, false, "boom");
            Assert.AreEqual(ConnectionTestApplyResult.Applied, outcome);
            var current = store.Get(saved.Id)!;
            Assert.AreEqual("FAILED", current.Status);
            Assert.AreEqual("boom", current.LastError);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RevisionIncrementsOnEachSaveAndSetEnabled()
    {
        var store = NewStore(out var root);
        try
        {
            var saved = store.Save("etsy", "shop1", "Name1", true);
            var afterRename = store.Save("etsy", "shop1", "Name2", true, saved.Id);
            Assert.IsTrue(afterRename.Revision > saved.Revision);

            store.SetEnabled(saved.Id, false);
            var afterDisable = store.Get(saved.Id)!;
            Assert.IsTrue(afterDisable.Revision > afterRename.Revision);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void EnabledThenDisabledThenReenabledRequiresFreshRevisionToApplyTest()
    {
        var store = NewStore(out var root);
        try
        {
            var saved = store.Save("etsy", "shop1", "Etsy Mağazam", true);
            var oldRevision = saved.Revision;

            store.SetEnabled(saved.Id, false);
            store.SetEnabled(saved.Id, true);

            // Even though it's enabled again, the revision moved on - the old
            // in-flight probe's result must still be rejected as stale.
            var outcome = store.RecordTest(saved.Id, oldRevision, true);
            Assert.AreEqual(ConnectionTestApplyResult.Stale, outcome);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
