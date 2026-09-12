using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #693 (SOURCE HEALTH: XML content-type ve payload doğrulama): a well-formed-enough HTML error/login page can
// parse "successfully" as generic XML syntax (matching tags), so a syntax-only check is not enough -- must
// reject on content-type and on the actual payload signature before ever handing it to the XML parser.
[TestClass]
public sealed class XmlSourcePayloadValidationTests
{
    static HttpClient Client(HttpStatusCode status, string content, string mediaType, byte[]? rawBytes = null)
        => new(new FixedHandler(status, rawBytes ?? Encoding.UTF8.GetBytes(content), mediaType));

    [TestMethod]
    public async Task ValidXmlIsAcceptedNormally()
    {
        using var http = Client(HttpStatusCode.OK, "<Products><Product><Sku>A</Sku></Product></Products>", "text/xml");
        var text = await new XmlSourceReader(http).ReadAsync("https://example.test/feed.xml");
        StringAssert.Contains(text, "<Sku>A</Sku>");
    }

    [TestMethod]
    public async Task AnHtmlContentTypeIsRejectedBeforeParsingEvenIfTheBodyLooksLikeValidMarkup()
    {
        // Well-formed enough to parse as generic XML syntax (matching tags) -- content-type must catch it.
        using var http = Client(HttpStatusCode.OK, "<html><body><h1>404 Not Found</h1></body></html>", "text/html");
        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => new XmlSourceReader(http).ReadAsync("https://example.test/feed.xml"));
        StringAssert.Contains(ex.Message, "HTML");
    }

    [TestMethod]
    public async Task AnHtmlPayloadIsRejectedByItsSignatureEvenWhenTheServerLiesAboutContentType()
    {
        // Content-type says XML but the body is actually an HTML error page -- must not trust the header alone.
        using var http = Client(HttpStatusCode.OK, "<!DOCTYPE html>\n<html><body>Service unavailable</body></html>", "text/xml");
        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => new XmlSourceReader(http).ReadAsync("https://example.test/feed.xml"));
        StringAssert.Contains(ex.Message, "HTML");
    }

    [TestMethod]
    public async Task AnEmptyBodyIsRejectedWithAClearMessageInsteadOfAConfusingParseError()
    {
        using var http = Client(HttpStatusCode.OK, "", "text/xml");
        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => new XmlSourceReader(http).ReadAsync("https://example.test/feed.xml"));
        StringAssert.Contains(ex.Message, "boş");
    }

    [TestMethod]
    public async Task AnOversizedPayloadIsRejectedInsteadOfBeingBufferedInFull()
    {
        var oversized = new byte[XmlSourceReader.Limit + 1024];
        using var http = Client(HttpStatusCode.OK, "", "text/xml", rawBytes: oversized);
        var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => new XmlSourceReader(http).ReadAsync("https://example.test/feed.xml"));
        StringAssert.Contains(ex.Message, "25 MB");
    }

    [TestMethod]
    public async Task MismatchedEncodingIsRejectedInsteadOfSilentlyImportingGarbledText()
    {
        // XML prolog declares UTF-16 but the bytes are actually UTF-8 -- must fail closed, not garble-import.
        var mismatched = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-16\"?><Products/>");
        using var http = Client(HttpStatusCode.OK, "", "text/xml", rawBytes: mismatched);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => new XmlSourceReader(http).ReadAsync("https://example.test/feed.xml"));
    }

    sealed class FixedHandler(HttpStatusCode status, byte[] body, string mediaType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            return Task.FromResult(response);
        }
    }
}
