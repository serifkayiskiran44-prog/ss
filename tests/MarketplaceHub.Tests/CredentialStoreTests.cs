using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// CredentialStore previously had no directory parameter at all -- Save/Load always resolved to the one real
// %LocalAppData%\MonoBridgeDesktop\credentials.bin file, unlike every other store in this codebase
// (CatalogStore(dataDirectory), AutomationStore(dataDirectory), TemplateStore.Load(directory), ...). That made
// it untestable without risking this machine's real stored Etsy OAuth credentials (see #276's re-verification
// notes). These tests exercise the now-injectable path; none of them touch the real credentials file.
[TestClass]
public sealed class CredentialStoreTests
{
    static string NewRoot([System.Runtime.CompilerServices.CallerMemberName] string caller = "") =>
        Path.Combine(Path.GetTempPath(), "credential-store-" + caller + "-" + Guid.NewGuid().ToString("N"));

    [TestMethod]
    public void LoadReturnsNullWhenNoFileExistsYetInTheGivenDirectory()
    {
        var root = NewRoot();
        try { Assert.IsNull(CredentialStore.Load(root)); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void SaveThenLoadRoundTripsAllFieldsFromTheGivenDirectory()
    {
        var root = NewRoot();
        try
        {
            var original = new EtsyCredentials("key-1", "secret-1", "token-1", "999", "refresh-1", DateTimeOffset.UtcNow.AddHours(1), "https://example.test/callback");
            CredentialStore.Save(original, root);

            var loaded = CredentialStore.Load(root);

            Assert.IsNotNull(loaded);
            Assert.AreEqual(original.Key, loaded!.Key);
            Assert.AreEqual(original.Secret, loaded.Secret);
            Assert.AreEqual(original.Token, loaded.Token);
            Assert.AreEqual(original.ShopId, loaded.ShopId);
            Assert.AreEqual(original.RefreshToken, loaded.RefreshToken);
            Assert.AreEqual(original.RedirectUri, loaded.RedirectUri);
            Assert.AreEqual(original.ExpiresAt!.Value.ToUnixTimeSeconds(), loaded.ExpiresAt!.Value.ToUnixTimeSeconds());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void TwoDifferentDirectoriesAreCompletelyIsolatedFromEachOther()
    {
        var rootA = NewRoot("A"); var rootB = NewRoot("B");
        try
        {
            CredentialStore.Save(new EtsyCredentials("key-a", "s", "t", "1"), rootA);

            Assert.IsNull(CredentialStore.Load(rootB), "A credential saved under one directory must not be visible from a different directory.");
            Assert.AreEqual("key-a", CredentialStore.Load(rootA)!.Key);
        }
        finally
        {
            if (Directory.Exists(rootA)) Directory.Delete(rootA, true);
            if (Directory.Exists(rootB)) Directory.Delete(rootB, true);
        }
    }

    [TestMethod]
    public void SaveOverwritesAPreviouslySavedCredentialInTheSameDirectory()
    {
        var root = NewRoot();
        try
        {
            CredentialStore.Save(new EtsyCredentials("old", "s", "t", "1"), root);
            CredentialStore.Save(new EtsyCredentials("new", "s", "t", "1"), root);

            Assert.AreEqual("new", CredentialStore.Load(root)!.Key);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
