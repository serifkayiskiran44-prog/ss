using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

/// Coverage for #2667: deleting an order export template must be
/// compare-and-delete against the caller's Version snapshot, and a
/// delete+recreate under the same Id must never let an old snapshot delete
/// the new generation.
[TestClass]
public sealed class OrderExportTemplateDeleteRevisionTests
{
    static void WithRoot(Action<string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "order-export-delete-" + Guid.NewGuid().ToString("N"));
        try { test(root); }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    static void InsertRawRow(string root, string id, string name, string fieldIdsJson, object version)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO OrderExportTemplates(Id,Name,FieldIdsJson,Version,UpdatedUtc) VALUES($id,$name,$fields,$version,$updated)";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$name", name); cmd.Parameters.AddWithValue("$fields", fieldIdsJson);
        cmd.Parameters.AddWithValue("$version", version); cmd.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    [TestMethod]
    public void DeleteWithCurrentVersionSucceedsExactlyOnce() => WithRoot(root =>
    {
        var store = new OrderExportTemplateStore(root);
        var created = store.Save(new OrderExportTemplate { Name = "Standart", FieldIds = ["Marketplace"] });
        Assert.AreEqual(OrderExportTemplateDeleteResult.Deleted, store.Delete(created.Id, created.Version));
        Assert.AreEqual(0, store.List().Count);
        Assert.AreEqual(OrderExportTemplateDeleteResult.AlreadyDeleted, store.Delete(created.Id, created.Version));
    });

    [TestMethod]
    public void StaleSnapshotCannotDeleteANewerVersion() => WithRoot(root =>
    {
        var store = new OrderExportTemplateStore(root);
        var snapshot = store.Save(new OrderExportTemplate { Name = "Standart", FieldIds = ["Marketplace"] });
        var staleVersion = snapshot.Version;
        new OrderExportTemplateStore(root).Save(new OrderExportTemplate { Id = snapshot.Id, Name = "Standart", FieldIds = ["OrderId"], Version = staleVersion });

        Assert.AreEqual(OrderExportTemplateDeleteResult.Stale, store.Delete(snapshot.Id, staleVersion));
        var survivor = store.Find(snapshot.Id);
        Assert.IsNotNull(survivor, "A stale delete must remove zero rows.");
        CollectionAssert.AreEqual(new[] { "OrderId" }, survivor!.FieldIds);
    });

    [TestMethod]
    public void OfManyConcurrentDeletesExactlyOneSucceeds() => WithRoot(root =>
    {
        var created = new OrderExportTemplateStore(root).Save(new OrderExportTemplate { Name = "Standart", FieldIds = ["Marketplace"] });
        using var gate = new Barrier(8);
        var results = Enumerable.Range(0, 8).Select(_ => Task.Run(() => { var store = new OrderExportTemplateStore(root); gate.SignalAndWait(); return store.Delete(created.Id, created.Version); })).ToArray();
        Task.WaitAll(results);

        Assert.AreEqual(1, results.Count(t => t.Result == OrderExportTemplateDeleteResult.Deleted));
        Assert.AreEqual(7, results.Count(t => t.Result == OrderExportTemplateDeleteResult.AlreadyDeleted));
    });

    [TestMethod]
    public void DeleteThenRecreateUnderTheSameIdCannotBeDeletedByTheOldSnapshot() => WithRoot(root =>
    {
        var store = new OrderExportTemplateStore(root);
        var original = store.Save(new OrderExportTemplate { Name = "Standart", FieldIds = ["Marketplace"] });
        var oldVersion = original.Version;
        Assert.AreEqual(OrderExportTemplateDeleteResult.Deleted, store.Delete(original.Id, oldVersion));

        var recreated = store.Save(new OrderExportTemplate { Id = original.Id, Name = "Standart", FieldIds = ["Total"] });
        Assert.IsTrue(recreated.Version > oldVersion, "A recreated template must never reuse a Version an old snapshot may still hold.");
        Assert.AreEqual(OrderExportTemplateDeleteResult.Stale, store.Delete(original.Id, oldVersion));
        Assert.IsNotNull(store.Find(original.Id));
    });

    [TestMethod]
    public void RevisionFloorSurvivesRestart() => WithRoot(root =>
    {
        var original = new OrderExportTemplateStore(root).Save(new OrderExportTemplate { Name = "Standart", FieldIds = ["Marketplace"] });
        new OrderExportTemplateStore(root).Delete(original.Id, original.Version);

        var reopened = new OrderExportTemplateStore(root);
        var recreated = reopened.Save(new OrderExportTemplate { Id = original.Id, Name = "Standart", FieldIds = ["Marketplace"] });
        Assert.IsTrue(recreated.Version > original.Version);
        Assert.AreEqual(OrderExportTemplateDeleteResult.Stale, new OrderExportTemplateStore(root).Delete(original.Id, original.Version));
    });

    [TestMethod]
    public void SaveWithAPreDeleteSnapshotIsStillRejectedAfterRecreate() => WithRoot(root =>
    {
        var store = new OrderExportTemplateStore(root);
        var original = store.Save(new OrderExportTemplate { Name = "Standart", FieldIds = ["Marketplace"] });
        store.Delete(original.Id, original.Version);
        store.Save(new OrderExportTemplate { Id = original.Id, Name = "Standart", FieldIds = ["OrderId"] });

        Assert.ThrowsException<InvalidOperationException>(() => store.Save(new OrderExportTemplate { Id = original.Id, Name = "Standart", FieldIds = ["Total"], Version = original.Version }));
    });

    [TestMethod]
    public void NormalDeleteRefusesACorruptRowAndTheRecoveryPathRemovesIt() => WithRoot(root =>
    {
        var store = new OrderExportTemplateStore(root);
        InsertRawRow(root, "bad1", "Bozuk", "{not-json", 1);

        Assert.ThrowsException<OrderExportTemplateCorruptException>(() => store.Delete("bad1", 1));
        Assert.AreEqual(1, store.CorruptTemplates().Count, "A corrupt row must not be deleted through the normal snapshot path.");

        store.DeleteCorruptTemplate("bad1");
        Assert.AreEqual(0, store.CorruptTemplates().Count);
    });

    [TestMethod]
    public void RecoveryDeleteRefusesAHealthyRow() => WithRoot(root =>
    {
        var store = new OrderExportTemplateStore(root);
        var healthy = store.Save(new OrderExportTemplate { Name = "Standart", FieldIds = ["Marketplace"] });
        Assert.ThrowsException<InvalidOperationException>(() => store.DeleteCorruptTemplate(healthy.Id));
        Assert.IsNotNull(store.Find(healthy.Id));
    });

    [TestMethod]
    public void ZeroNegativeAndOverflowingLegacyVersionsAreQuarantined() => WithRoot(root =>
    {
        var store = new OrderExportTemplateStore(root);
        InsertRawRow(root, "zero", "Sıfır", "[\"Marketplace\"]", 0);
        InsertRawRow(root, "neg", "Negatif", "[\"Marketplace\"]", -3);
        InsertRawRow(root, "huge", "Taşan", "[\"Marketplace\"]", 3_000_000_000L);

        Assert.AreEqual(0, store.List().Count);
        Assert.AreEqual(3, store.CorruptTemplates().Count);
        Assert.ThrowsException<OrderExportTemplateCorruptException>(() => store.Find("huge"));
    });

    [TestMethod]
    public void RecreateAfterCorruptRecoveryDeleteNeverReusesTheOldVersion() => WithRoot(root =>
    {
        var store = new OrderExportTemplateStore(root);
        InsertRawRow(root, "bad1", "Bozuk", "{not-json", 5);
        store.DeleteCorruptTemplate("bad1");

        var recreated = store.Save(new OrderExportTemplate { Id = "bad1", Name = "Yeni", FieldIds = ["Marketplace"] });
        Assert.AreEqual(6, recreated.Version);
    });
}
