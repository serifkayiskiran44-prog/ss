using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Hepsiburada;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class HepsiburadaCredentialTests
{
    static void WithRoot(Action<string> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "hepsiburada-vault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void HepsiburadaCredentialsAreAccountScopedAndNeverCrossShop() => WithRoot(root =>
    {
        var vault = new MarketplaceCredentialVault(root);
        var credentials = new HepsiburadaCredentials("merchant-a", "secret", HepsiburadaEnvironment.Production, "MonoBridge/1");

        vault.Save("hb-a", "hepsiburada", "merchant-a", credentials);

        Assert.AreEqual(credentials, vault.Load<HepsiburadaCredentials>("hb-a", "hepsiburada", "merchant-a"));
        Assert.ThrowsException<InvalidOperationException>(() =>
            vault.Load<HepsiburadaCredentials>("hb-a", "hepsiburada", "merchant-b"));
    });

    [TestMethod]
    public void HepsiburadaCredentialsRejectWrongPayloadIdentity() => WithRoot(root =>
    {
        var vault = new MarketplaceCredentialVault(root);
        var credentials = new HepsiburadaCredentials("merchant-a", "secret", HepsiburadaEnvironment.Sit, "MonoBridge/1");

        Assert.ThrowsException<ArgumentException>(() =>
            vault.Save("hb-a", "hepsiburada", "merchant-b", credentials));
    });

    [TestMethod]
    public void HepsiburadaValidationRejectsControlCharactersAndUnboundedSecrets()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            HepsiburadaConnection.Validate(new HepsiburadaCredentials("merchant", "bad\r\nsecret", HepsiburadaEnvironment.Production, "MonoBridge/1")));
        Assert.ThrowsException<ArgumentException>(() =>
            HepsiburadaConnection.Validate(new HepsiburadaCredentials("merchant", new string('s', 513), HepsiburadaEnvironment.Production, "MonoBridge/1")));
    }

    [TestMethod]
    public void HepsiburadaValidationRequiresTrimmedBoundedFields()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            HepsiburadaConnection.Validate(new HepsiburadaCredentials(" merchant", "secret", HepsiburadaEnvironment.Production, "MonoBridge/1")));
        Assert.ThrowsException<ArgumentException>(() =>
            HepsiburadaConnection.Validate(new HepsiburadaCredentials("merchant", "secret", (HepsiburadaEnvironment)99, "MonoBridge/1")));
        HepsiburadaConnection.Validate(new HepsiburadaCredentials("merchant", "secret", HepsiburadaEnvironment.Production, "MonoBridge/1"));
    }
}
