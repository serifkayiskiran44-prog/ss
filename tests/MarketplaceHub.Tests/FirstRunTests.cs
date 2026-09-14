using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Simulates a completely empty Windows profile: no LocalAppData app folder, no DB
/// files, no credentials. Every local store must initialize its own empty schema and
/// read back empty results without throwing or making a network call.
[TestClass]
public sealed class FirstRunTests
{
    [TestMethod]
    public void AllLocalStoresInitializeCleanlyOnAnEmptyProfileDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "first-run-" + Guid.NewGuid().ToString("N"));
        Assert.IsFalse(Directory.Exists(root), "Precondition: directory must not exist yet, like a fresh profile.");
        try
        {
            var catalog = new CatalogStore(root);
            Assert.AreEqual(0, catalog.Products().Count);
            Assert.AreEqual(0, catalog.Sources().Count);

            var sync = new SyncStore(root);
            Assert.IsNotNull(sync);

            var orders = new OrdersStore(root);
            Assert.IsNotNull(orders);

            var media = new MediaStore(root);
            Assert.AreEqual(0, media.List().Count);

            var connections = new MarketplaceConnectionStore(root);
            Assert.IsNotNull(connections);

            var xmlRuns = new XmlRunStore(root);
            Assert.IsNotNull(xmlRuns);

            var automation = new AutomationStore(root);
            Assert.IsNotNull(automation);

            var audit = new AuditStore(root);
            Assert.AreEqual(0, audit.List().Count);

            var messages = new MessageStore(root);
            Assert.IsNotNull(messages);

            var exceptions = new OrderExceptionStore(root);
            Assert.IsNotNull(exceptions);

            var apiHealth = new ApiHealthStore(root);
            Assert.IsNotNull(apiHealth);

            var quality = new DataQualityStore(root);
            Assert.IsNotNull(quality);

            var search = new GlobalSearchIndexService(root);
            Assert.IsNotNull(search);

            var startupRecovery = new StartupRecovery(root);
            Assert.IsFalse(startupRecovery.State.UncleanExit, "A brand new profile has no prior unclean exit marker.");

            var crashGuard = new StartupCrashGuard(root);
            Assert.IsFalse(crashGuard.RecoveryModeRequired);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void ReopeningAnEmptyProfileWithoutOnboardingStaysSafe()
    {
        var root = Path.Combine(Path.GetTempPath(), "first-run-reopen-" + Guid.NewGuid().ToString("N"));
        try
        {
            _ = new CatalogStore(root);
            _ = new CatalogStore(root);
            var catalog = new CatalogStore(root);
            Assert.AreEqual(0, catalog.Products().Count);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
