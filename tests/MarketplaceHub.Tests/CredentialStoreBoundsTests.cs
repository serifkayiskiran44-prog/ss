using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;

namespace MarketplaceHub.Tests;

/// Coverage for #2644: CredentialStore.Load must never read an unbounded
/// encrypted file or decrypt-then-deserialize an unbounded plaintext blob.
///
/// Every test uses a fresh temporary directory while still round-tripping
/// through the real DPAPI CurrentUser boundary. The user's real LocalAppData
/// credential file is never inspected, backed up, overwritten, or deleted.
[TestClass]
public sealed class CredentialStoreBoundsTests
{
    static void WithIsolatedStore(Action<string, string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MonoBridgeCredentialTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { test(directory, Path.Combine(directory, "credentials.bin")); }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public void MissingFileReturnsNull() => WithIsolatedStore((directory, _) =>
    {
        Assert.IsNull(TrMarketplaceHubDesktop.CredentialStore.Load(directory));
    });

    [TestMethod]
    public void ValidNormalSizeCredentialRoundTrips() => WithIsolatedStore((directory, _) =>
    {
        var credentials = new TrMarketplaceHubDesktop.EtsyCredentials("k", "s", "t", "123");
        TrMarketplaceHubDesktop.CredentialStore.Save(credentials, directory);
        var loaded = TrMarketplaceHubDesktop.CredentialStore.Load(directory);
        Assert.IsNotNull(loaded);
        Assert.AreEqual("k", loaded!.Key);
        Assert.AreEqual("123", loaded.ShopId);
    });

    [TestMethod]
    public void OversizedEncryptedFileIsRejectedWithoutFullyReadingIt() => WithIsolatedStore((directory, storePath) =>
    {
        // Far larger than any real DPAPI-wrapped credential blob could ever be -
        // this must be rejected before File.ReadAllBytes ever runs.
        File.WriteAllBytes(storePath, new byte[10 * 1024 * 1024]);
        var error = Assert.ThrowsException<InvalidOperationException>(() => TrMarketplaceHubDesktop.CredentialStore.Load(directory));
        StringAssert.Contains(error.Message, "okunamadı");
    });

    [TestMethod]
    public void CorruptTruncatedBlobFailsClosedWithTheRecoveryMessage() => WithIsolatedStore((directory, storePath) =>
    {
        File.WriteAllBytes(storePath, new byte[] { 1, 2, 3, 4, 5 });
        var error = Assert.ThrowsException<InvalidOperationException>(() => TrMarketplaceHubDesktop.CredentialStore.Load(directory));
        StringAssert.Contains(error.Message, "okunamadı");
    });

    [TestMethod]
    public void ZeroByteFileFailsClosedRatherThanCrashingOrReturningNull() => WithIsolatedStore((directory, storePath) =>
    {
        File.WriteAllBytes(storePath, Array.Empty<byte>());
        Assert.ThrowsException<InvalidOperationException>(() => TrMarketplaceHubDesktop.CredentialStore.Load(directory));
    });

    [TestMethod]
    public void RestartPreservesTheFileByteForByteAfterANormalSave() => WithIsolatedStore((directory, storePath) =>
    {
        var credentials = new TrMarketplaceHubDesktop.EtsyCredentials("k2", "s2", "t2", "456");
        TrMarketplaceHubDesktop.CredentialStore.Save(credentials, directory);
        var bytesAfterSave = File.ReadAllBytes(storePath);
        _ = TrMarketplaceHubDesktop.CredentialStore.Load(directory);
        var bytesAfterLoad = File.ReadAllBytes(storePath);
        CollectionAssert.AreEqual(bytesAfterSave, bytesAfterLoad, "A normal Load() must never rewrite/truncate the file.");
    });
}
