using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class SiteXmlPublisherTests
{
    // Characters that change under URL encoding, so a leak in either form is caught.
    const string Token = "tok-SECRET value/+=&1";
    string directory = "";
    SiteHandler site = null!;

    [TestInitialize]
    public void Setup()
    {
        directory = Path.Combine(Path.GetTempPath(), "site-xml-publish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        site = new();
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    [TestMethod]
    public void PreviewDescribesTheFeedAndSendsNothing()
    {
        using var publisher = Publisher();
        var path = Feed(3);

        var preview = publisher.Preview("trendyol", "feed", path);

        Assert.AreEqual(3, preview.ModelCount);
        Assert.AreEqual("https://www.petshoptedarik.com.tr/marketxml/files/trendyol/feed.xml", preview.TargetUrl);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(), preview.Sha256);
        Assert.AreEqual(0, site.Requests.Count);
    }

    [TestMethod]
    public async Task PublishIsRefusedWithoutExplicitApprovalAndSendsNothing()
    {
        using var publisher = Publisher();
        var preview = publisher.Preview("trendyol", "feed", Feed());

        await Assert.ThrowsExceptionAsync<SiteXmlPublishException>(() => publisher.PublishAsync(preview, false));

        Assert.AreEqual(0, site.Requests.Count);
        Assert.IsNull(publisher.Receipt(preview.Id));
    }

    [TestMethod]
    public async Task ApprovedPublishUploadsTheExactFileVerifiesItAndWritesAReceipt()
    {
        using var publisher = Publisher();
        var path = Feed(2);
        var preview = publisher.Preview("trendyol", "feed", path);

        var receipt = await publisher.PublishAsync(preview, true);

        Assert.AreEqual(2, site.Requests.Count);
        var upload = site.Requests[0];
        Assert.AreEqual(HttpMethod.Post, upload.Method);
        Assert.AreEqual("www.petshoptedarik.com.tr", upload.Uri.Host);
        Assert.AreEqual("/marketxml/upload.php", upload.Uri.AbsolutePath);
        var query = HttpUtility.ParseQueryString(upload.Uri.Query);
        Assert.AreEqual(Token, query["token"]);
        Assert.AreEqual("trendyol", query["market"]);
        Assert.AreEqual("feed", query["name"]);
        Assert.AreEqual("application/xml", upload.ContentType);
        CollectionAssert.AreEqual(File.ReadAllBytes(path), upload.Body);
        var verify = site.Requests[1];
        Assert.AreEqual(HttpMethod.Get, verify.Method);
        Assert.AreEqual(preview.TargetUrl + "?_verify=" + preview.Sha256[..16], verify.Uri.AbsoluteUri);
        Assert.AreEqual(preview.TargetUrl, receipt.PublicUrl);
        Assert.AreEqual(preview.Sha256, receipt.Sha256);
        Assert.AreEqual(2, receipt.ModelCount);
        Assert.IsNotNull(publisher.Receipt(preview.Id));
    }

    [TestMethod]
    public async Task APublishedPreviewCannotBePublishedAgain()
    {
        using var publisher = Publisher();
        var preview = publisher.Preview("trendyol", "feed", Feed());
        await publisher.PublishAsync(preview, true);

        var error = await Assert.ThrowsExceptionAsync<SiteXmlPublishException>(() => publisher.PublishAsync(preview, true));

        StringAssert.Contains(error.Message, "daha önce");
        Assert.AreEqual(2, site.Requests.Count);
    }

    [TestMethod]
    public async Task ATamperedPreviewIsRefusedBeforeAnyRequest()
    {
        using var publisher = Publisher();
        var preview = publisher.Preview("trendyol", "feed", Feed());

        await Assert.ThrowsExceptionAsync<SiteXmlPublishException>(() => publisher.PublishAsync(preview with { Sha256 = new string('0', 64) }, true));
        await Assert.ThrowsExceptionAsync<SiteXmlPublishException>(() => publisher.PublishAsync(preview with { Id = "unknown" }, true));

        Assert.AreEqual(0, site.Requests.Count);
    }

    [TestMethod]
    public async Task AFileThatChangedAfterThePreviewIsRefusedBeforeAnyRequest()
    {
        using var publisher = Publisher();
        var path = Feed(2);
        var preview = publisher.Preview("trendyol", "feed", path);
        File.WriteAllText(path, "<Models><Model><Barcode>OTHER</Barcode></Model></Models>");

        var error = await Assert.ThrowsExceptionAsync<SiteXmlPublishException>(() => publisher.PublishAsync(preview, true));

        StringAssert.Contains(error.Message, "değişti");
        Assert.AreEqual(0, site.Requests.Count);
    }

    [TestMethod]
    public async Task MissingTokenRefusesWithoutAnyRequest()
    {
        var settings = new Settings { UploadToken = null };
        using var publisher = Publisher(settings);
        var preview = publisher.Preview("trendyol", "feed", Feed());

        var error = await Assert.ThrowsExceptionAsync<SiteXmlPublishException>(() => publisher.PublishAsync(preview, true));

        Assert.IsFalse(publisher.IsConfigured);
        StringAssert.Contains(error.Message, "anahtar");
        Assert.AreEqual(0, site.Requests.Count);
    }

    [TestMethod]
    public void OnlyHttpsOnThePinnedHostWithSafeNamesIsAccepted()
    {
        var path = Feed();
        foreach (var settings in new[]
        {
            new Settings { UploadUrl = "http://www.petshoptedarik.com.tr/marketxml/upload.php" },
            new Settings { UploadUrl = "https://evil.example/marketxml/upload.php" },
            new Settings { PublicBaseUrl = "https://evil.example/marketxml/files" },
            new Settings { PublicBaseUrl = "ftp://www.petshoptedarik.com.tr/marketxml/files" },
            new Settings { UploadUrl = "not a url" }
        })
        {
            using var publisher = Publisher(settings);
            Assert.ThrowsException<SiteXmlPublishException>(() => publisher.Preview("trendyol", "feed", path));
        }
        using var normal = Publisher();
        foreach (var name in new[] { "../x", "UPPER", "a b", "", "x/y", new string('a', 65), "feed\n" })
        {
            Assert.ThrowsException<SiteXmlPublishException>(() => normal.Preview(name, "feed", path));
            Assert.ThrowsException<SiteXmlPublishException>(() => normal.Preview("trendyol", name, path));
        }
        Assert.AreEqual(0, site.Requests.Count);
    }

    [TestMethod]
    public void PortAndUserInfoInThePinnedUrlsAreRefused()
    {
        var path = Feed();
        foreach (var settings in new[]
        {
            new Settings { UploadUrl = "https://www.petshoptedarik.com.tr:8443/marketxml/upload.php" },
            new Settings { UploadUrl = "https://user:pw@www.petshoptedarik.com.tr/marketxml/upload.php" },
            new Settings { PublicBaseUrl = "https://www.petshoptedarik.com.tr:8443/marketxml/files" }
        })
        {
            using var publisher = Publisher(settings);
            Assert.ThrowsException<SiteXmlPublishException>(() => publisher.Preview("trendyol", "feed", path));
        }
    }

    [TestMethod]
    public void OversizedFeedIsRefusedWithoutLoadingItWhole()
    {
        using var publisher = Publisher();
        var path = Feed();
        using (var stream = new FileStream(path, FileMode.Append)) stream.SetLength(SiteXmlPublisher.MaxBytes + 1);
        Assert.ThrowsException<SiteXmlPublishException>(() => publisher.Preview("trendyol", "feed", path));
    }

    [TestMethod]
    public void EmptyWrongRootMalformedAndDtdFeedsAreRefusedAtPreview()
    {
        using var publisher = Publisher();
        var empty = Feed(0);
        var wrongRoot = Feed(2, "Products");
        var malformed = Path.Combine(directory, "bad.xml"); File.WriteAllText(malformed, "<Models><Model>");
        var dtd = Path.Combine(directory, "dtd.xml"); File.WriteAllText(dtd, "<!DOCTYPE Models [<!ENTITY x \"y\">]><Models><Model>&x;</Model></Models>");

        foreach (var path in new[] { empty, wrongRoot, malformed, dtd, Path.Combine(directory, "missing.xml") })
            Assert.ThrowsException<SiteXmlPublishException>(() => publisher.Preview("trendyol", "feed", path), path);

        Assert.AreEqual(0, site.Requests.Count);
    }

    [TestMethod]
    public async Task ArrayOfModelRootIsAccepted()
    {
        using var publisher = Publisher();
        var preview = publisher.Preview("trendyol", "feed", Feed(4, "ArrayOfModel"));
        await publisher.PublishAsync(preview, true);
        Assert.AreEqual(4, preview.ModelCount);
    }

    [TestMethod]
    public async Task AnUnexpectedReturnedAddressFailsAndLeavesNoReceipt()
    {
        site.UploadBody = "https://evil.example/marketxml/files/trendyol/feed.xml";
        using var publisher = Publisher();
        var preview = publisher.Preview("trendyol", "feed", Feed());

        var error = await Assert.ThrowsExceptionAsync<SiteXmlPublishException>(() => publisher.PublishAsync(preview, true));

        StringAssert.Contains(error.Message, "beklenmeyen");
        Assert.AreEqual(1, site.Requests.Count, "No read-back from an address the site was not expected to return.");
        Assert.IsNull(publisher.Receipt(preview.Id));
    }

    [TestMethod]
    public async Task ARelativeAddressThatResolvesToTheExpectedFileIsAccepted()
    {
        site.UploadBody = "/marketxml/files/trendyol/feed.xml";
        using var publisher = Publisher();
        var preview = publisher.Preview("trendyol", "feed", Feed());

        var receipt = await publisher.PublishAsync(preview, true);

        Assert.AreEqual(preview.TargetUrl, receipt.PublicUrl);
    }

    [TestMethod]
    public async Task ReadBackMismatchIsAVerificationFailureWithoutAReceipt()
    {
        site.DownloadOverride = Encoding.UTF8.GetBytes("<Models><Model><Barcode>X</Barcode></Model></Models>");
        using var publisher = Publisher();
        var preview = publisher.Preview("trendyol", "feed", Feed());

        await Assert.ThrowsExceptionAsync<SiteXmlPublishVerificationException>(() => publisher.PublishAsync(preview, true));

        Assert.IsNull(publisher.Receipt(preview.Id));
    }

    [TestMethod]
    public async Task ServerRejectionIsAFailureAndTheSamePreviewMayBeRetried()
    {
        site.UploadStatus = HttpStatusCode.Forbidden;
        using var publisher = Publisher();
        var preview = publisher.Preview("trendyol", "feed", Feed());

        var error = await Assert.ThrowsExceptionAsync<SiteXmlPublishException>(() => publisher.PublishAsync(preview, true));
        StringAssert.Contains(error.Message, "403");
        Assert.IsNull(publisher.Receipt(preview.Id));

        site.UploadStatus = HttpStatusCode.OK;
        var receipt = await publisher.PublishAsync(preview, true);
        Assert.AreEqual(preview.TargetUrl, receipt.PublicUrl);
    }

    [TestMethod]
    public async Task TheTokenNeverAppearsInErrorsOrTheDatabase()
    {
        site.Override = request => throw new HttpRequestException(
            "failed for https://www.petshoptedarik.com.tr/marketxml/upload.php?token=" + Uri.EscapeDataString(Token) + "&market=trendyol and raw " + Token);
        using var publisher = Publisher();
        var preview = publisher.Preview("trendyol", "feed", Feed());

        var error = await Assert.ThrowsExceptionAsync<SiteXmlPublishException>(() => publisher.PublishAsync(preview, true));
        AssertNoToken(error.Message);

        site.Override = null;
        await publisher.PublishAsync(preview, true);
        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(directory, "site-xml-publish.db*"))
            AssertNoToken(Encoding.UTF8.GetString(File.ReadAllBytes(file)));
    }

    [TestMethod]
    public void RegistrySettingsReadTheSharedValuesTrimmedAndFallBackToTheDefaults()
    {
        var keyPath = @"Software\MonoBridgeTests\SiteXml\" + Guid.NewGuid().ToString("N");
        try
        {
            Assert.IsNull(new RegistrySiteUploadSettings(keyPath).UploadToken);
            using (var key = Registry.CurrentUser.CreateSubKey(keyPath)) key.SetValue("upload_token", "  abc  ");

            var settings = new RegistrySiteUploadSettings(keyPath);

            Assert.AreEqual("abc", settings.UploadToken);
            Assert.AreEqual(RegistrySiteUploadSettings.DefaultUploadUrl, settings.UploadUrl);
            Assert.AreEqual(RegistrySiteUploadSettings.DefaultPublicBaseUrl, settings.PublicBaseUrl);
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true)!) key.SetValue("upload_url", "https://www.petshoptedarik.com.tr/other/upload.php");
            Assert.AreEqual("https://www.petshoptedarik.com.tr/other/upload.php", settings.UploadUrl);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(@"Software\MonoBridgeTests", throwOnMissingSubKey: false); }
    }

    static void AssertNoToken(string text)
    {
        foreach (var form in new[] { Token, Uri.EscapeDataString(Token), WebUtility.UrlEncode(Token), "tok-SECRET" })
            Assert.IsFalse(text.Contains(form, StringComparison.Ordinal), "The upload token leaked: " + form);
    }

    string Feed(int models = 2, string root = "Models")
    {
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".xml");
        var items = string.Concat(Enumerable.Range(1, models).Select(i => $"<Model><Barcode>B{i}</Barcode><Name>Ürün {i}</Name></Model>"));
        File.WriteAllText(path, $"<?xml version=\"1.0\" encoding=\"utf-8\"?><{root}>{items}</{root}>", new UTF8Encoding(false));
        return path;
    }

    SiteXmlPublisher Publisher(Settings? settings = null) => new(directory, settings ?? new Settings(), site);

    sealed class Settings : ISiteUploadSettings
    {
        public string? UploadToken { get; set; } = Token;
        public string UploadUrl { get; set; } = RegistrySiteUploadSettings.DefaultUploadUrl;
        public string PublicBaseUrl { get; set; } = RegistrySiteUploadSettings.DefaultPublicBaseUrl;
    }

    sealed class SiteHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, byte[] Body, string? ContentType)> Requests { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage>? Override { get; set; }
        public string UploadBody { get; set; } = "{\"url\":\"https://www.petshoptedarik.com.tr/marketxml/files/trendyol/feed.xml\"}";
        public HttpStatusCode UploadStatus { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode DownloadStatus { get; set; } = HttpStatusCode.OK;
        public byte[]? DownloadOverride { get; set; }
        byte[] stored = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            var body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellation);
            Requests.Add((request.Method, request.RequestUri!, body, request.Content?.Headers.ContentType?.MediaType));
            if (Override is not null) return Override(request);
            if (request.Method == HttpMethod.Post)
            {
                stored = body;
                return new HttpResponseMessage(UploadStatus) { Content = new StringContent(UploadBody) };
            }
            return new HttpResponseMessage(DownloadStatus) { Content = new ByteArrayContent(DownloadOverride ?? stored) };
        }
    }
}
