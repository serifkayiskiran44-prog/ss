using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

/// Coverage for #2633: a malformed persisted timestamp in Messages or
/// MessageTemplates must not crash List()/Templates(), must never resolve
/// to a default date, and Get() must distinguish a corrupt row from a
/// genuinely missing one; recovery deletion is explicit and re-validated.
[TestClass]
public sealed class MessageReadResilienceTests
{
    static MessageStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "message-resilience-" + Guid.NewGuid().ToString("N"));
        return new MessageStore(root);
    }

    static void InsertRawMessage(string root, string id, string marketplace, string shop, string created, string updated)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "messages.db") }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO Messages VALUES($id,$m,$s,'','','','','subj','body','Inbound','Unread',$created,$updated,'')";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$m", marketplace); cmd.Parameters.AddWithValue("$s", shop);
        cmd.Parameters.AddWithValue("$created", created); cmd.Parameters.AddWithValue("$updated", updated);
        cmd.ExecuteNonQuery();
    }

    static void InsertRawTemplate(string root, string id, string name, string updated)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "messages.db") }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO MessageTemplates VALUES($id,$name,'body',$updated)";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$name", name); cmd.Parameters.AddWithValue("$updated", updated);
        cmd.ExecuteNonQuery();
    }

    static MessageRecord Message(string marketplace = "etsy") => new() { Marketplace = marketplace, ShopId = "shop1", Subject = "Konu", Body = "Metin" };

    [TestMethod]
    public void ValidMessagesRoundTripNormally()
    {
        var store = NewStore(out var root);
        try
        {
            var saved = store.Upsert(Message());
            Assert.AreEqual(1, store.List().Count);
            Assert.AreEqual(saved.Id, store.Get(saved.Id)!.Id);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MalformedCreatedUtcDoesNotBlockOtherMessages()
    {
        var store = NewStore(out var root);
        try
        {
            store.Upsert(Message());
            InsertRawMessage(root, "bad-1", "trendyol", "shop2", "not-a-date", DateTime.UtcNow.ToString("O"));

            var list = store.List();
            Assert.AreEqual(1, list.Count);

            var corrupt = store.CorruptMessages();
            Assert.AreEqual(1, corrupt.Count);
            Assert.AreEqual("bad-1", corrupt[0].Id);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MalformedUpdatedUtcDoesNotBlockOtherMessages()
    {
        var store = NewStore(out var root);
        try
        {
            store.Upsert(Message());
            InsertRawMessage(root, "bad-1", "trendyol", "shop2", DateTime.UtcNow.ToString("O"), "junk");

            Assert.AreEqual(1, store.List().Count);
            Assert.AreEqual(1, store.CorruptMessages().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void CorruptMessageNeverResolvesToDefaultDate()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawMessage(root, "bad-1", "trendyol", "shop2", "", "");
            Assert.IsFalse(store.List().Any(m => m.Id == "bad-1"));
            Assert.AreEqual(1, store.CorruptMessages().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void GetThrowsDistinctTypedErrorForCorruptRowVsMissing()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawMessage(root, "bad-1", "trendyol", "shop2", "junk", DateTime.UtcNow.ToString("O"));
            Assert.ThrowsException<MessageCorruptException>(() => store.Get("bad-1"));
            Assert.IsNull(store.Get("does-not-exist"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MalformedTemplateUpdatedUtcDoesNotBlockOtherTemplates()
    {
        var store = NewStore(out var root);
        try
        {
            store.SaveTemplate("Iyi", "gövde");
            InsertRawTemplate(root, "bad-1", "Kotu", "junk");

            var templates = store.Templates();
            Assert.AreEqual(1, templates.Count);
            Assert.AreEqual("Iyi", templates[0].Name);

            var corrupt = store.CorruptMessageTemplates();
            Assert.AreEqual(1, corrupt.Count);
            Assert.AreEqual("bad-1", corrupt[0].Id);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DeleteCorruptMessageRemovesOnlyThatRecord()
    {
        var store = NewStore(out var root);
        try
        {
            var healthy = store.Upsert(Message());
            InsertRawMessage(root, "bad-1", "trendyol", "shop2", "junk", DateTime.UtcNow.ToString("O"));

            store.DeleteCorruptMessage("bad-1");

            Assert.AreEqual(0, store.CorruptMessages().Count);
            Assert.AreEqual(1, store.List().Count);
            Assert.AreEqual(healthy.Id, store.List().Single().Id);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DeleteCorruptMessageRefusesAHealthyRow()
    {
        var store = NewStore(out var root);
        try
        {
            var healthy = store.Upsert(Message());
            Assert.ThrowsException<InvalidOperationException>(() => store.DeleteCorruptMessage(healthy.Id));
            Assert.AreEqual(1, store.List().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestartPreservesValidMessagesAndCorruptDetection()
    {
        var root = Path.Combine(Path.GetTempPath(), "message-resilience-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new MessageStore(root);
            store.Upsert(Message());
            InsertRawMessage(root, "bad-1", "trendyol", "shop2", "junk", DateTime.UtcNow.ToString("O"));

            var reopened = new MessageStore(root);
            Assert.AreEqual(1, reopened.List().Count);
            Assert.AreEqual(1, reopened.CorruptMessages().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void CorruptMessageAndCorruptTemplateCoexistIndependently()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawMessage(root, "bad-msg", "etsy", "shop1", "junk", DateTime.UtcNow.ToString("O"));
            InsertRawTemplate(root, "bad-tpl", "Kotu", "junk");

            Assert.AreEqual(1, store.CorruptMessages().Count);
            Assert.AreEqual(1, store.CorruptMessageTemplates().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DiagnosticsNeverContainRawBodyOrCustomer()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawMessage(root, "bad-1", "etsy", "shop1", "junk", DateTime.UtcNow.ToString("O"));
            var corrupt = store.CorruptMessages().Single();
            StringAssert.DoesNotMatch(corrupt.Reason, new System.Text.RegularExpressions.Regex("subj|body"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
