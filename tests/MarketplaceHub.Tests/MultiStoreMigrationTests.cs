using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;
using TrMarketplaceHubDesktop.Etsy;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class MultiStoreMigrationTests
{
    readonly List<string> roots = [];
    sealed class InjectedMigrationFailureException(MultiStoreMigrationCheckpoint checkpoint) : Exception
    {
        public MultiStoreMigrationCheckpoint Checkpoint { get; } = checkpoint;
    }
    sealed class InjectedConnectionSubstepFailureException : Exception;
    sealed class InjectedStageWriteFailureException(MultiStoreMigrationCheckpoint stage) : Exception
    {
        public MultiStoreMigrationCheckpoint Stage { get; } = stage;
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        foreach (var root in roots)
            if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    [TestMethod]
    public void DryRunCountsCopiedLegacyDataWithoutWritingAndConfirmationIsRequired()
    {
        var target = CopyLegacyFixture();
        var before = Snapshot(target);
        var service = new MultiStoreMigrationService(target);

        var report = service.DryRun();

        Assert.AreEqual(3, report.SourceBindings);
        Assert.AreEqual(1, report.OnlineBalances);
        Assert.AreEqual(0, report.PhysicalBalances);
        Assert.AreEqual(1, report.Connections);
        Assert.AreEqual(1, report.ProductBindings);
        Assert.AreEqual(1, report.Orders);
        Assert.AreEqual(1, report.AutomationSettings);
        CollectionAssert.AreEquivalent(before, Snapshot(target), "Dry-run must not create a schema, checkpoint, backup or receipt.");

        Assert.ThrowsException<InvalidOperationException>(() => service.Apply(report, "wrong-confirmation"));
        CollectionAssert.AreEquivalent(before, Snapshot(target), "Rejected confirmation must remain write-free.");
    }

    [TestMethod]
    public void DryRunRejectsAnUncheckpointedWalInsteadOfIgnoringRecentLegacyData()
    {
        var target = CopyLegacyFixture();
        var wal = Path.Combine(target, "catalog.db-wal");
        File.WriteAllBytes(wal, [1, 2, 3, 4]);
        var before = Snapshot(target);

        var error = Assert.ThrowsException<InvalidOperationException>(() => new MultiStoreMigrationService(target).DryRun());

        StringAssert.Contains(error.Message, "WAL");
        CollectionAssert.AreEquivalent(before, Snapshot(target), "Rejected dry-run must not mutate or discard the pending WAL.");
    }

    [TestMethod]
    public void ApplyPreservesLegacyIdentityCreatesReceiptAndIsIdempotentWithoutHttp()
    {
        var target = CopyLegacyFixture();
        var service = new MultiStoreMigrationService(target);
        var report = service.DryRun();
        var legacyCredential = Hash(Path.Combine(target, "credentials.bin"));
        var sourceJson = Scalar(target, "catalog.db", "SELECT Json FROM Sources WHERE Id='xml-a'");
        var productJson = Scalar(target, "catalog.db", "SELECT Json FROM CatalogProducts LIMIT 1");
        var mapping = Scalar(target, "catalog.db", "SELECT ExternalId FROM MarketplaceMappings WHERE Channel='etsy' AND ShopId='303'");
        var orderJson = Scalar(target, "orders.db", "SELECT payload FROM orders WHERE marketplace='manual' AND shop='legacy-shop' AND id='ORD-1'");
        var stockReceiptJson = Scalar(target, "catalog.db", "SELECT Json FROM OrderStockReceipts WHERE Marketplace='etsy' AND ShopId='303' AND OrderId='LEGACY-RECEIPT'");
        var mediaId = Scalar(target, "media.db", "SELECT Id FROM ProductMedia LIMIT 1");
        var mediaUrl = Scalar(target, "media.db", "SELECT Url FROM ProductMedia LIMIT 1");

        var first = service.Apply(report, report.ConfirmationToken);
        var second = new MultiStoreMigrationService(target).Apply(report, report.ConfirmationToken);

        Assert.AreEqual(MultiStoreMigrationCheckpoint.Completed, first.Checkpoint);
        Assert.AreEqual(first.ReceiptId, second.ReceiptId);
        Assert.AreEqual(first.BackupPath, second.BackupPath);
        Assert.IsTrue(File.Exists(first.BackupPath));
        Assert.AreEqual(MultiStoreMigrationService.BackupFormat, new DataBackupService(target).Validate(first.BackupPath).Format);
        Assert.AreEqual(legacyCredential, Hash(Path.Combine(target, "credentials.bin")), "Legacy encrypted credentials must remain byte-for-byte intact.");
        Assert.AreEqual(sourceJson, Scalar(target, "catalog.db", "SELECT Json FROM Sources WHERE Id='xml-a'"));
        Assert.AreEqual(productJson, Scalar(target, "catalog.db", "SELECT Json FROM CatalogProducts LIMIT 1"));
        Assert.AreEqual(mapping, Scalar(target, "catalog.db", "SELECT ExternalId FROM MarketplaceMappings WHERE Channel='etsy' AND ShopId='303'"));
        Assert.AreEqual(orderJson, Scalar(target, "orders.db", "SELECT payload FROM orders WHERE marketplace='manual' AND shop='legacy-shop' AND id='ORD-1'"));
        Assert.AreEqual(stockReceiptJson, Scalar(target, "catalog.db", "SELECT Json FROM OrderStockReceipts WHERE Marketplace='etsy' AND ShopId='303' AND OrderId='LEGACY-RECEIPT'"));
        Assert.AreEqual(mediaId, Scalar(target, "media.db", "SELECT Id FROM ProductMedia LIMIT 1"));
        Assert.AreEqual(mediaUrl, Scalar(target, "media.db", "SELECT Url FROM ProductMedia LIMIT 1"));
        Assert.IsNotNull(new CatalogStore(target).GetOrderStockStatus("etsy", "303", "LEGACY-RECEIPT"));
        Assert.AreEqual(mediaId, new MediaStore(target).List(ProductId(target)).Single().Id);
        Assert.AreEqual(3, new ProductSourceBindingStore(target).Get(ProductId(target)).Count);
        Assert.AreEqual(7, new InventoryLocationStore(target).GetBalance(ProductId(target), InventoryLocationStore.OnlineLocationId).Quantity);
        var connection = new MarketplaceConnectionStore(target).Find("etsy", "303");
        Assert.IsNotNull(connection);
        Assert.IsNotNull(new ProductChannelBindingStore(target).Get(ProductId(target), connection.Id));
        Assert.AreEqual(1, new OrdersStore(target).ReadAll().Count);
        Assert.AreEqual(1, new AutomationStore(target).List().Count);
    }

    [TestMethod]
    public void EveryCheckpointCanResumeAndRollbackRestoresTheCopiedLegacyDirectory()
    {
        foreach (var checkpoint in Enum.GetValues<MultiStoreMigrationCheckpoint>().Where(value => value != MultiStoreMigrationCheckpoint.None))
        {
            var target = CopyLegacyFixture();
            var legacySnapshot = Snapshot(target);
            var report = new MultiStoreMigrationService(target).DryRun();
            var interrupted = new MultiStoreMigrationService(target, reached =>
            {
                if (reached == checkpoint) throw new InjectedMigrationFailureException(checkpoint);
            });

            var error = Assert.ThrowsException<InjectedMigrationFailureException>(() => interrupted.Apply(report, report.ConfirmationToken));
            Assert.AreEqual(checkpoint, error.Checkpoint);

            var resumed = new MultiStoreMigrationService(target).Apply(report, report.ConfirmationToken);
            Assert.AreEqual(MultiStoreMigrationCheckpoint.Completed, resumed.Checkpoint, $"Restart must resume after {checkpoint}.");
            Assert.AreEqual(3, new ProductSourceBindingStore(target).Get(ProductId(target)).Count);
            Assert.AreEqual(1, new MarketplaceConnectionStore(target).List(false).Count);
            Assert.AreEqual(1, new ProductChannelBindingStore(target).List().Count);

            var rollback = new MultiStoreMigrationService(target).Rollback(resumed);
            Assert.AreEqual(resumed.BackupPath, rollback.BackupPath);
            Assert.IsTrue(File.Exists(rollback.BackupPath), "Rollback must preserve its source backup archive.");
            CollectionAssert.AreEquivalent(legacySnapshot, Snapshot(target), $"Rollback after {checkpoint} must restore the copied legacy directory exactly.");
        }
    }

    [TestMethod]
    public void StartupGateDoesNotWriteBeforeExplicitConfirmationAndCanCompleteMigration()
    {
        var target = CopyLegacyFixture();
        var before = Snapshot(target);
        MultiStoreMigrationDryRun shown = null;

        var cancelled = MultiStoreMigrationStartupGate.EnsureReady(target, report => { shown = report; return null; });

        Assert.IsFalse(cancelled);
        Assert.IsNotNull(shown);
        Assert.AreEqual(1, shown.Connections);
        CollectionAssert.AreEquivalent(before, Snapshot(target), "Startup must not create schema, inventory, vault, checkpoint, probe, or receipt before local confirmation.");

        var completed = MultiStoreMigrationStartupGate.EnsureReady(target, report => report.ConfirmationToken);

        Assert.IsTrue(completed);
        Assert.IsNull(new MultiStoreMigrationService(target).StartupReport(), "A completed/current profile must start without another prompt.");
        Assert.IsNotNull(new MarketplaceConnectionStore(target).Find("etsy", "303"));
    }

    [TestMethod]
    public void FreshNoOpStartupDoesNotPromptOrCreateTheDataDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "multi-store-noop-" + Guid.NewGuid().ToString("N"));
        roots.Add(root);
        var prompted = false;

        var ready = MultiStoreMigrationStartupGate.EnsureReady(root, report =>
        {
            prompted = true;
            return report.ConfirmationToken;
        });

        Assert.IsTrue(ready);
        Assert.IsFalse(prompted);
        Assert.IsFalse(Directory.Exists(root), "The migration gate itself stays write-free for a fresh/no-op profile; normal preflight may create it afterward.");
    }

    [TestMethod]
    public void ProductionStartupGateResumesAfterAnInjectedCheckpointFailure()
    {
        var target = CopyLegacyFixture();
        var prompted = 0;

        Assert.ThrowsException<InjectedMigrationFailureException>(() => MultiStoreMigrationStartupGate.EnsureReady(target, report =>
        {
            prompted++;
            return report.ConfirmationToken;
        }, checkpoint =>
        {
            if (checkpoint == MultiStoreMigrationCheckpoint.ConnectionsApplied) throw new InjectedMigrationFailureException(checkpoint);
        }));

        var resumed = MultiStoreMigrationStartupGate.EnsureReady(target, report =>
        {
            prompted++;
            return report.ConfirmationToken;
        });

        Assert.IsTrue(resumed);
        Assert.AreEqual(2, prompted);
        Assert.IsNull(new MultiStoreMigrationService(target).StartupReport());
    }

    [TestMethod]
    public void ConnectionSubstepCrashRestoresApprovedBackupAndProductionStartupCanRetry()
    {
        var target = CopyLegacyFixture();
        var trendyolPath = Path.Combine(target, "trendyol.bin");
        new TrendyolSettingsStore(trendyolPath).Save(new TrendyolSettings("101", "trendyol-key", "trendyol-secret", "101 - Test"));
        SqliteConnection.ClearAllPools();
        var before = Snapshot(target);
        var etsyLegacyHash = Hash(Path.Combine(target, "credentials.bin"));
        var trendyolLegacyHash = Hash(trendyolPath);
        var prompts = 0;

        Assert.ThrowsException<InjectedConnectionSubstepFailureException>(() =>
            MultiStoreMigrationStartupGate.EnsureReady(
                target,
                report => { prompts++; return report.ConfirmationToken; },
                afterConnectionResult: result =>
                {
                    if (result.Channel == "etsy" && result.State == MarketplaceConnectionMigrationState.Imported)
                        throw new InjectedConnectionSubstepFailureException();
                }));

        Assert.IsTrue(File.Exists(Path.Combine(target, "multi-store-migration-v1.json")), "An abrupt process stop leaves a durable in-progress marker for startup recovery.");
        Assert.IsTrue(Directory.Exists(Path.Combine(target, "marketplace-credentials")), "The fixture must really stop after the first credential substep wrote.");
        Assert.AreEqual(etsyLegacyHash, Hash(Path.Combine(target, "credentials.bin")));
        Assert.AreEqual(trendyolLegacyHash, Hash(trendyolPath));
        var approvedBackups = Directory.GetFiles(Directory.GetParent(target)!.FullName, Path.GetFileName(target) + "-multi-store-v1-*.zip");
        Assert.AreEqual(1, approvedBackups.Length);
        Assert.AreEqual(MultiStoreMigrationService.BackupFormat, new DataBackupService(target).Validate(approvedBackups[0]).Format);

        var restoredBeforeRetryApproval = false;
        var ready = MultiStoreMigrationStartupGate.EnsureReady(target, report =>
        {
            prompts++;
            CollectionAssert.AreEquivalent(before, Snapshot(target), "Restart must restore the exact approved snapshot before showing the retry confirmation.");
            Assert.IsFalse(Directory.Exists(Path.Combine(target, "marketplace-credentials")), "No partial vault entry may survive startup recovery.");
            Assert.IsFalse(File.Exists(Path.Combine(target, "multi-store-migration-v1.json")), "The restored pre-migration backup contains no stale checkpoint.");
            restoredBeforeRetryApproval = true;
            return report.ConfirmationToken;
        });

        Assert.IsTrue(ready);
        Assert.IsTrue(restoredBeforeRetryApproval);
        Assert.AreEqual(2, prompts, "Retry after verified rollback requires a fresh dry-run and explicit local confirmation.");
        var connections = new MarketplaceConnectionStore(target).List(false);
        Assert.AreEqual(2, connections.Count);
        var vault = new MarketplaceCredentialVault(target);
        var etsy = connections.Single(connection => connection.Channel == "etsy");
        var trendyol = connections.Single(connection => connection.Channel == "trendyol");
        Assert.AreEqual("303", vault.Load<EtsyCredentials>(etsy.Id, etsy.Channel, etsy.ShopId)!.ShopId);
        Assert.AreEqual("101", vault.Load<TrendyolSettings>(trendyol.Id, trendyol.Channel, trendyol.ShopId)!.SupplierId);
        Assert.AreEqual(1, new ProductChannelBindingStore(target).List().Count);
        Assert.AreEqual(etsyLegacyHash, Hash(Path.Combine(target, "credentials.bin")), "Legacy Etsy bytes remain untouched after rollback and retry.");
        Assert.AreEqual(trendyolLegacyHash, Hash(trendyolPath), "Legacy Trendyol bytes remain untouched after rollback and retry.");
    }

    [DataTestMethod]
    [DataRow(MultiStoreMigrationCheckpoint.SourceBindingsApplied)]
    [DataRow(MultiStoreMigrationCheckpoint.InventoryBalancesApplied)]
    [DataRow(MultiStoreMigrationCheckpoint.ConnectionsApplied)]
    [DataRow(MultiStoreMigrationCheckpoint.ProductBindingsApplied)]
    public void EveryMutatingStageCrashRestoresApprovedSnapshotBeforeFreshApproval(MultiStoreMigrationCheckpoint stage)
    {
        var target = CopyLegacyFixture();
        var before = Snapshot(target);
        var legacyCredentialHash = Hash(Path.Combine(target, "credentials.bin"));
        var prompts = 0;

        var failure = Assert.ThrowsException<InjectedStageWriteFailureException>(() =>
            MultiStoreMigrationStartupGate.EnsureReady(
                target,
                report => { prompts++; return report.ConfirmationToken; },
                afterStageWrite: writtenStage =>
                {
                    if (writtenStage == stage) throw new InjectedStageWriteFailureException(stage);
                }));

        SqliteConnection.ClearAllPools(); // A real process crash releases its SQLite handles before restart.
        Assert.AreEqual(stage, failure.Stage);
        Assert.IsTrue(File.Exists(Path.Combine(target, "multi-store-migration-v1.json")), "The durable in-progress marker must survive the simulated process stop.");
        Assert.IsFalse(before.SequenceEqual(Snapshot(target)), "The test must interrupt after the stage committed at least one real write.");

        var restoredBeforeRetryApproval = false;
        var ready = MultiStoreMigrationStartupGate.EnsureReady(target, report =>
        {
            prompts++;
            CollectionAssert.AreEquivalent(before, Snapshot(target), $"Restart after {stage} must restore the approved pre-migration snapshot before prompting.");
            restoredBeforeRetryApproval = true;
            return report.ConfirmationToken;
        });

        Assert.IsTrue(ready);
        Assert.IsTrue(restoredBeforeRetryApproval);
        Assert.AreEqual(2, prompts, "Every recovered mutating stage requires a fresh dry-run and explicit approval.");
        Assert.AreEqual(legacyCredentialHash, Hash(Path.Combine(target, "credentials.bin")));
        Assert.AreEqual(3, new ProductSourceBindingStore(target).Get(ProductId(target)).Count);
        Assert.AreEqual(7, new InventoryLocationStore(target).GetBalance(ProductId(target), InventoryLocationStore.OnlineLocationId).Quantity);
        Assert.AreEqual(1, new ProductChannelBindingStore(target).List().Count);
    }

    [TestMethod]
    public void MixedConnectionOutcomeRestoresApprovedBackupBeforeARepairedRetry()
    {
        var target = CopyLegacyFixture();
        var trendyolPath = Path.Combine(target, "trendyol.bin");
        File.WriteAllBytes(trendyolPath, [1, 2, 3, 4]);
        SqliteConnection.ClearAllPools();
        var before = Snapshot(target);
        var etsyLegacyHash = Hash(Path.Combine(target, "credentials.bin"));
        var corruptTrendyolHash = Hash(trendyolPath);
        var prompts = 0;

        var failure = Assert.ThrowsException<InvalidOperationException>(() =>
            MultiStoreMigrationStartupGate.EnsureReady(target, report => { prompts++; return report.ConfirmationToken; }));

        StringAssert.Contains(failure.Message, "geri");
        CollectionAssert.AreEquivalent(before, Snapshot(target), "An Imported + Failed outcome must not leave the successful account or checkpoint writes behind.");
        Assert.AreEqual(etsyLegacyHash, Hash(Path.Combine(target, "credentials.bin")));
        Assert.AreEqual(corruptTrendyolHash, Hash(trendyolPath));
        Assert.IsFalse(Directory.Exists(Path.Combine(target, "marketplace-credentials")));
        Assert.IsFalse(File.Exists(Path.Combine(target, "multi-store-migration-v1.json")));

        new TrendyolSettingsStore(trendyolPath).Save(new TrendyolSettings("101", "trendyol-key", "trendyol-secret", "101 - Test"));
        SqliteConnection.ClearAllPools();
        var repairedTrendyolHash = Hash(trendyolPath);
        var ready = MultiStoreMigrationStartupGate.EnsureReady(target, report => { prompts++; return report.ConfirmationToken; });

        Assert.IsTrue(ready);
        Assert.AreEqual(2, prompts, "The repaired retry must receive a new dry-run and explicit local approval.");
        var connections = new MarketplaceConnectionStore(target).List(false);
        Assert.AreEqual(2, connections.Count);
        Assert.AreEqual(1, new ProductChannelBindingStore(target).List().Count);
        Assert.AreEqual(etsyLegacyHash, Hash(Path.Combine(target, "credentials.bin")));
        Assert.AreEqual(repairedTrendyolHash, Hash(trendyolPath));
    }

    [TestMethod]
    public void StartupRecoversMissingActiveDirectoryFromDurableRestoreJournalBeforeFreshApproval()
    {
        var target = CopyLegacyFixture();
        var before = Snapshot(target);
        var prompts = 0;

        Assert.ThrowsException<InjectedStageWriteFailureException>(() =>
            MultiStoreMigrationStartupGate.EnsureReady(target, report => { prompts++; return report.ConfirmationToken; },
                afterStageWrite: stage =>
                {
                    if (stage == MultiStoreMigrationCheckpoint.ProductBindingsApplied)
                        throw new InjectedStageWriteFailureException(stage);
                }));
        SqliteConnection.ClearAllPools();

        Assert.ThrowsException<DataRestoreAbruptInterruptionException>(() =>
            MultiStoreMigrationStartupGate.EnsureReady(target, report => report.ConfirmationToken,
                afterRestoreCheckpoint: checkpoint =>
                {
                    if (checkpoint == DataRestoreCheckpoint.ActiveDirectoryMoved)
                        throw new DataRestoreAbruptInterruptionException();
                }));

        Assert.IsFalse(Directory.Exists(target), "The fault must reproduce the real missing-active-directory crash window.");
        Assert.AreEqual(1, Directory.GetDirectories(Directory.GetParent(target)!.FullName, Path.GetFileName(target) + ".pre-restore-*").Length);
        Assert.AreEqual(1, Directory.GetFiles(Directory.GetParent(target)!.FullName, "." + Path.GetFileName(target) + ".restore-journal-v1.json").Length);

        var restoredBeforeRetryApproval = false;
        var ready = MultiStoreMigrationStartupGate.EnsureReady(target, report =>
        {
            prompts++;
            CollectionAssert.AreEquivalent(before, Snapshot(target), "Restore-journal recovery must recreate the approved data directory byte-for-byte before prompting.");
            restoredBeforeRetryApproval = true;
            return report.ConfirmationToken;
        });

        Assert.IsTrue(ready);
        Assert.IsTrue(restoredBeforeRetryApproval);
        Assert.AreEqual(2, prompts);
        Assert.AreEqual(1, new ProductChannelBindingStore(target).List().Count);
        Assert.IsFalse(File.Exists(Path.Combine(Directory.GetParent(target)!.FullName, "." + Path.GetFileName(target) + ".restore-journal-v1.json")));
    }

    [DataTestMethod]
    [DataRow(DataRestoreCheckpoint.JournalPrepared)]
    [DataRow(DataRestoreCheckpoint.StagingExtracted)]
    [DataRow(DataRestoreCheckpoint.StagingVerified)]
    [DataRow(DataRestoreCheckpoint.ActiveDirectoryMoved)]
    [DataRow(DataRestoreCheckpoint.RestoredDirectoryVerified)]
    public void RestoreJournalRecoversEveryDurableSwapSubstep(DataRestoreCheckpoint interruptedAt)
    {
        var target = CopyLegacyFixture();
        var before = Snapshot(target);
        var prompts = 0;
        Assert.ThrowsException<InjectedStageWriteFailureException>(() =>
            MultiStoreMigrationStartupGate.EnsureReady(target, report => { prompts++; return report.ConfirmationToken; },
                afterStageWrite: stage =>
                {
                    if (stage == MultiStoreMigrationCheckpoint.ProductBindingsApplied)
                        throw new InjectedStageWriteFailureException(stage);
                }));
        SqliteConnection.ClearAllPools();

        Assert.ThrowsException<DataRestoreAbruptInterruptionException>(() =>
            MultiStoreMigrationStartupGate.EnsureReady(target, report => report.ConfirmationToken,
                afterRestoreCheckpoint: checkpoint =>
                {
                    if (checkpoint == interruptedAt) throw new DataRestoreAbruptInterruptionException();
                }));

        var observedRestoredSnapshot = false;
        Assert.IsTrue(MultiStoreMigrationStartupGate.EnsureReady(target, report =>
        {
            prompts++;
            CollectionAssert.AreEquivalent(before, Snapshot(target));
            observedRestoredSnapshot = true;
            return report.ConfirmationToken;
        }));

        Assert.IsTrue(observedRestoredSnapshot);
        Assert.AreEqual(2, prompts);
        Assert.IsFalse(File.Exists(Path.Combine(Directory.GetParent(target)!.FullName, "." + Path.GetFileName(target) + ".restore-journal-v1.json")));
    }

    [TestMethod]
    public void MissingActiveDirectoryWithTamperedJournalBackupFailsClosedAndPreservesPreRestoreData()
    {
        var target = CopyLegacyFixture();
        var legacyHash = Hash(Path.Combine(target, "credentials.bin"));
        Assert.ThrowsException<InjectedStageWriteFailureException>(() =>
            MultiStoreMigrationStartupGate.EnsureReady(target, report => report.ConfirmationToken,
                afterStageWrite: stage =>
                {
                    if (stage == MultiStoreMigrationCheckpoint.ProductBindingsApplied)
                        throw new InjectedStageWriteFailureException(stage);
                }));
        SqliteConnection.ClearAllPools();
        Assert.ThrowsException<DataRestoreAbruptInterruptionException>(() =>
            MultiStoreMigrationStartupGate.EnsureReady(target, report => report.ConfirmationToken,
                afterRestoreCheckpoint: checkpoint =>
                {
                    if (checkpoint == DataRestoreCheckpoint.ActiveDirectoryMoved) throw new DataRestoreAbruptInterruptionException();
                }));
        var parent = Directory.GetParent(target)!.FullName;
        var journalPath = Path.Combine(parent, "." + Path.GetFileName(target) + ".restore-journal-v1.json");
        using var journal = JsonDocument.Parse(File.ReadAllText(journalPath));
        var backupPath = journal.RootElement.GetProperty("BackupPath").GetString()!;
        File.AppendAllText(backupPath, "tampered");
        var prompted = false;

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            MultiStoreMigrationStartupGate.EnsureReady(target, report => { prompted = true; return report.ConfirmationToken; }));

        StringAssert.Contains(error.Message, "yedek");
        Assert.IsFalse(prompted);
        Assert.IsFalse(Directory.Exists(target));
        var old = Directory.GetDirectories(parent, Path.GetFileName(target) + ".pre-restore-*").Single();
        Assert.AreEqual(legacyHash, Hash(Path.Combine(old, "credentials.bin")));
        Assert.IsTrue(File.Exists(journalPath));
    }

    [DataTestMethod]
    [DataRow("PreRestoreDirectory")]
    [DataRow("PreRestoreFingerprint")]
    public void RestoreJournalRejectsTamperedPathOrFingerprint(string field)
    {
        var target = CopyLegacyFixture();
        Assert.ThrowsException<InjectedStageWriteFailureException>(() =>
            MultiStoreMigrationStartupGate.EnsureReady(target, report => report.ConfirmationToken,
                afterStageWrite: stage =>
                {
                    if (stage == MultiStoreMigrationCheckpoint.ProductBindingsApplied)
                        throw new InjectedStageWriteFailureException(stage);
                }));
        SqliteConnection.ClearAllPools();
        Assert.ThrowsException<DataRestoreAbruptInterruptionException>(() =>
            MultiStoreMigrationStartupGate.EnsureReady(target, report => report.ConfirmationToken,
                afterRestoreCheckpoint: checkpoint =>
                {
                    if (checkpoint == DataRestoreCheckpoint.ActiveDirectoryMoved) throw new DataRestoreAbruptInterruptionException();
                }));
        var parent = Directory.GetParent(target)!.FullName;
        var journalPath = Path.Combine(parent, "." + Path.GetFileName(target) + ".restore-journal-v1.json");
        var journal = JsonNode.Parse(File.ReadAllText(journalPath))!.AsObject();
        journal[field] = field == "PreRestoreFingerprint" ? new string('0', 64) : Path.Combine(parent, "outside-restore-data");
        File.WriteAllText(journalPath, journal.ToJsonString());
        var prompted = false;

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            MultiStoreMigrationStartupGate.EnsureReady(target, report => { prompted = true; return report.ConfirmationToken; }));

        StringAssert.Contains(error.Message, "geri");
        Assert.IsFalse(prompted);
        Assert.IsFalse(Directory.Exists(target));
        Assert.IsTrue(File.Exists(journalPath));
        Assert.AreEqual(1, Directory.GetDirectories(parent, Path.GetFileName(target) + ".pre-restore-*").Length);
    }

    [DataTestMethod]
    [DataRow("marketplace-credentials")]
    [DataRow("media/cache")]
    public void RestoreRejectsNestedJunctionBeforeDirectorySwapAndNeverTouchesExternalTarget(string relativeLink)
    {
        var root = Path.Combine(Path.GetTempPath(), "multi-store-reparse-" + Guid.NewGuid().ToString("N"));
        roots.Add(root);
        var target = Path.Combine(root, "data");
        var external = Path.Combine(root, "external-target");
        var backup = Path.Combine(root, "approved.zip");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(target, "marker.txt"), "approved");
        File.WriteAllText(Path.Combine(external, "sentinel.txt"), "external-safe");
        var service = new DataBackupService(target);
        service.Backup(backup);
        var backupHash = Hash(backup);
        File.WriteAllText(Path.Combine(target, "marker.txt"), "current");
        var injected = false;

        var error = Assert.ThrowsException<InvalidDataException>(() =>
            new DataBackupService(target, checkpoint =>
            {
                if (checkpoint != DataRestoreCheckpoint.StagingExtracted) return;
                var staging = Directory.GetDirectories(root, ".data.restore-staging-*").Single();
                var link = Path.Combine(staging, relativeLink.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(link)!);
                CreateJunction(link, external);
                injected = true;
            }).Restore(backup));

        StringAssert.Contains(error.Message, "reparse");
        Assert.IsTrue(injected);
        Assert.AreEqual("current", File.ReadAllText(Path.Combine(target, "marker.txt")), "The active directory must remain byte-identical when staging is unsafe.");
        Assert.AreEqual("external-safe", File.ReadAllText(Path.Combine(external, "sentinel.txt")));
        Assert.AreEqual(1, Directory.GetFiles(external).Length, "Cleanup must not traverse the junction into the external target.");
        Assert.AreEqual(backupHash, Hash(backup));
        Assert.IsFalse(File.Exists(Path.Combine(root, ".data.restore-journal-v1.json")));
        Assert.AreEqual(0, Directory.GetDirectories(root, ".data.restore-staging-*").Length);
        Assert.AreEqual(0, Directory.GetDirectories(root, "data.pre-restore-*").Length);
    }

    [TestMethod]
    public void BackupRejectsNestedActiveDirectoryJunctionWithoutReadingExternalFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "multi-store-active-reparse-" + Guid.NewGuid().ToString("N"));
        roots.Add(root);
        var target = Path.Combine(root, "data");
        var external = Path.Combine(root, "external-target");
        var link = Path.Combine(target, "media", "external-cache");
        var backup = Path.Combine(root, "backup.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(target, "marker.txt"), "current");
        File.WriteAllText(Path.Combine(external, "sentinel.txt"), "external-safe");
        CreateJunction(link, external);

        var error = Assert.ThrowsException<InvalidDataException>(() => new DataBackupService(target).Backup(backup));

        StringAssert.Contains(error.Message, "reparse");
        Assert.IsFalse(File.Exists(backup));
        Assert.AreEqual("external-safe", File.ReadAllText(Path.Combine(external, "sentinel.txt")));
        Assert.AreEqual(1, Directory.GetFiles(external).Length);
        Directory.Delete(link, false);
    }

    [TestMethod]
    public void RecoveryRejectsNestedPreRestoreJunctionAndPreservesAllArtifacts()
    {
        var target = CopyLegacyFixture();
        var parent = Directory.GetParent(target)!.FullName;
        var external = Path.Combine(parent, "external-recovery-target");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "sentinel.txt"), "external-safe");
        Assert.ThrowsException<InjectedStageWriteFailureException>(() =>
            MultiStoreMigrationStartupGate.EnsureReady(target, report => report.ConfirmationToken,
                afterStageWrite: stage =>
                {
                    if (stage == MultiStoreMigrationCheckpoint.ProductBindingsApplied)
                        throw new InjectedStageWriteFailureException(stage);
                }));
        SqliteConnection.ClearAllPools();
        Assert.ThrowsException<DataRestoreAbruptInterruptionException>(() =>
            MultiStoreMigrationStartupGate.EnsureReady(target, report => report.ConfirmationToken,
                afterRestoreCheckpoint: checkpoint =>
                {
                    if (checkpoint == DataRestoreCheckpoint.ActiveDirectoryMoved) throw new DataRestoreAbruptInterruptionException();
                }));
        var old = Directory.GetDirectories(parent, Path.GetFileName(target) + ".pre-restore-*").Single();
        var link = Path.Combine(old, "marketplace-credentials-link");
        CreateJunction(link, external);
        var journal = Path.Combine(parent, "." + Path.GetFileName(target) + ".restore-journal-v1.json");

        var error = Assert.ThrowsException<InvalidDataException>(() =>
            MultiStoreMigrationStartupGate.EnsureReady(target, report => report.ConfirmationToken));

        StringAssert.Contains(error.Message, "reparse");
        Assert.IsFalse(Directory.Exists(target));
        Assert.IsTrue(Directory.Exists(old));
        Assert.IsTrue(File.Exists(journal));
        Assert.AreEqual("external-safe", File.ReadAllText(Path.Combine(external, "sentinel.txt")));
        Directory.Delete(link, false);
    }

    [TestMethod]
    public void RecoveryRejectsNestedActiveJunctionDuringExactManifestComparison()
    {
        var target = CopyLegacyFixture();
        var parent = Directory.GetParent(target)!.FullName;
        var external = Path.Combine(parent, "external-active-target");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "sentinel.txt"), "external-safe");
        Assert.ThrowsException<InjectedStageWriteFailureException>(() =>
            MultiStoreMigrationStartupGate.EnsureReady(target, report => report.ConfirmationToken,
                afterStageWrite: stage =>
                {
                    if (stage == MultiStoreMigrationCheckpoint.ProductBindingsApplied)
                        throw new InjectedStageWriteFailureException(stage);
                }));
        SqliteConnection.ClearAllPools();
        var link = "";
        Assert.ThrowsException<DataRestoreAbruptInterruptionException>(() =>
            MultiStoreMigrationStartupGate.EnsureReady(target, report => report.ConfirmationToken,
                afterRestoreCheckpoint: checkpoint =>
                {
                    if (checkpoint != DataRestoreCheckpoint.RestoredDirectoryVerified) return;
                    link = Path.Combine(target, "media-link");
                    CreateJunction(link, external);
                    throw new DataRestoreAbruptInterruptionException();
                }));
        var journal = Path.Combine(parent, "." + Path.GetFileName(target) + ".restore-journal-v1.json");

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            MultiStoreMigrationStartupGate.EnsureReady(target, report => report.ConfirmationToken));

        StringAssert.Contains(error.Message, "geri");
        Assert.IsTrue(Directory.Exists(target));
        Assert.IsTrue(File.Exists(journal));
        Assert.AreEqual(1, Directory.GetDirectories(parent, Path.GetFileName(target) + ".pre-restore-*").Length);
        Assert.AreEqual("external-safe", File.ReadAllText(Path.Combine(external, "sentinel.txt")));
        Directory.Delete(link!, false);
    }

    [TestMethod]
    public void MissingActiveDirectoryWithStaleRestoreArtifactFailsClosedWithoutCreatingFreshData()
    {
        var root = Path.Combine(Path.GetTempPath(), "multi-store-stale-restore-" + Guid.NewGuid().ToString("N"));
        roots.Add(root);
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, "data");
        var stale = target + ".pre-restore-stale";
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "preserved.txt"), "preserve");
        var prompted = false;

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            MultiStoreMigrationStartupGate.EnsureReady(target, report => { prompted = true; return report.ConfirmationToken; }));

        StringAssert.Contains(error.Message, "geri");
        Assert.IsFalse(prompted);
        Assert.IsFalse(Directory.Exists(target), "A stale artifact without its validated journal must never be treated as a fresh profile.");
        Assert.AreEqual("preserve", File.ReadAllText(Path.Combine(stale, "preserved.txt")));
    }

    [TestMethod]
    public void MutationAfterVerifiedCheckpointCannotProduceCompletedReceipt()
    {
        var target = CopyLegacyFixture();
        var report = new MultiStoreMigrationService(target).DryRun();
        var unexpected = Path.Combine(target, "after-verified.txt");

        var error = Assert.ThrowsException<InvalidOperationException>(() =>
            new MultiStoreMigrationService(target, checkpoint =>
            {
                if (checkpoint == MultiStoreMigrationCheckpoint.Verified) File.WriteAllText(unexpected, "changed");
            }).Apply(report, report.ConfirmationToken));

        StringAssert.Contains(error.Message, "checkpoint");
        Assert.IsTrue(File.Exists(unexpected));
        var state = File.ReadAllText(Path.Combine(target, "multi-store-migration-v1.json"));
        StringAssert.Contains(state, "\"Checkpoint\":6");
        Assert.IsFalse(state.Contains("\"Checkpoint\":7", StringComparison.Ordinal), "Completed state must not be written after post-verification mutation.");

        File.Delete(unexpected);
        var completed = new MultiStoreMigrationService(target).Apply(report, report.ConfirmationToken);
        Assert.AreEqual(MultiStoreMigrationCheckpoint.Completed, completed.Checkpoint);
    }

    [TestMethod]
    public void CompletedMigrationRequiresItsVerifiedRollbackBackupForIdempotentSuccess()
    {
        foreach (var corrupt in new[] { false, true })
        {
            var target = CopyLegacyFixture();
            var service = new MultiStoreMigrationService(target);
            var report = service.DryRun();
            var completed = service.Apply(report, report.ConfirmationToken);
            if (corrupt) File.AppendAllText(completed.BackupPath, "corrupt");
            else File.Delete(completed.BackupPath);

            var error = Assert.ThrowsException<InvalidOperationException>(() =>
                new MultiStoreMigrationService(target).Apply(report, report.ConfirmationToken));

            StringAssert.Contains(error.Message, "yedek");
            StringAssert.Contains(error.Message, "geri");
        }
    }

    [TestMethod]
    public void InterruptedMigrationRejectsAlteredDataWithOriginalOrUnrelatedNewApproval()
    {
        foreach (var useNewReport in new[] { false, true })
        {
            var target = CopyLegacyFixture();
            var approved = new MultiStoreMigrationService(target).DryRun();
            Assert.ThrowsException<InjectedMigrationFailureException>(() => new MultiStoreMigrationService(target, checkpoint =>
            {
                if (checkpoint == MultiStoreMigrationCheckpoint.SourceBindingsApplied) throw new InjectedMigrationFailureException(checkpoint);
            }).Apply(approved, approved.ConfirmationToken));
            SqliteConnection.ClearAllPools();
            File.WriteAllText(Path.Combine(target, "unexpected-after-checkpoint.txt"), "changed");
            var candidate = useNewReport ? new MultiStoreMigrationService(target).DryRun() : approved;
            var before = Snapshot(target);

            var error = Assert.ThrowsException<InvalidOperationException>(() =>
                new MultiStoreMigrationService(target).Apply(candidate, candidate.ConfirmationToken));

            StringAssert.Contains(error.Message, "checkpoint");
            CollectionAssert.AreEquivalent(before, Snapshot(target), "A rejected resume must not advance or rewrite partial migration data.");
        }
    }

    [TestMethod]
    public void ProductBindingDryRunCountsExactUnionOfExistingAndLegacyProfileIdentities()
    {
        var target = CopyLegacyFixture();
        var connections = new MarketplaceConnectionStore(target);
        var existingConnection = connections.Save("amazon", "existing-shop", "Existing", true, "existing-connection");
        var bindings = new ProductChannelBindingStore(target);
        bindings.Save(new(ProductId(target), existingConnection.Id, "REMOTE-EXISTING", "SKU-EXISTING", "BAR-EXISTING",
            true, true, true, "", "", "Active", 0, default), 0);
        SqliteConnection.ClearAllPools();
        Checkpoint(target);
        var service = new MultiStoreMigrationService(target);

        var report = service.DryRun();

        Assert.AreEqual(2, report.ProductBindings, "Disjoint existing and legacy-derived (ProductId,ConnectionId) identities form a union, not a maximum count.");
        var receipt = service.Apply(report, report.ConfirmationToken);
        Assert.AreEqual(MultiStoreMigrationCheckpoint.Completed, receipt.Checkpoint);
        Assert.AreEqual(2, new ProductChannelBindingStore(target).List().Count);
    }

    [TestMethod]
    public void ProductBindingDryRunExcludesLegacyProfilesThatMigrationWillSkip()
    {
        var target = CopyLegacyFixture();
        var catalog = new CatalogStore(target);
        var skippedProduct = catalog.CreateManual(new CatalogProduct { Sku = "SKIP-1", Name = "Skipped", Price = 1, Stock = 1, Currency = "TRY" });
        var workspace = new EtsyWorkspaceStore(target);
        var state = workspace.Load("303");
        state.Profiles.Add(new() { ProductId = skippedProduct.Id, ListingId = 901, Price = 1, PriceCurrency = "TRY" });
        workspace.Save(state);
        SqliteConnection.ClearAllPools();
        var service = new MultiStoreMigrationService(target);

        var report = service.DryRun();

        Assert.AreEqual(1, report.ProductBindings, "A profile with no unique remote listing is not a migratable binding identity.");
        _ = service.Apply(report, report.ConfirmationToken);
        Assert.AreEqual(1, new ProductChannelBindingStore(target).List().Count);
    }

    string CopyLegacyFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "multi-store-migration-" + Guid.NewGuid().ToString("N"));
        roots.Add(root);
        var legacy = Path.Combine(root, "legacy");
        var target = Path.Combine(root, "copy");
        Directory.CreateDirectory(legacy);
        SeedLegacy(legacy);
        SqliteConnection.ClearAllPools();
        CopyDirectory(legacy, target);
        return target;
    }

    static void SeedLegacy(string root)
    {
        var catalog = new CatalogStore(root);
        var source = new XmlSource { Id = "xml-a", Name = "Legacy XML", Location = "legacy.xml", ItemPath = "/Items/Item", Currency = "TRY", Fields = new() { ["Sku"] = "sku", ["Name"] = "name", ["Cost"] = "price", ["Stock"] = "stock" } };
        catalog.SaveSource(source);
        catalog.Import(source, [new CatalogProduct
        {
            SourceId = source.Id,
            SourceKind = "xml",
            PriceSource = "xml",
            StockSource = "xml",
            MediaSource = "xml",
            Sku = "LEGACY-1",
            Name = "Legacy",
            Price = 10,
            Stock = 7,
            Currency = "TRY"
        }]);
        var product = catalog.Products().Single();
        new MarketplaceMappingStore(root).Save(new("etsy", "303", product.Id, "listing-900"));
        new MediaStore(root).Add(product.Id, "https://example.com/legacy-product.jpg", "xml");
        CredentialStore.Save(new EtsyCredentials("legacy-key", "legacy-secret", "legacy-token", "303"), root);
        var etsy = new EtsyWorkspaceStore(root);
        var state = etsy.Load("303");
        state.Listings.Add(new(900, "Legacy", "active", 7, 10, "TRY", "LEGACY-1"));
        state.Profiles.Add(new() { ProductId = product.Id, ListingId = 900, Price = 10, PriceCurrency = "TRY" });
        etsy.Save(state);
        new OrdersStore(root).SaveManual(new OrderSnapshot { Marketplace = "manual", ShopId = "legacy-shop", OrderId = "ORD-1", Currency = "TRY" });
        new AutomationStore(root).Save(new AutomationJob { Id = "legacy-job", Channel = "etsy", Shop = "303", Kind = AutomationKind.Health, IntervalMinutes = 30, NextRunUtc = DateTime.UtcNow.AddHours(1) });
        SqliteConnection.ClearAllPools();
        using var connection = new SqliteConnection("Data Source=" + Path.Combine(root, "catalog.db"));
        connection.Open();
        var stockReceipt = new OrderStockReceipt("etsy", "303", "LEGACY-RECEIPT", DateTime.UtcNow,
            [new OrderStockMovement(product.Id, product.Sku, 1, 7, 6)]);
        using (var receipt = connection.CreateCommand())
        {
            receipt.CommandText = "INSERT INTO OrderStockReceipts(Marketplace,ShopId,OrderId,Payload,Json) VALUES('etsy','303','LEGACY-RECEIPT',$payload,$json)";
            receipt.Parameters.AddWithValue("$payload", "{\"LEGACY-1\":1}");
            receipt.Parameters.AddWithValue("$json", JsonSerializer.Serialize(stockReceipt));
            receipt.ExecuteNonQuery();
        }
        using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TABLE IF EXISTS ProductSourceBindings;
            DROP TABLE IF EXISTS InventoryLocations;
            DROP TABLE IF EXISTS InventoryBalances;
            DROP TABLE IF EXISTS InventoryProductGenerations;
            DROP TABLE IF EXISTS InventoryMovements;
            DROP TABLE IF EXISTS InventoryTransferPreviews;
            DROP TABLE IF EXISTS InventoryTransferReceipts;
            DROP TABLE IF EXISTS InventoryOrderReceipts;
            DROP TABLE IF EXISTS InventoryManualSaleReceipts;
            DROP TABLE IF EXISTS InventoryMigrations;
            DROP TABLE IF EXISTS MarketplaceConnections;
            DROP TABLE IF EXISTS MarketplaceCredentialMigrations;
            DROP TABLE IF EXISTS ProductChannelBindings;
            DROP TABLE IF EXISTS ProductChannelBindingPreviews;
            DROP TABLE IF EXISTS ProductChannelBindingReceipts;
            """;
        command.ExecuteNonQuery();
    }

    static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
    }

    static void CreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Junction helper başlatılamadı.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, output + error);
        Assert.IsTrue((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
    }

    static string[] Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(root, path) + ":" + Hash(path)).OrderBy(value => value, StringComparer.Ordinal).ToArray();

    static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    static string Scalar(string root, string database, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, database), Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    static string ProductId(string root) => Scalar(root, "catalog.db", "SELECT Id FROM CatalogProducts LIMIT 1");

    static void Checkpoint(string root)
    {
        using var connection = new SqliteConnection("Data Source=" + Path.Combine(root, "catalog.db"));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        command.ExecuteNonQuery();
        connection.Close();
        SqliteConnection.ClearAllPools();
    }
}
