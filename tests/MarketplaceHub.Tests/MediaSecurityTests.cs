using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class MediaSecurityTests
{
    [TestMethod]
    public void ApprovedFileWithinRootIsAccepted()
    {
        var root = Path.Combine(Path.GetTempPath(), "media-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "photo.jpg");
        File.WriteAllBytes(file, [1, 2, 3]);
        try
        {
            var ok = MediaFileAccessPolicy.TryResolveApprovedFile(file, [root], out var resolved, out var error);
            Assert.IsTrue(ok, error);
            Assert.AreEqual(MediaFileAccessPolicy.Canonicalize(file), resolved);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void FileOutsideApprovedRootIsRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "media-root-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "media-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        var file = Path.Combine(outside, "secret.jpg");
        File.WriteAllBytes(file, [1, 2, 3]);
        try
        {
            var ok = MediaFileAccessPolicy.TryResolveApprovedFile(file, [root], out _, out var error);
            Assert.IsFalse(ok);
            Assert.IsTrue(error.Length > 0);
        }
        finally { Directory.Delete(root, true); Directory.Delete(outside, true); }
    }

    [TestMethod]
    public void PathTraversalOutOfApprovedRootIsRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "media-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var traversal = Path.Combine(root, "..", "..", "windows", "win.ini");
            var ok = MediaFileAccessPolicy.TryResolveApprovedFile(traversal, [root], out _, out var error);
            Assert.IsFalse(ok);
            Assert.IsTrue(error.Length > 0);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void UncPathIsRejectedByDefault()
    {
        var ok = MediaFileAccessPolicy.TryResolveApprovedFile(@"\\server\share\image.jpg", [@"C:\anywhere"], out _, out var error);
        Assert.IsFalse(ok);
        StringAssert.Contains(error, "UNC");
    }

    [TestMethod]
    public async Task LocalMediaValidationRejectsFileOutsideApprovedRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "media-approved-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "media-blocked-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        var file = Path.Combine(outside, "img.png");
        File.WriteAllBytes(file, [1, 2, 3]);
        try
        {
            var service = new MediaValidationService(approvedLocalRoots: [root]);
            var media = new ProductMediaRecord { Url = new Uri(file).AbsoluteUri };
            var result = await service.ValidateAsync(media);
            Assert.AreEqual(MediaStatus.InvalidUrl, result.Status);
        }
        finally { Directory.Delete(root, true); Directory.Delete(outside, true); }
    }

    [TestMethod]
    public void PrivateAndLoopbackAddressesAreNotPublic()
    {
        Assert.IsFalse(SafeRemoteHttp.IsPublicAddress(IPAddress.Loopback));
        Assert.IsFalse(SafeRemoteHttp.IsPublicAddress(IPAddress.Parse("127.0.0.1")));
        Assert.IsFalse(SafeRemoteHttp.IsPublicAddress(IPAddress.Parse("10.0.0.5")));
        Assert.IsFalse(SafeRemoteHttp.IsPublicAddress(IPAddress.Parse("172.16.5.1")));
        Assert.IsFalse(SafeRemoteHttp.IsPublicAddress(IPAddress.Parse("192.168.1.1")));
        Assert.IsFalse(SafeRemoteHttp.IsPublicAddress(IPAddress.Parse("169.254.169.254")));
        Assert.IsFalse(SafeRemoteHttp.IsPublicAddress(IPAddress.IPv6Loopback));
        Assert.IsFalse(SafeRemoteHttp.IsPublicAddress(IPAddress.Parse("fc00::1")));
        Assert.IsTrue(SafeRemoteHttp.IsPublicAddress(IPAddress.Parse("8.8.8.8")));
    }

    [TestMethod]
    public async Task SafeHttpClientRefusesToConnectToLoopbackEvenWithActiveListener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptTcpClientAsync();
        try
        {
            using var client = SafeRemoteHttp.CreateClient(TimeSpan.FromSeconds(5));
            await Assert.ThrowsExceptionAsync<HttpRequestException>(async () =>
                await client.GetAsync($"http://127.0.0.1:{port}/"));
            Assert.IsFalse(acceptTask.IsCompleted, "SSRF guard must reject before a socket connection is attempted.");
        }
        finally { listener.Stop(); }
    }
}
