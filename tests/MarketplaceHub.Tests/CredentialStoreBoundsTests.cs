using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using TrMarketplaceHubDesktop;

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

    [TestMethod]
    public void AggregateOversizedCredentialIsRejectedBeforeReplacingReadableFile() => WithIsolatedStore((directory, storePath) =>
    {
        CredentialStore.Save(new EtsyCredentials("original-key", "original-secret", "original-token", "123"), directory);
        var originalBytes = File.ReadAllBytes(storePath);
        var largeScopes = Enumerable.Range(0, 128).Select(_ => new string('s', 256)).ToArray();
        var oversized = new EtsyCredentials(
            new string('k', 4096), new string('x', 4096), new string('t', 4096), "123",
            new string('r', 4096), null, new string('u', 4096), largeScopes);

        Assert.ThrowsException<ArgumentException>(() => CredentialStore.Save(oversized, directory));
        CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(storePath));
        Assert.AreEqual("original-key", CredentialStore.Load(directory)!.Key);
    });

    [TestMethod]
    public void BoundedReaderRejectsGrowthAfterObservedLengthAndReadsOnlyOneExtraByte()
    {
        using var stream = new LengthClaimStream(new byte[100], 4);
        Assert.ThrowsException<InvalidDataException>(() => ReadBounded(stream, 64));
        Assert.AreEqual(5, stream.BytesReturned);
    }

    [TestMethod]
    public void BoundedReaderRejectsOversizedLengthBeforeReading()
    {
        using var stream = new LengthClaimStream(new byte[100], 65);
        Assert.ThrowsException<InvalidDataException>(() => ReadBounded(stream, 64));
        Assert.AreEqual(0, stream.BytesReturned);
    }

    static byte[] ReadBounded(Stream stream, int maximum)
    {
        var type = typeof(CredentialStore).Assembly.GetType("TrMarketplaceHubDesktop.BoundedCredentialFile");
        Assert.IsNotNull(type, "Credential stores must share the bounded same-handle reader.");
        var method = type.GetMethod("ReadBounded", BindingFlags.Static | BindingFlags.NonPublic, null, new[] { typeof(Stream), typeof(int) }, null);
        Assert.IsNotNull(method);
        try { return (byte[])method.Invoke(null, new object[] { stream, maximum }); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    sealed class LengthClaimStream(byte[] data, long claimedLength) : Stream
    {
        int position;
        public int BytesReturned { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => claimedLength;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(count, data.Length - position);
            if (read <= 0) return 0;
            Array.Copy(data, position, buffer, offset, read);
            position += read;
            BytesReturned += read;
            return read;
        }
        public override int Read(Span<byte> buffer)
        {
            var read = Math.Min(buffer.Length, data.Length - position);
            if (read <= 0) return 0;
            data.AsSpan(position, read).CopyTo(buffer);
            position += read;
            BytesReturned += read;
            return read;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
