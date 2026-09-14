using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

/// Coverage for issues #1979 (versioned order export field registry, PII
/// fields default-off) and #1980 (named export template CRUD storing only
/// field IDs, never filter results or values).
[TestClass]
public sealed class OrderExportTemplateTests
{
    static OrderExportTemplateStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "order-export-" + Guid.NewGuid().ToString("N"));
        return new OrderExportTemplateStore(root);
    }

    [TestMethod]
    public void SchemaVersionIsStable()
    {
        Assert.AreEqual(1, OrderExportFieldRegistry.SchemaVersion);
    }

    [TestMethod]
    public void AllFieldIdsAreUniqueAndNonEmpty()
    {
        var ids = OrderExportFieldRegistry.Fields.Select(f => f.Id).ToList();
        Assert.IsTrue(ids.All(id => id.Length > 0));
        Assert.AreEqual(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [TestMethod]
    public void AllFieldsHaveNonEmptyLocalizedLabels()
    {
        Assert.IsTrue(OrderExportFieldRegistry.Fields.All(f => !string.IsNullOrWhiteSpace(f.Label)));
    }

    [TestMethod]
    public void PiiFieldsAreNeverDefaultSelected()
    {
        Assert.IsTrue(OrderExportFieldRegistry.Fields.Where(f => f.IsPii).All(f => !f.DefaultSelected),
            "A PII field must not be pre-selected in a fresh template.");
    }

    [TestMethod]
    public void MissingFieldIdIsReportedAsInvalid()
    {
        Assert.IsFalse(OrderExportFieldRegistry.IsValidFieldId("DoesNotExist"));
        Assert.IsNull(OrderExportFieldRegistry.TryGet("DoesNotExist"));
    }

    [TestMethod]
    public void CreateEditAndRemoveTemplateLifecycle()
    {
        var store = NewStore(out var root);
        try
        {
            var created = store.Save(new OrderExportTemplate { Name = "Standart", FieldIds = ["Marketplace", "OrderId"] });
            Assert.AreEqual(1, created.Version);

            var updated = store.Save(new OrderExportTemplate { Id = created.Id, Name = "Standart", FieldIds = ["Marketplace", "OrderId", "Total"], Version = created.Version });
            Assert.AreEqual(created.Id, updated.Id);
            Assert.AreEqual(3, updated.FieldIds.Count);
            Assert.AreEqual(1, store.List().Count, "Editing must update in place, not create a second template.");

            store.Delete(created.Id);
            Assert.AreEqual(0, store.List().Count);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void UnknownFieldIdIsRejected()
    {
        var store = NewStore(out var root);
        try
        {
            Assert.ThrowsException<ArgumentException>(() => store.Save(new OrderExportTemplate { Name = "Bozuk", FieldIds = ["Marketplace", "NoSuchField"] }));
            Assert.AreEqual(0, store.List().Count);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RemovedRegistryFieldStillReferencedByAStoredTemplateFailsOnNextSave()
    {
        // Simulates a field being retired from the registry after a template
        // already referenced it: re-saving that template (e.g. adding another
        // field) must reject the now-invalid ID rather than silently keeping it.
        var store = NewStore(out var root);
        try
        {
            var created = store.Save(new OrderExportTemplate { Name = "Eski", FieldIds = ["Marketplace"] });
            var corrupted = new OrderExportTemplate { Id = created.Id, Name = "Eski", FieldIds = ["Marketplace", "RetiredFieldId"], Version = created.Version };
            Assert.ThrowsException<ArgumentException>(() => store.Save(corrupted));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DuplicateNameIsRejected()
    {
        var store = NewStore(out var root);
        try
        {
            store.Save(new OrderExportTemplate { Name = "Standart", FieldIds = ["Marketplace"] });
            Assert.ThrowsException<InvalidOperationException>(() => store.Save(new OrderExportTemplate { Name = "standart", FieldIds = ["OrderId"] }));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void StaleVersionIsRejected()
    {
        var store = NewStore(out var root);
        try
        {
            var created = store.Save(new OrderExportTemplate { Name = "Standart", FieldIds = ["Marketplace"] });
            store.Save(new OrderExportTemplate { Id = created.Id, Name = "Standart", FieldIds = ["OrderId"], Version = created.Version });
            Assert.ThrowsException<InvalidOperationException>(() => store.Save(new OrderExportTemplate { Id = created.Id, Name = "Standart", FieldIds = ["Total"], Version = created.Version }));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void EmptyFieldSelectionIsRejected()
    {
        var store = NewStore(out var root);
        try
        {
            Assert.ThrowsException<ArgumentException>(() => store.Save(new OrderExportTemplate { Name = "Boş", FieldIds = [] }));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestartPreservesTemplateFieldSelection()
    {
        var root = Path.Combine(Path.GetTempPath(), "order-export-" + Guid.NewGuid().ToString("N"));
        try
        {
            new OrderExportTemplateStore(root).Save(new OrderExportTemplate { Name = "Standart", FieldIds = ["Marketplace", "OrderId", "Total"] });
            var reopened = new OrderExportTemplateStore(root).List().Single();
            Assert.AreEqual("Standart", reopened.Name);
            CollectionAssert.AreEquivalent(new[] { "Marketplace", "OrderId", "Total" }, reopened.FieldIds);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
