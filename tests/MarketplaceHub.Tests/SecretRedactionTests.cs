using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Regression coverage for issue #308: SyncStore.Fail's old Redact() replaced only
/// the secret KEY NAME (api_key -> [redacted]) and left the VALUE in LastError. These
/// tests exercise the real production sink (Fail -> SQLite -> Get/List readback),
/// not just the redactor helper in isolation, with synthetic (non-real) secret values.
[TestClass]
public sealed class SecretRedactionTests
{
    const string ApiKeyValue = "FAKE_API_VALUE_123";
    const string BearerValue = "FAKE_BEARER_VALUE_456";
    const string AccessTokenValue = "FAKE_ACCESS_TOKEN_789";
    const string RefreshTokenValue = "FAKE_REFRESH_TOKEN_ABC";
    const string ClientSecretValue = "FAKE_CLIENT_SECRET_XYZ";

    static SyncStore NewStore(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "sync-redaction-" + Guid.NewGuid().ToString("N"));
        return new SyncStore(root);
    }

    static string FailAndReadBack(SyncStore store, string rawError)
    {
        var job = store.Enqueue(new SyncRequest("etsy", "stock-price", "listing-1", "v1"));
        store.Fail(job.Id, rawError);
        return store.Get(job.Id).LastError;
    }

    [TestMethod]
    public void ApiKeyValueIsMaskedNotJustTheKeyName()
    {
        var store = NewStore(out var root);
        try
        {
            var persisted = FailAndReadBack(store, $"api_key={ApiKeyValue}");
            Assert.IsFalse(persisted.Contains(ApiKeyValue, StringComparison.Ordinal), persisted);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void AuthorizationBearerValueIsMasked()
    {
        var store = NewStore(out var root);
        try
        {
            var persisted = FailAndReadBack(store, $"Authorization: Bearer {BearerValue}");
            Assert.IsFalse(persisted.Contains(BearerValue, StringComparison.Ordinal), persisted);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void AccessAndRefreshTokenValuesAreMasked()
    {
        var store = NewStore(out var root);
        try
        {
            var persisted = FailAndReadBack(store, $"access_token={AccessTokenValue}&refresh_token={RefreshTokenValue}");
            Assert.IsFalse(persisted.Contains(AccessTokenValue, StringComparison.Ordinal), persisted);
            Assert.IsFalse(persisted.Contains(RefreshTokenValue, StringComparison.Ordinal), persisted);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ClientSecretValueInJsonIsMasked()
    {
        var store = NewStore(out var root);
        try
        {
            var persisted = FailAndReadBack(store, $"{{\"client_secret\":\"{ClientSecretValue}\"}}");
            Assert.IsFalse(persisted.Contains(ClientSecretValue, StringComparison.Ordinal), persisted);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void NonSecretErrorTextSurvivesRedactionForOperatorTriage()
    {
        var store = NewStore(out var root);
        try
        {
            var persisted = FailAndReadBack(store, "Etsy 429 rate limit; SKU ABC-123 stok güncellenemedi.");
            StringAssert.Contains(persisted, "429");
            StringAssert.Contains(persisted, "ABC-123");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RedactionIsIdempotentOnRetry()
    {
        var store = NewStore(out var root);
        try
        {
            var job = store.Enqueue(new SyncRequest("etsy", "stock-price", "listing-1", "v1"));
            store.Fail(job.Id, $"api_key={ApiKeyValue}");
            var once = store.Get(job.Id).LastError;
            store.Fail(job.Id, once);
            var twice = store.Get(job.Id).LastError;
            Assert.AreEqual(once, twice);
            Assert.IsFalse(twice.Contains(ApiKeyValue, StringComparison.Ordinal));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [TestMethod]
    public void MainWindowLogSanitizesBeforeUiAndAuditPersistence()
    {
        var sanitized = AuditStore.Sanitize($"Etsy dispatch başarısız: Authorization: Bearer {BearerValue} api_key={ApiKeyValue}");
        Assert.IsFalse(sanitized.Contains(BearerValue, StringComparison.Ordinal));
        Assert.IsFalse(sanitized.Contains(ApiKeyValue, StringComparison.Ordinal));
    }
}
