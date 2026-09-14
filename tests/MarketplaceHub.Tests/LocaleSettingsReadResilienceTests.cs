using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

/// Coverage for #2643: a malformed persisted row in StoreLocaleSettings must
/// not crash List(), Get() must distinguish a corrupt row from a genuinely
/// missing one, and Save() must never silently overwrite a corrupt existing
/// row under the same (channel, shop) identity.
[TestClass]
public sealed class LocaleSettingsReadResilienceTests
{
    static LocaleSettingsStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "locale-resilience-" + Guid.NewGuid().ToString("N"));
        return new LocaleSettingsStore(root);
    }

    static void InsertRawRow(string root, string channel, string shopId, string updatedUtc, object? vatRate = null, object? version = null, string currency = "TRY", string culture = "tr-TR")
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "locale-settings.db") }.ToString());
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO StoreLocaleSettings(Channel,ShopId,Currency,CultureName,VatRate,DatePattern,Version,UpdatedUtc) VALUES($channel,$shop,$currency,$culture,$vat,'dd.MM.yyyy',$version,$updated)";
        cmd.Parameters.AddWithValue("$channel", channel); cmd.Parameters.AddWithValue("$shop", shopId); cmd.Parameters.AddWithValue("$currency", currency); cmd.Parameters.AddWithValue("$culture", culture);
        cmd.Parameters.AddWithValue("$vat", vatRate ?? 20m); cmd.Parameters.AddWithValue("$version", version ?? 1); cmd.Parameters.AddWithValue("$updated", updatedUtc);
        cmd.ExecuteNonQuery();
    }

    static StoreLocaleSettings Settings(string channel, string shop) => new() { Channel = channel, ShopId = shop };

    [TestMethod]
    public void ValidMultiShopRowsListNormally()
    {
        var store = NewStore(out var root);
        try
        {
            store.Save(Settings("etsy", "shop1"));
            store.Save(Settings("trendyol", "shop2"));
            Assert.AreEqual(2, store.List().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MalformedUpdatedUtcDoesNotDropOtherRows()
    {
        var store = NewStore(out var root);
        try
        {
            store.Save(Settings("etsy", "shop1"));
            InsertRawRow(root, "trendyol", "shop2", "not-a-date");

            var list = store.List();
            Assert.AreEqual(1, list.Count);

            var corrupt = store.CorruptSettings();
            Assert.AreEqual(1, corrupt.Count);
            Assert.AreEqual("trendyol", corrupt[0].Channel);
            Assert.AreEqual("shop2", corrupt[0].ShopId);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MalformedVatRateIsFailClosed()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "trendyol", "shop2", DateTime.UtcNow.ToString("O"), vatRate: "not-a-number");
            Assert.AreEqual(0, store.List().Count);
            Assert.AreEqual(1, store.CorruptSettings().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MalformedVersionIsFailClosed()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "trendyol", "shop2", DateTime.UtcNow.ToString("O"), version: "not-a-number");
            Assert.AreEqual(0, store.List().Count);
            Assert.AreEqual(1, store.CorruptSettings().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void GetThrowsDistinctTypedErrorForCorruptRowVsMissing()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "trendyol", "shop2", "junk");
            Assert.ThrowsException<LocaleSettingsCorruptException>(() => store.Get("trendyol", "shop2"));
            Assert.IsNull(store.Get("trendyol", "does-not-exist"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void SaveOverCorruptExistingRowFailsClosedInsteadOfSilentlyOverwriting()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "trendyol", "shop2", "junk");
            Assert.ThrowsException<LocaleSettingsCorruptException>(() => store.Save(Settings("trendyol", "shop2")));
            Assert.AreEqual(1, store.CorruptSettings().Count, "The corrupt row must still be present, not overwritten.");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void SaveStillWorksForANewIdentity()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "trendyol", "shop2", "junk");
            store.Save(Settings("etsy", "shop1"));
            Assert.AreEqual(1, store.List().Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RestartPreservesCorruptRowDetection()
    {
        var root = Path.Combine(Path.GetTempPath(), "locale-resilience-" + Guid.NewGuid().ToString("N"));
        try
        {
            new LocaleSettingsStore(root);
            InsertRawRow(root, "trendyol", "shop2", "junk");

            var reopened = new LocaleSettingsStore(root);
            Assert.AreEqual(0, reopened.List().Count);
            Assert.AreEqual(1, reopened.CorruptSettings().Count);
            Assert.ThrowsException<LocaleSettingsCorruptException>(() => reopened.Get("trendyol", "shop2"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MultipleCorruptRowsAreAllReportedIndependently()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "a", "s1", "1");
            InsertRawRow(root, "b", "s2", DateTime.UtcNow.ToString("O"), vatRate: "xx");
            InsertRawRow(root, "c", "s3", DateTime.UtcNow.ToString("O"), version: "xx");

            var corrupt = store.CorruptSettings();
            Assert.AreEqual(3, corrupt.Count);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void UnicodeShopIdRoundTrips()
    {
        var store = NewStore(out var root);
        try
        {
            store.Save(Settings("etsy", "Şirket-Mağazası"));
            var loaded = store.Get("etsy", "Şirket-Mağazası");
            Assert.IsNotNull(loaded);
            Assert.AreEqual("Şirket-Mağazası", loaded!.ShopId);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DiagnosticsNeverContainRawCurrencyOrCulture()
    {
        var store = NewStore(out var root);
        try
        {
            InsertRawRow(root, "trendyol", "shop2", "junk", currency: "XYZ", culture: "xx-XX");
            var corrupt = store.CorruptSettings().Single();
            StringAssert.DoesNotMatch(corrupt.Reason, new System.Text.RegularExpressions.Regex("XYZ|xx-XX"));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
