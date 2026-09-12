using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #694 (SOURCE HEALTH: Feed fingerprint ve değişim oranı alarmı): re-verifies, against the real
// CatalogStore.Import path (not the standalone DropshipAnomalyGuard class, which SafetyAndStabilityTests.cs
// already covers on its own), that the previously-audited #283 wiring (CatalogStore.Import ->
// EnsureDropshipFeedSafe -> DropshipAnomalyGuard.Evaluate) actually catches mass deletion and mass addition,
// and that its baseline survives an app restart. EnsureDropshipFeedSafe derives "previous" from a live query
// of the persisted catalog (Products().Where(SourceId==...)), not an in-memory profile, so a fresh
// CatalogStore instance against the same directory (simulating restart) should see the same baseline.
[TestClass]
public sealed class FeedAnomalyRealImportPathTests
{
    static IReadOnlyList<CatalogProduct> Seed(string sourceId, int count) =>
        Enumerable.Range(1, count).Select(i => new CatalogProduct { SourceId = sourceId, Sku = "SKU-" + i, Name = "Product " + i, Price = 10m, Stock = 5, Active = true }).ToList();

    [TestMethod]
    public void UnchangedFeedReimportedIsNotTreatedAsAnAnomaly()
    {
        var root = Path.Combine(Path.GetTempPath(), "feed-anomaly-" + Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = new CatalogStore(root);
            var source = new XmlSource { Id = "supplier-1", Name = "Supplier" };
            var products = Seed(source.Id, 20);
            catalog.Import(source, products); // first import: no previous baseline, cannot be anomalous.

            catalog.Import(source, products); // identical re-submission: same count/stock/price -- must not block.

            Assert.AreEqual(20, catalog.Products().Count());
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void ANormalPartialDeltaIsAllowed()
    {
        var root = Path.Combine(Path.GetTempPath(), "feed-anomaly-" + Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = new CatalogStore(root);
            var source = new XmlSource { Id = "supplier-1", Name = "Supplier" };
            catalog.Import(source, Seed(source.Id, 20));

            // 10% drop -- well under the 30% default threshold; import is additive/upsert-only (a normal feed
            // isn't required to be a complete snapshot), so the two products missing from this batch simply
            // stay untouched rather than being deleted -- the assertion here is "not blocked", not "shrank".
            catalog.Import(source, Seed(source.Id, 18));

            Assert.AreEqual(20, catalog.Products().Count(), "A normal partial delta must not be blocked, and import must not delete products absent from a non-complete batch.");
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void MassDeletionThroughTheRealImportPathIsBlockedAndTheCatalogIsUnchanged()
    {
        var root = Path.Combine(Path.GetTempPath(), "feed-anomaly-" + Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = new CatalogStore(root);
            var source = new XmlSource { Id = "supplier-1", Name = "Supplier" };
            catalog.Import(source, Seed(source.Id, 20));

            // 20 -> 5 is a 75% drop, far past the 30% default MaxCountDeltaPercent.
            Assert.ThrowsException<InvalidOperationException>(() => catalog.Import(source, Seed(source.Id, 5)));

            Assert.AreEqual(20, catalog.Products().Count(), "A blocked import must leave the previously-committed catalog completely unchanged.");
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void MassAdditionThroughTheRealImportPathIsBlockedAndTheCatalogIsUnchanged()
    {
        var root = Path.Combine(Path.GetTempPath(), "feed-anomaly-" + Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = new CatalogStore(root);
            var source = new XmlSource { Id = "supplier-1", Name = "Supplier" };
            catalog.Import(source, Seed(source.Id, 20));

            // 20 -> 60 is a 200% jump, far past the 30% default MaxCountDeltaPercent -- a scraped/corrupted
            // feed duplicating itself must not be silently accepted as legitimate growth.
            Assert.ThrowsException<InvalidOperationException>(() => catalog.Import(source, Seed(source.Id, 60)));

            Assert.AreEqual(20, catalog.Products().Count());
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void TheAnomalyBaselineSurvivesAnAppRestartInsteadOfResettingToAnEmptyCatalog()
    {
        var root = Path.Combine(Path.GetTempPath(), "feed-anomaly-" + Guid.NewGuid().ToString("N"));
        try
        {
            var firstProcess = new CatalogStore(root);
            var source = new XmlSource { Id = "supplier-1", Name = "Supplier" };
            firstProcess.Import(source, Seed(source.Id, 20));

            // A brand-new CatalogStore instance against the same directory simulates the app restarting;
            // if the guard's baseline reset to "no previous feed" on restart, a mass-deletion feed would be
            // wrongly accepted as a legitimate "first import" instead of being compared against the 20 that
            // are actually already on disk.
            var afterRestart = new CatalogStore(root);
            Assert.ThrowsException<InvalidOperationException>(() => afterRestart.Import(source, Seed(source.Id, 3)));
            Assert.AreEqual(20, afterRestart.Products().Count(), "The pre-restart catalog must still be the comparison baseline, not reset by the new process.");
        }
        finally { Cleanup(root); }
    }

    static void Cleanup(string root) { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
