using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop.Trendyol;

namespace MarketplaceHub.Tests;

[TestClass]
public class TrendyolProductActivityTests
{
    string directory;
    TrendyolWorkspaceStore store;
    SqliteConnection connection;
    static readonly DateTime Epoch = new(2026, 9, 18, 9, 0, 0, DateTimeKind.Utc);

    [TestInitialize]
    public void Setup()
    {
        directory = Path.Combine(Path.GetTempPath(), "trendyol-activity-" + Guid.NewGuid().ToString("N"));
        store = new TrendyolWorkspaceStore(directory);
        connection = new SqliteConnection("Data Source=" + Path.Combine(directory, "catalog.db"));
        connection.Open();
    }

    [TestCleanup]
    public void Cleanup()
    {
        connection?.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    [TestMethod]
    public void LatestCreateAndUpdateMessagesAreSelectedIndependentlyByReceiptTime()
    {
        Save("new-update", "10", TrendyolOperation.Price, Epoch.AddHours(4), "Kuyrukta", "Yeni fiyat gönderimi", [Row("P1")]);
        Save("old-create", "10", TrendyolOperation.Create, Epoch, "Hatalı", "Eski oluşturma", [Row("P1")]);
        Save("new-create", "10", TrendyolOperation.Create, Epoch.AddHours(2), "İşlem tamamlandı", "Yeni oluşturma", [Row("P1")]);
        Save("old-update", "10", TrendyolOperation.Stock, Epoch.AddHours(1), "Hatalı", "Eski stok gönderimi", [Row("P1")]);
        var activity = store.ProductActivities("10")["P1"];
        StringAssert.Contains(activity.CreateMessage, "İşlem tamamlandı");
        StringAssert.Contains(activity.CreateMessage, "Yeni oluşturma");
        StringAssert.Contains(activity.CreateMessage, "18.09.2026 11:00 UTC");
        Assert.IsFalse(activity.CreateMessage.Contains("Eski"));
        StringAssert.Contains(activity.UpdateMessage, "Kuyrukta");
        StringAssert.Contains(activity.UpdateMessage, "Yeni fiyat gönderimi");
        StringAssert.Contains(activity.UpdateMessage, "18.09.2026 13:00 UTC");
        Assert.IsFalse(activity.UpdateMessage.Contains("Eski"));
    }

    [TestMethod]
    public void ActivityIsIsolatedBySellerEvenForTheSameLocalProduct()
    {
        Save("seller-10", "10", TrendyolOperation.Create, Epoch, "Kuyrukta", "Birinci mağaza", [Row("P1")]);
        Save("seller-20", "20", TrendyolOperation.Price, Epoch.AddDays(1), "Hatalı", "Başka mağaza ayrıntısı", [Row("P1")]);
        var first = store.ProductActivities("10");
        Assert.AreEqual(1, first.Count);
        StringAssert.Contains(first["P1"].CreateMessage, "Birinci mağaza");
        Assert.AreEqual("", first["P1"].UpdateMessage);
        Assert.IsFalse(first["P1"].CreateMessage.Contains("Başka"));
        Assert.AreEqual("", store.ProductActivities("20")["P1"].CreateMessage);
        Assert.AreEqual(0, store.ProductActivities("30").Count);
    }

    [TestMethod]
    public void PreviewOnlyPlansNeverAppearAsSentActivity()
    {
        Save("preview", "10", TrendyolOperation.Create, Epoch, "", "", [Row("P1")], includeReceipt: false);
        Assert.AreEqual(0, store.ProductActivities("10").Count);
    }

    [TestMethod]
    public void MultiProductResultsUseABatchLabelAndNeverLeakAnotherProductsDetails()
    {
        Save("batch", "10", TrendyolOperation.Create, Epoch, "Hatalı", "P2: yalnız ikinci ürünün gizli hata ayrıntısı", [Row("P1"), Row("P2")]);
        var activities = store.ProductActivities("10");
        Assert.AreEqual(2, activities.Count);
        foreach (var activity in activities.Values)
        {
            StringAssert.StartsWith(activity.CreateMessage, "Toplu gönderim:");
            StringAssert.Contains(activity.CreateMessage, "Hatalı");
            StringAssert.Contains(activity.CreateMessage, "18.09.2026 09:00 UTC");
            StringAssert.Contains(activity.CreateMessage, "işlem geçmişinde");
            Assert.IsFalse(activity.CreateMessage.Contains("P2:"));
            Assert.IsFalse(activity.CreateMessage.Contains("gizli"));
        }
    }

    [TestMethod]
    public void SkippedPreviewRowsAreNotAttributedASendResult()
    {
        Save("mixed", "10", TrendyolOperation.Stock, Epoch, "İşlem tamamlandı", "Gönderilen ürün ayrıntısı", [Row("P1"), Row("P2") with { ItemJson = null, Status = "Atlanacak" }]);
        var activities = store.ProductActivities("10");
        Assert.IsTrue(activities.ContainsKey("P1"));
        Assert.IsFalse(activities.ContainsKey("P2"));
        StringAssert.StartsWith(activities["P1"].UpdateMessage, "Toplu gönderim:");
        Assert.IsFalse(activities["P1"].UpdateMessage.Contains("Gönderilen ürün ayrıntısı"));
    }

    [TestMethod]
    public void OnlyTheLatestThreeHundredReceiptsForTheSelectedSellerAreIncluded()
    {
        Save("old", "10", TrendyolOperation.Create, Epoch, "Kuyrukta", "", [Row("OLD")]);
        using (var transaction = connection.BeginTransaction())
        {
            for (var index = 1; index <= 300; index++)
                Save("recent-" + index, "10", TrendyolOperation.Stock, Epoch.AddMinutes(index), "Kuyrukta", "", [Row("P" + index)], transaction: transaction);
            Save("foreign", "20", TrendyolOperation.Stock, Epoch.AddDays(1), "Kuyrukta", "", [Row("FOREIGN")], transaction: transaction);
            transaction.Commit();
        }
        var activities = store.ProductActivities("10");
        Assert.AreEqual(300, activities.Count);
        Assert.IsFalse(activities.ContainsKey("OLD"));
        Assert.IsFalse(activities.ContainsKey("FOREIGN"));
        Assert.IsTrue(activities.ContainsKey("P1"));
    }

    [TestMethod]
    public void ActivityUsesThePersistedPlanSnapshot()
    {
        var rows = new List<TrendyolPreviewRow> { Row("ORIGINAL") };
        Save("persisted", "10", TrendyolOperation.UpdateUnapproved, Epoch, "İşleniyor", "Gerçek kayıt", rows);
        rows[0] = Row("CHANGED");
        var activities = store.ProductActivities("10");
        Assert.IsTrue(activities.ContainsKey("ORIGINAL"));
        Assert.IsFalse(activities.ContainsKey("CHANGED"));
        StringAssert.Contains(activities["ORIGINAL"].UpdateMessage, "Gerçek kayıt");
        Assert.AreEqual("", activities["ORIGINAL"].CreateMessage);
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void MismatchedSerializedSellerIsRejectedWithoutLeakingActivity(bool corruptPlan)
    {
        Save("tampered", "10", TrendyolOperation.Create, Epoch, "Hatalı", "Başka hesap ayrıntısı", [Row("P1")]);
        using var command = connection.CreateCommand();
        command.CommandText = corruptPlan
            ? "UPDATE TrendyolPlans SET Json=json_set(Json,'$.SellerId','20') WHERE Id='tampered'"
            : "UPDATE TrendyolReceipts SET Json=json_set(Json,'$.SellerId','20') WHERE PlanId='tampered'";
        command.ExecuteNonQuery();
        var error = Assert.ThrowsException<InvalidDataException>(() => store.ProductActivities("10"));
        Assert.IsFalse(error.Message.Contains("Başka hesap"));
    }

    static TrendyolPreviewRow Row(string id) => new(id, "SKU-" + id, "Product", "BARCODE-" + id, "Güncellenecek", "Preview only", "{\"barcode\":\"BARCODE-" + id + "\"}");

    void Save(string id, string seller, TrendyolOperation operation, DateTime createdUtc, string status, string detail,
        IReadOnlyList<TrendyolPreviewRow> rows, bool includeReceipt = true, SqliteTransaction transaction = null)
    {
        var plan = new TrendyolPlan(id, seller, "fixture-fingerprint", 0, createdUtc.AddMinutes(-1), operation, "fixture-catalog", rows, "{\"items\":[]}");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO TrendyolPlans VALUES($id,$seller,$json)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$seller", seller);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(plan));
        command.ExecuteNonQuery();
        if (!includeReceipt) return;
        command.Parameters.Clear();
        command.CommandText = "INSERT INTO TrendyolReceipts VALUES($id,$seller,$hash,$created,$json)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$seller", seller);
        command.Parameters.AddWithValue("$hash", "fixture-hash");
        command.Parameters.AddWithValue("$created", createdUtc.ToString("O"));
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(new TrendyolReceipt(id, seller, createdUtc, operation.ToString(), status, "fixture-batch", detail)));
        command.ExecuteNonQuery();
    }
}
