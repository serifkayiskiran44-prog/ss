using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class MarketplaceCredentialVaultTests
{
    static void WithRoot(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "marketplace-vault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void TwoTrendyolAndTwoEtsyAccountsRoundTripIndependently() => WithRoot(root =>
    {
        var vault = new MarketplaceCredentialVault(root);
        vault.Save("trendyol-a", "trendyol", "101", new TrendyolSettings("101", "key-a", "secret-a", "101 - Test"));
        vault.Save("trendyol-b", "trendyol", "202", new TrendyolSettings("202", "key-b", "secret-b", "202 - Test"));
        vault.Save("etsy-a", "etsy", "303", new EtsyCredentials("key-c", "secret-c", "token-c", "303"));
        vault.Save("etsy-b", "etsy", "404", new EtsyCredentials("key-d", "secret-d", "token-d", "404"));

        Assert.AreEqual("key-a", vault.Load<TrendyolSettings>("trendyol-a", "trendyol", "101")!.ApiKey);
        Assert.AreEqual("key-b", vault.Load<TrendyolSettings>("trendyol-b", "trendyol", "202")!.ApiKey);
        Assert.AreEqual("token-c", vault.Load<EtsyCredentials>("etsy-a", "etsy", "303")!.Token);
        Assert.AreEqual("token-d", vault.Load<EtsyCredentials>("etsy-b", "etsy", "404")!.Token);
    });

    [TestMethod]
    public void EnvelopeRejectsWrongChannelShopAndPayloadIdentityWithoutLeakingSecrets() => WithRoot(root =>
    {
        var vault = new MarketplaceCredentialVault(root);
        const string secret = "never-print-this-secret";
        vault.Save("etsy-a", "etsy", "303", new EtsyCredentials("key", secret, "token", "303"));

        foreach (var action in new Action[]
        {
            () => vault.Load<EtsyCredentials>("etsy-a", "trendyol", "303"),
            () => vault.Load<EtsyCredentials>("etsy-a", "etsy", "999"),
            () => vault.Load<TrendyolSettings>("etsy-a", "etsy", "303")
        })
        {
            var error = Assert.ThrowsException<InvalidOperationException>(action);
            Assert.IsFalse(error.ToString().Contains(secret, StringComparison.Ordinal));
        }

        var identityError = Assert.ThrowsException<ArgumentException>(() =>
            vault.Save("etsy-b", "etsy", "404", new EtsyCredentials("key", secret, "token", "405")));
        Assert.IsFalse(identityError.ToString().Contains(secret, StringComparison.Ordinal));
    });

    [TestMethod]
    public void ConnectionIdIsHashedAndTraversalCannotEscapeVaultDirectory() => WithRoot(root =>
    {
        var vault = new MarketplaceCredentialVault(root);
        const string connectionId = "..\\..\\outside\\account";
        vault.Save(connectionId, "etsy", "303", new EtsyCredentials("key", "secret", "token", "303"));

        var expectedName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(connectionId))).ToLowerInvariant() + ".bin";
        var vaultDirectory = Path.Combine(root, "marketplace-credentials");
        CollectionAssert.AreEqual(new[] { expectedName }, Directory.GetFiles(vaultDirectory).Select(Path.GetFileName).ToArray());
        Assert.IsFalse(File.Exists(Path.Combine(root, "outside", "account.bin")));
        Assert.AreEqual("303", vault.Load<EtsyCredentials>(connectionId, "etsy", "303")!.ShopId);
    });

    [TestMethod]
    public void OversizedPayloadAndEncryptedBlobFailClosedWithBoundedDiagnostics() => WithRoot(root =>
    {
        var vault = new MarketplaceCredentialVault(root);
        var oversized = new EtsyCredentials(new string('k', 40_000), "secret", "token", "303");
        var saveError = Assert.ThrowsException<ArgumentException>(() => vault.Save("etsy-a", "etsy", "303", oversized));
        Assert.IsTrue(saveError.Message.Length < 300);

        vault.Save("etsy-b", "etsy", "404", new EtsyCredentials("key", "secret", "token", "404"));
        var path = Directory.GetFiles(Path.Combine(root, "marketplace-credentials")).Single();
        File.WriteAllBytes(path, new byte[70 * 1024]);
        var loadError = Assert.ThrowsException<InvalidOperationException>(() => vault.Load<EtsyCredentials>("etsy-b", "etsy", "404"));
        Assert.IsTrue(loadError.Message.Length < 300);
    });

    [TestMethod]
    public void KnownCredentialPayloadsMustPassChannelValidationBeforeEncryption() => WithRoot(root =>
    {
        var vault = new MarketplaceCredentialVault(root);

        Assert.ThrowsException<ArgumentException>(() =>
            vault.Save("etsy-invalid", "etsy", "303", new EtsyCredentials("", "", "", "303")));
        Assert.ThrowsException<ArgumentException>(() =>
            vault.Save("trendyol-invalid", "trendyol", "101", new TrendyolSettings("101", "", "", "")));
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "marketplace-credentials")));
    });

    [TestMethod]
    public void RemovingOneConnectionNeverRemovesAnother() => WithRoot(root =>
    {
        var vault = new MarketplaceCredentialVault(root);
        vault.Save("etsy-a", "etsy", "303", new EtsyCredentials("key-a", "secret-a", "token-a", "303"));
        vault.Save("etsy-b", "etsy", "404", new EtsyCredentials("key-b", "secret-b", "token-b", "404"));

        Assert.IsTrue(vault.Remove("etsy-a"));
        Assert.IsNull(vault.Load<EtsyCredentials>("etsy-a", "etsy", "303"));
        Assert.AreEqual("key-b", vault.Load<EtsyCredentials>("etsy-b", "etsy", "404")!.Key);
    });
}
