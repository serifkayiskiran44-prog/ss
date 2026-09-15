using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

/// Coverage for #2651: OrderExportTemplateStore.List/Find must isolate a
/// corrupt persisted row (malformed JSON, invalid timestamp, or a field ID no
/// longer valid in OrderExportFieldRegistry) instead of crashing the whole
/// template list or silently starting an export with a stale/partial field
/// selection.
[TestClass]
public sealed class OrderExportTemplateCorruptionTests
{
    static string NewRoot() => Path.Combine(Path.GetTempPath(), "order-export-corrupt-" + Guid.NewGuid().ToString("N"));

    static void WithRoot(Action<string> test)
    {
        var root = NewRoot();
        try { test(root); }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    static void InsertRawRow(string root, string id, string name, string fieldIdsJson, int version = 1, string? updatedUtc = null)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO OrderExportTemplates(Id,Name,FieldIdsJson,Version,UpdatedUtc) VALUES($id,$name,$fields,$version,$updated)";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$name", name); cmd.Parameters.AddWithValue("$fields", fieldIdsJson);
        cmd.Parameters.AddWithValue("$version", version); cmd.Parameters.AddWithValue("$updated", updatedUtc ?? DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    [TestMethod]
    public void NormalTemplateListFindUnaffected() => WithRoot(root =>
    {
        var store = new OrderExportTemplateStore(root);
        var saved = store.Save(new OrderExportTemplate { Name = "Standart", FieldIds = ["Marketplace", "OrderId"] });
        Assert.AreEqual(1, store.List().Count);
        Assert.IsNotNull(store.Find(saved.Id));
        Assert.AreEqual(0, store.CorruptTemplates().Count);
    });

    [TestMethod]
    public void OneMalformedRowDoesNotHideHealthyTemplates() => WithRoot(root =>
    {
        var store = new OrderExportTemplateStore(root);
        store.Save(new OrderExportTemplate { Name = "Sağlıklı", FieldIds = ["Marketplace"] });
        InsertRawRow(root, "bad1", "Bozuk", "{not-json");
        Assert.AreEqual(1, store.List().Count);
        Assert.AreEqual(1, store.CorruptTemplates().Count);
    });

    [TestMethod]
    public void TruncatedJsonIsQuarantined() => WithRoot(root =>
    {
        var store = new OrderExportTemplateStore(root);
        InsertRawRow(root, "bad1", "Bozuk", "[\"Marketplace\"");
        Assert.AreEqual(0, store.List().Count);
        Assert.AreEqual(1, store.CorruptTemplates().Count);
    });

    [TestMethod]
    public void JsonNullIsQuarantined() => WithRoot(root =>
    {
        var store = new OrderExportTemplateStore(root);
        InsertRawRow(root, "bad1", "Bozuk", "null");
        Assert.AreEqual(0, store.List().Count);
        Assert.AreEqual(1, store.CorruptTemplates().Count);
    });

    [TestMethod]
    public void WrongJsonTypeIsQuarantined() => WithRoot(root =>
    {
        var store = new OrderExportTemplateStore(root);
        InsertRawRow(root, "bad1", "Bozuk", "{\"not\":\"an-array\"}");
        Assert.AreEqual(0, store.List().Count);
        Assert.AreEqual(1, store.CorruptTemplates().Count);
    });

    [TestMethod]
    public void EmptyFieldArrayIsQuarantinedNotSilentlyTreatedAsValid() => WithRoot(root =>
    {
        var store = new OrderExportTemplateStore(root);
        InsertRawRow(root, "bad1", "Bozuk", "[]");
        Assert.AreEqual(0, store.List().Count);
        Assert.AreEqual(1, store.CorruptTemplates().Count);
    });

    [TestMethod]
    public void UnknownFieldIdNoLongerInRegistryIsQuarantinedOnRead() => WithRoot(root =>
    {
        var store = new OrderExportTemplateStore(root);
        InsertRawRow(root, "bad1", "Eski", "[\"Marketplace\",\"RetiredFieldId\"]");
        Assert.AreEqual(0, store.List().Count);
        Assert.AreEqual(1, store.CorruptTemplates().Count);
        Assert.ThrowsException<OrderExportTemplateCorruptException>(() => store.Find("bad1"));
    });

    [TestMethod]
    public void MalformedTimestampAloneDoesNotDropTheWholeList() => WithRoot(root =>
    {
        var store = new OrderExportTemplateStore(root);
        store.Save(new OrderExportTemplate { Name = "Sağlıklı", FieldIds = ["Marketplace"] });
        InsertRawRow(root, "bad1", "Bozuk", "[\"Marketplace\"]", updatedUtc: "not-a-date");
        Assert.AreEqual(1, store.List().Count);
        Assert.AreEqual(1, store.CorruptTemplates().Count);
    });

    [TestMethod]
    public void FindDistinguishesCorruptFromTrulyMissing() => WithRoot(root =>
    {
        var store = new OrderExportTemplateStore(root);
        InsertRawRow(root, "bad1", "Bozuk", "{not-json");
        Assert.ThrowsException<OrderExportTemplateCorruptException>(() => store.Find("bad1"));
        Assert.IsNull(store.Find("does-not-exist"));
    });

    [TestMethod]
    public void OversizedFieldIdsJsonIsQuarantined() => WithRoot(root =>
    {
        var store = new OrderExportTemplateStore(root);
        var huge = "[" + string.Join(",", Enumerable.Repeat("\"Marketplace\"", 20000)) + "]";
        InsertRawRow(root, "bad1", "Bozuk", huge);
        Assert.AreEqual(0, store.List().Count);
        Assert.AreEqual(1, store.CorruptTemplates().Count);
    });

    [TestMethod]
    public void RestartKeepsCorruptionStateDeterministic() => WithRoot(root =>
    {
        _ = new OrderExportTemplateStore(root);
        InsertRawRow(root, "bad1", "Bozuk", "{not-json");
        var reopened = new OrderExportTemplateStore(root);
        Assert.AreEqual(0, reopened.List().Count);
        Assert.AreEqual(1, reopened.CorruptTemplates().Count);
    });

    [TestMethod]
    public void UserCanExplicitlyDeleteTheCorruptRow() => WithRoot(root =>
    {
        var store = new OrderExportTemplateStore(root);
        InsertRawRow(root, "bad1", "Bozuk", "{not-json");
        store.DeleteCorruptTemplate("bad1");
        Assert.AreEqual(0, store.CorruptTemplates().Count);
    });

    [TestMethod]
    public void ReplacementCanOnlyBeCreatedAfterExplicitDeleteOfTheCorruptRow() => WithRoot(root =>
    {
        var store = new OrderExportTemplateStore(root);
        InsertRawRow(root, "bad1", "AynıAd", "{not-json");
        // Saving a brand-new template under a fresh Id but colliding Name must
        // still work (corrupt rows never block unrelated new templates) - the
        // corrupt row itself is left untouched until explicitly deleted.
        var created = store.Save(new OrderExportTemplate { Name = "Farklı", FieldIds = ["Marketplace"] });
        Assert.AreEqual(1, store.List().Count);
        Assert.AreEqual(1, store.CorruptTemplates().Count);
        Assert.AreEqual("Farklı", store.Find(created.Id)!.Name);
    });
}
