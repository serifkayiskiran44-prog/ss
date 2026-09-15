using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

/// Coverage for #2660: a persisted Status/Direction that fails to parse must
/// never silently become a valid business state (e.g. Status -> Unread).
[TestClass]
public sealed class MessageEnumIdentityIntegrityTests
{
    static string NewRoot() => Path.Combine(Path.GetTempPath(), "message-enum-integrity-" + Guid.NewGuid().ToString("N"));

    static void WithRoot(Action<string> test)
    {
        var root = NewRoot();
        try { test(root); }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    static void InsertRaw(string root, string id, string status, string direction)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "messages.db") }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO Messages VALUES($id,'etsy','shop1','','','','','subj','body',$direction,$status,$created,$updated,'')";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$status", status); cmd.Parameters.AddWithValue("$direction", direction);
        cmd.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O")); cmd.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    static MessageRecord Valid() => new() { Marketplace = "etsy", ShopId = "shop1", Subject = "Konu", Body = "Metin", Direction = "Inbound", Status = MessageStatus.Unread };

    [TestMethod]
    public void NormalStatusAndDirectionRoundTripUnaffected() => WithRoot(root =>
    {
        var store = new MessageStore(root);
        var saved = store.Upsert(Valid());
        Assert.AreEqual(MessageStatus.Unread, store.Get(saved.Id)!.Status);
        Assert.AreEqual("Inbound", store.Get(saved.Id)!.Direction);
        Assert.AreEqual(0, store.CorruptMessages().Count);
    });

    [DataTestMethod]
    [DataRow("Unknown")]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("unread")]
    [DataRow("UNREAD")]
    [DataRow("0")]
    public void UnrecognizedOrWronglyCasedStatusNeverSilentlyBecomesUnread(string rawStatus) => WithRoot(root =>
    {
        var store = new MessageStore(root);
        InsertRaw(root, "m1", rawStatus, "Inbound");
        Assert.ThrowsException<MessageCorruptException>(() => store.Get("m1"));
        Assert.AreEqual(0, store.List().Count);
        Assert.AreEqual(1, store.CorruptMessages().Count);
    });

    [DataTestMethod]
    [DataRow("Outgoing")]
    [DataRow("")]
    [DataRow("inbound")]
    [DataRow("INBOUND")]
    public void UnrecognizedDirectionIsQuarantinedNotAcceptedAsValid(string rawDirection) => WithRoot(root =>
    {
        var store = new MessageStore(root);
        InsertRaw(root, "m1", "Unread", rawDirection);
        Assert.ThrowsException<MessageCorruptException>(() => store.Get("m1"));
        Assert.AreEqual(1, store.CorruptMessages().Count);
    });

    [TestMethod]
    public void CorruptRowDoesNotHideHealthyMessagesInList() => WithRoot(root =>
    {
        var store = new MessageStore(root);
        store.Upsert(Valid());
        InsertRaw(root, "m-bad", "Unknown", "Inbound");
        Assert.AreEqual(1, store.List().Count);
        Assert.AreEqual(1, store.CorruptMessages().Count);
    });

    [TestMethod]
    public void UpsertRejectsInvalidDirectionOnWrite() => WithRoot(root =>
    {
        var store = new MessageStore(root);
        var bad = Valid(); bad.Direction = "Sideways";
        Assert.ThrowsException<ArgumentException>(() => store.Upsert(bad));
    });

    [TestMethod]
    public void DeleteCorruptMessageRemovesOnlyTheCorruptEnumRow() => WithRoot(root =>
    {
        var store = new MessageStore(root);
        InsertRaw(root, "m-bad", "Unknown", "Inbound");
        store.DeleteCorruptMessage("m-bad");
        Assert.AreEqual(0, store.CorruptMessages().Count);
    });

    [TestMethod]
    public void DeleteCorruptMessageRefusesAHealthyRow() => WithRoot(root =>
    {
        var store = new MessageStore(root);
        var saved = store.Upsert(Valid());
        Assert.ThrowsException<InvalidOperationException>(() => store.DeleteCorruptMessage(saved.Id));
    });

    [TestMethod]
    public void RestartKeepsEnumCorruptionClassificationDeterministic() => WithRoot(root =>
    {
        _ = new MessageStore(root);
        InsertRaw(root, "m-bad", "Unknown", "Inbound");
        var reopened = new MessageStore(root);
        Assert.ThrowsException<MessageCorruptException>(() => reopened.Get("m-bad"));
        Assert.AreEqual(1, reopened.CorruptMessages().Count);
    });

    [TestMethod]
    public void MalformedTimestampTogetherWithMalformedStatusStaysBoundedAndDeterministic() => WithRoot(root =>
    {
        var store = new MessageStore(root);
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "messages.db") }.ToString()))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO Messages VALUES('m-bad','etsy','shop1','','','','','subj','body','Inbound','Unknown','not-a-date','not-a-date','')";
            cmd.ExecuteNonQuery();
        }
        Assert.ThrowsException<MessageCorruptException>(() => store.Get("m-bad"));
        Assert.AreEqual(1, store.CorruptMessages().Count);
    });
}
