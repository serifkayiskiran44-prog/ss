using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;

namespace MarketplaceHub.Tests;

/// Coverage for #2644: CredentialStore.Load must never read an unbounded
/// encrypted file or decrypt-then-deserialize an unbounded plaintext blob.
///
/// CredentialStore has no directory-injection parameter (unlike every other
/// store in this codebase) - it always targets the real per-Windows-user
/// %LocalAppData%\MonoBridgeDesktop\credentials.bin, because it round-trips
/// through the real DPAPI CurrentUser boundary. These tests back up and
/// restore any pre-existing file around each case so a developer's real saved
/// Etsy credentials (if any) are never lost.
[TestClass]
public sealed class CredentialStoreBoundsTests
{
    static string StorePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop", "credentials.bin");

    static void WithIsolatedStore(Action test)
    {
        var path = StorePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[]? backup = File.Exists(path) ? File.ReadAllBytes(path) : null;
        try { test(); }
        finally
        {
            if (backup is not null) File.WriteAllBytes(path, backup);
            else if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public void MissingFileReturnsNull() => WithIsolatedStore(() =>
    {
        if (File.Exists(StorePath)) File.Delete(StorePath);
        Assert.IsNull(TrMarketplaceHubDesktop.CredentialStore.Load());
    });

    [TestMethod]
    public void ValidNormalSizeCredentialRoundTrips() => WithIsolatedStore(() =>
    {
        var credentials = new TrMarketplaceHubDesktop.EtsyCredentials("k", "s", "t", "123");
        TrMarketplaceHubDesktop.CredentialStore.Save(credentials);
        var loaded = TrMarketplaceHubDesktop.CredentialStore.Load();
        Assert.IsNotNull(loaded);
        Assert.AreEqual("k", loaded!.Key);
        Assert.AreEqual("123", loaded.ShopId);
    });

    [TestMethod]
    public void OversizedEncryptedFileIsRejectedWithoutFullyReadingIt() => WithIsolatedStore(() =>
    {
        // Far larger than any real DPAPI-wrapped credential blob could ever be -
        // this must be rejected before File.ReadAllBytes ever runs.
        File.WriteAllBytes(StorePath, new byte[10 * 1024 * 1024]);
        var error = Assert.ThrowsException<InvalidOperationException>(() => TrMarketplaceHubDesktop.CredentialStore.Load());
        StringAssert.Contains(error.Message, "okunamadı");
    });

    [TestMethod]
    public void CorruptTruncatedBlobFailsClosedWithTheRecoveryMessage() => WithIsolatedStore(() =>
    {
        File.WriteAllBytes(StorePath, new byte[] { 1, 2, 3, 4, 5 });
        var error = Assert.ThrowsException<InvalidOperationException>(() => TrMarketplaceHubDesktop.CredentialStore.Load());
        StringAssert.Contains(error.Message, "okunamadı");
    });

    [TestMethod]
    public void ZeroByteFileFailsClosedRatherThanCrashingOrReturningNull() => WithIsolatedStore(() =>
    {
        File.WriteAllBytes(StorePath, Array.Empty<byte>());
        Assert.ThrowsException<InvalidOperationException>(() => TrMarketplaceHubDesktop.CredentialStore.Load());
    });

    [TestMethod]
    public void RestartPreservesTheFileByteForByteAfterANormalSave() => WithIsolatedStore(() =>
    {
        var credentials = new TrMarketplaceHubDesktop.EtsyCredentials("k2", "s2", "t2", "456");
        TrMarketplaceHubDesktop.CredentialStore.Save(credentials);
        var bytesAfterSave = File.ReadAllBytes(StorePath);
        _ = TrMarketplaceHubDesktop.CredentialStore.Load();
        var bytesAfterLoad = File.ReadAllBytes(StorePath);
        CollectionAssert.AreEqual(bytesAfterSave, bytesAfterLoad, "A normal Load() must never rewrite/truncate the file.");
    });
}
