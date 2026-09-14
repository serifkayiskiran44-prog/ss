using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #2564 (FIRST RUN: safe cold start and empty-state workspace on a completely fresh profile). Every store the app
// touches at startup must create its own schema from nothing without seeding fake data, every empty query must
// return an empty result rather than throw, a connector with no credentials must read as NOT_CONFIGURED rather than
// an error, closing and reopening without any onboarding must reach the exact same safe state, and a corrupted
// database file must fail loudly at open rather than being silently treated as a healthy empty store.
[TestClass]
public sealed class ColdStartTests
{
    [TestMethod]
    public void EveryMajorStoreOpensCleanOnACompletelyFreshProfileWithEmptyReadsNoSeedDataAndConnectorsReadNotConfigured()
    {
        var root = Path.Combine(Path.GetTempPath(), "cold-start-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.IsFalse(Directory.Exists(root), "the fixture folder must not exist yet -- this stands in for a clean profile with no app-data folder at all");

            // Opening every major store on the fresh profile must succeed without throwing and without any network I/O
            // (none of these constructors touch the network -- they are pure local file/SQLite operations), and each
            // must create only its own schema, never seed user-visible data.
            var catalog = new CatalogStore(root);
            var orders = new OrdersStore(root);
            var connections = new MarketplaceConnectionStore(root);
            var apiHealth = new ApiHealthStore(root);
            var xmlRuns = new XmlRunStore(root);
            var notifications = new NotificationStore(root);
            var audit = new AuditStore(root);
            Assert.IsTrue(Directory.Exists(root), "the store constructors themselves create the app-data folder -- never left for a person to create by hand");

            // Every empty-workspace read returns an empty collection, never throws and never a seeded row.
            Assert.AreEqual(0, catalog.Products().Count); Assert.AreEqual(0, catalog.Sources().Count);
            Assert.AreEqual(0, orders.ReadAll().Count);
            Assert.AreEqual(0, xmlRuns.List(null, 100).Count);
            Assert.AreEqual(0, notifications.List().Count);
            Assert.AreEqual(0, audit.List(100).Count);

            // A connector with zero credentials configured reads as NOT_CONFIGURED -- never an error state -- and the
            // health store agrees once the dashboard's own EnsureConnection call has run (as it does on every real refresh).
            var defaults = connections.List(includeDefaults: true);
            Assert.IsTrue(defaults.Count > 0, "the catalog of known marketplaces is shown even with nothing configured");
            Assert.IsTrue(defaults.All(c => c.Status == "NOT_CONFIGURED"), "an unconfigured connector is NOT_CONFIGURED, never an error, before any credential is entered");
            foreach (var connection in defaults) apiHealth.EnsureConnection(connection.Channel, connection.ShopId, connection.Status, connection.LastError);
            Assert.IsTrue(apiHealth.List().All(h => h.State == "NOT_CONFIGURED"));
            Assert.AreEqual(0, apiHealth.Summary().RateLimited + apiHealth.Summary().AuthErrors, "nothing looks like an auth or rate-limit failure with zero credentials");

            // Close and reopen without any onboarding step: the exact same safe, empty state is preserved -- no fresh seed, no duplicate defaults.
            SqliteConnection.ClearAllPools();
            var reopenedCatalog = new CatalogStore(root); var reopenedConnections = new MarketplaceConnectionStore(root);
            Assert.AreEqual(0, reopenedCatalog.Products().Count);
            Assert.AreEqual(defaults.Count, reopenedConnections.List().Count, "reopening never duplicates the default connector rows");

            // Adding the first real piece of data deterministically moves the workspace out of the empty state -- no reinstall needed.
            var source = new XmlSource { Id = "src", Name = "İlk kaynak", ItemPath = "/p", Fields = new Dictionary<string, string> { ["Sku"] = "s", ["Name"] = "n" }, IntervalMinutes = 30 };
            reopenedCatalog.SaveSource(source);
            Assert.AreEqual(1, new CatalogStore(root).Sources().Count, "the first added source is there on the very next open, still with no onboarding step required");
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void ACorruptedDatabaseFileFailsLoudlyAtOpenInsteadOfBeingSilentlyTreatedAsAHealthyEmptyStore()
    {
        var root = Path.Combine(Path.GetTempPath(), "cold-start-corrupt-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            // A file that exists at the exact path CatalogStore expects but is not a database at all -- the shape a
            // half-written first-run schema-create left behind by a crash, or a disk-full truncation, would take.
            File.WriteAllBytes(Path.Combine(root, "catalog.db"), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            var opened = Assert.ThrowsException<SqliteException>(() => new CatalogStore(root));
            StringAssert.Contains(opened.Message.ToLowerInvariant(), "not a database"); // SQLite's own SQLITE_NOTADB -- never silently treated as an empty, healthy catalog
        }
        finally { Cleanup(root); }
    }

    static void Cleanup(string root)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
            catch (IOException) { Thread.Sleep(300); }
            catch (UnauthorizedAccessException) { Thread.Sleep(300); }
        }
    }
}
