using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for #2653: Migration Assistant's Apply/Undo unit-of-work must be
/// crash-safe, idempotent and resumable across the catalog/journal/taxonomy/
/// store-metadata phases, and Undo must revert only what a given operation
/// itself created.
[TestClass]
public sealed class MigrationAssistantAtomicityTests
{
    static string NewRoot() => Path.Combine(Path.GetTempPath(), "migration-atomicity-" + Guid.NewGuid().ToString("N"));

    static CatalogProduct Row(string sku, string brand = "Acme", string category = "Genel", string name = "Ürün", int stock = 5) =>
        new() { Sku = sku, Barcode = "", Name = name, Brand = brand, Category = category, Cost = 10, Price = 20, Currency = "TRY", VatRate = 20, Stock = stock, Active = true };

    static MigrationPreview PreviewFor(string operationId, params CatalogProduct[] rows) => new()
    {
        SourcePath = "fixture.xlsx",
        Format = MigrationSourceFormat.Excel,
        OperationId = operationId,
        Lines = rows.Select((r, i) => new MigrationPreviewLine { RowNumber = i + 1, Action = "CREATE", Product = r }).ToList(),
        Stores = [new MigrationStoreMetadata("etsy", "shop-a", "Shop A", true)],
    };

    [TestMethod]
    public void NormalApplyProducesOneCompletedOperationWithCorrectCounts()
    {
        var root = NewRoot();
        var service = new MigrationAssistantService(root);
        var preview = PreviewFor(Guid.NewGuid().ToString("N"), Row("SKU-1"));
        var result = service.Apply(preview, true, "");
        Assert.AreEqual(1, result.Created);
        Assert.AreEqual(2, result.TaxonomyAdded);
        Assert.AreEqual(1, result.StoresAdded);
        var journal = new MigrationJournalStore(root).FindByOperation(preview.OperationId);
        Assert.IsNotNull(journal);
        Assert.AreEqual(MigrationJournalStore.PhaseCompleted, journal!.Phase);
    }

    [TestMethod]
    public void RetryingTheSamePreviewIsIdempotentNoDuplicateCountsOrCatalogRows()
    {
        var root = NewRoot();
        var service = new MigrationAssistantService(root);
        var preview = PreviewFor(Guid.NewGuid().ToString("N"), Row("SKU-1"));
        var first = service.Apply(preview, true, "");
        var second = service.Apply(preview, true, "");
        Assert.AreEqual(first.JournalId, second.JournalId);
        Assert.AreEqual(first.TaxonomyAdded, second.TaxonomyAdded);
        Assert.AreEqual(first.StoresAdded, second.StoresAdded);
        Assert.AreEqual(1, new CatalogStore(root).Products().Count(p => p.Sku == "SKU-1"));
    }

    [TestMethod]
    public void CatalogAppliedBeforeCrashIsNeverReappliedOnRetry()
    {
        // Simulates: catalog mutation committed, then the process died before the
        // journal row was written. On the next Apply() with the same preview, the
        // catalog mutation is re-run (it's the only way to know it "took"), but
        // because it matches by Sku it merges instead of duplicating.
        var root = NewRoot();
        var opId = Guid.NewGuid().ToString("N");
        var preview = PreviewFor(opId, Row("SKU-1"));
        new CatalogStore(root).ApplyMigration(preview.ReadyRows, "migration:" + opId);
        Assert.AreEqual(1, new CatalogStore(root).Products().Count(p => p.Sku == "SKU-1"));

        var service = new MigrationAssistantService(root);
        var result = service.Apply(preview, true, "");
        Assert.AreEqual(1, new CatalogStore(root).Products().Count(p => p.Sku == "SKU-1"), "Catalog apply must merge by Sku, never duplicate, on a post-crash retry.");
        Assert.IsNotNull(new MigrationJournalStore(root).FindByOperation(opId));
        _ = result;
    }

    [TestMethod]
    public void ResumingFromCatalogAppliedPhaseCompletesTaxonomyAndStorePhases()
    {
        var root = NewRoot();
        var opId = Guid.NewGuid().ToString("N");
        var preview = PreviewFor(opId, Row("SKU-1"));
        var catalog = new CatalogStore(root);
        var receipt = catalog.ApplyMigration(preview.ReadyRows, "migration:" + opId);
        var journal = new MigrationJournalStore(root);
        var journalId = journal.InsertCatalogPhase(opId, "fixture.xlsx", receipt, "");
        Assert.AreEqual(MigrationJournalStore.PhaseCatalogApplied, journal.FindByOperation(opId)!.Phase);

        var result = new MigrationAssistantService(root).Apply(preview, true, "");
        Assert.AreEqual(journalId, result.JournalId);
        Assert.AreEqual(2, result.TaxonomyAdded);
        Assert.AreEqual(1, result.StoresAdded);
        Assert.AreEqual(MigrationJournalStore.PhaseCompleted, journal.FindByOperation(opId)!.Phase);
    }

    [TestMethod]
    public void ResumingFromTaxonomyAppliedPhaseDoesNotRecountTaxonomyButStillAppliesStores()
    {
        var root = NewRoot();
        var opId = Guid.NewGuid().ToString("N");
        var preview = PreviewFor(opId, Row("SKU-1"));
        var catalog = new CatalogStore(root);
        var receipt = catalog.ApplyMigration(preview.ReadyRows, "migration:" + opId);
        var journal = new MigrationJournalStore(root);
        var journalId = journal.InsertCatalogPhase(opId, "fixture.xlsx", receipt, "");
        var taxonomy = new TaxonomyStore(root);
        var brand = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "Acme", Value = "Acme" });
        var category = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "Genel", Value = "Genel" });
        journal.AppendTaxonomyAdded(journalId, [new(TaxonomyKind.Brand, brand.Id, brand.Name), new(TaxonomyKind.Category, category.Id, category.Name)]);
        journal.AdvanceToTaxonomyPhase(journalId);
        Assert.AreEqual(MigrationJournalStore.PhaseTaxonomyApplied, journal.FindByOperation(opId)!.Phase);

        var result = new MigrationAssistantService(root).Apply(preview, true, "");
        Assert.AreEqual(2, result.TaxonomyAdded, "Resume must report the phase's already-recorded count, not zero from a no-op rerun.");
        Assert.AreEqual(1, result.StoresAdded);
        Assert.AreEqual(MigrationJournalStore.PhaseCompleted, journal.FindByOperation(opId)!.Phase);
    }

    [TestMethod]
    public void StoresAddedNeverDoubleCountsAnAlreadyExistingStore()
    {
        var root = NewRoot();
        new MarketplaceConnectionStore(root).Save("etsy", "shop-a", "Zaten Var", true);
        var service = new MigrationAssistantService(root);
        var preview = PreviewFor(Guid.NewGuid().ToString("N"), Row("SKU-1"));
        var result = service.Apply(preview, true, "");
        Assert.AreEqual(0, result.StoresAdded, "A store metadata row that already existed must never be counted as newly added.");
    }

    [TestMethod]
    public void UndoAfterCompletedOperationRevertsCatalogTaxonomyAndStoreSideEffects()
    {
        var root = NewRoot();
        var service = new MigrationAssistantService(root);
        var preview = PreviewFor(Guid.NewGuid().ToString("N"), Row("SKU-1", stock: 0));
        var result = service.Apply(preview, true, "");
        Assert.AreEqual(1, new CatalogStore(root).Products().Count(p => p.Sku == "SKU-1"));
        Assert.AreEqual(1, new TaxonomyStore(root).List(TaxonomyKind.Brand).Count(x => x.Name == "Acme"));

        service.Undo(result.JournalId);
        Assert.AreEqual(0, new CatalogStore(root).Products().Count(p => p.Sku == "SKU-1"));
        Assert.AreEqual(0, new TaxonomyStore(root).List(TaxonomyKind.Brand).Count(x => x.Name == "Acme"));
        Assert.AreEqual(0, new TaxonomyStore(root).List(TaxonomyKind.Category).Count(x => x.Name == "Genel"));
        Assert.IsFalse(new MarketplaceConnectionStore(root).List(false).Any(x => x.ShopId == "shop-a"));
    }

    [TestMethod]
    public void UndoRefusesAPartiallyAppliedOperation()
    {
        var root = NewRoot();
        var opId = Guid.NewGuid().ToString("N");
        var preview = PreviewFor(opId, Row("SKU-1"));
        var catalog = new CatalogStore(root);
        var receipt = catalog.ApplyMigration(preview.ReadyRows, "migration:" + opId);
        var journal = new MigrationJournalStore(root);
        var journalId = journal.InsertCatalogPhase(opId, "fixture.xlsx", receipt, "");

        var service = new MigrationAssistantService(root);
        var ex = Assert.ThrowsException<InvalidOperationException>(() => service.Undo(journalId));
        StringAssert.Contains(ex.Message, "RECOVERY_REQUIRED");
    }

    [TestMethod]
    public void UndoNeverDeletesATaxonomyEntryStillUsedByAnUnrelatedProduct()
    {
        var root = NewRoot();
        // A pre-existing, unrelated product already uses this brand before the
        // migration operation under test even runs.
        new CatalogStore(root).ApplyMigration([Row("SKU-OTHER", brand: "SharedBrand")], "manual:other");
        var service = new MigrationAssistantService(root);
        var preview = PreviewFor(Guid.NewGuid().ToString("N"), Row("SKU-1", brand: "SharedBrand", stock: 0));
        var result = service.Apply(preview, true, "");

        service.Undo(result.JournalId);
        Assert.AreEqual(0, new CatalogStore(root).Products().Count(p => p.Sku == "SKU-1"), "The operation's own catalog row must still be undone.");
        Assert.AreEqual(1, new TaxonomyStore(root).List(TaxonomyKind.Brand).Count(x => x.Name == "SharedBrand"), "A brand still used by another product must never be deleted by Undo.");
    }

    [TestMethod]
    public void UndoNeverDeletesAStoreMetadataRowTouchedSinceItWasCreated()
    {
        var root = NewRoot();
        var service = new MigrationAssistantService(root);
        var preview = PreviewFor(Guid.NewGuid().ToString("N"), Row("SKU-1", stock: 0));
        var result = service.Apply(preview, true, "");
        var stores = new MarketplaceConnectionStore(root);
        var created = stores.List(false).Single(x => x.ShopId == "shop-a");
        stores.SetEnabled(created.Id, false); // someone touches it after creation -> Revision advances

        service.Undo(result.JournalId);
        Assert.IsTrue(new MarketplaceConnectionStore(root).List(false).Any(x => x.Id == created.Id), "A store row modified since creation must be left alone by Undo, not silently deleted.");
    }

    [TestMethod]
    public void ApplyingAnAlreadyUndoneOperationAgainIsRejected()
    {
        var root = NewRoot();
        var service = new MigrationAssistantService(root);
        var preview = PreviewFor(Guid.NewGuid().ToString("N"), Row("SKU-1", stock: 0));
        var result = service.Apply(preview, true, "");
        service.Undo(result.JournalId);
        Assert.ThrowsException<InvalidOperationException>(() => service.Apply(preview, true, ""));
    }
}
