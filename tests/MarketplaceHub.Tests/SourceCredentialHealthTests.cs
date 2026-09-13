using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #892 (SOURCE GOVERNANCE: source credential health). The state follows two facts — a credential's presence and
// the last real answer — and never its value; the download entry points refuse a 401/403 with a typed reason that
// says whether a credential was sent; the probe sends the saved credential so a working one reads as healthy; a
// credential saved for one source is never sent for another.
[TestClass]
public sealed class SourceCredentialHealthTests
{
    [TestMethod]
    public void TheStateFollowsPresenceAndTheLastAnswerNeverTheValue()
    {
        Assert.AreEqual(CredentialPresence.None, SourceCredentialHealth.PresenceOf(null));
        Assert.AreEqual(CredentialPresence.None, SourceCredentialHealth.PresenceOf(new XmlAuth()));
        Assert.AreEqual(CredentialPresence.Saved, SourceCredentialHealth.PresenceOf(new XmlAuth("operator", "p")));
        Assert.AreEqual(CredentialPresence.Saved, SourceCredentialHealth.PresenceOf(new XmlAuth("", "only-secret")));

        Assert.AreEqual(SourceCredentialHealth.Missing, SourceCredentialHealth.Evaluate(CredentialPresence.None, 401, "AUTH_ERROR"));
        Assert.AreEqual(SourceCredentialHealth.Invalid, SourceCredentialHealth.Evaluate(CredentialPresence.Saved, 401, "AUTH_ERROR"));
        Assert.AreEqual(SourceCredentialHealth.Invalid, SourceCredentialHealth.Evaluate(CredentialPresence.Saved, 403, "AUTH_ERROR"));
        Assert.AreEqual(SourceCredentialHealth.Valid, SourceCredentialHealth.Evaluate(CredentialPresence.Saved, 200, "HEALTHY"));
        Assert.AreEqual(SourceCredentialHealth.NotNeeded, SourceCredentialHealth.Evaluate(CredentialPresence.None, 200, "HEALTHY"));
        Assert.AreEqual(SourceCredentialHealth.Unreadable, SourceCredentialHealth.Evaluate(CredentialPresence.Unreadable, 401, "AUTH_ERROR"));
        Assert.AreEqual(SourceCredentialHealth.Unreadable, SourceCredentialHealth.Evaluate(CredentialPresence.Unreadable, 200, "HEALTHY"), "an unreadable blob is a renewal regardless of the probe");
        Assert.AreEqual(SourceCredentialHealth.Valid + "?", SourceCredentialHealth.Evaluate(CredentialPresence.Saved, null, "TIMEOUT"), "a timeout says nothing about the credential");
        Assert.AreEqual(SourceCredentialHealth.Unknown, SourceCredentialHealth.Evaluate(CredentialPresence.None, 500, "SERVER_ERROR"));
        Assert.AreEqual(SourceCredentialHealth.Missing, SourceCredentialHealth.AfterRefusal(credentialSent: false));
        Assert.AreEqual(SourceCredentialHealth.Invalid, SourceCredentialHealth.AfterRefusal(credentialSent: true));
        Assert.AreEqual(SourceCredentialHealth.Valid + "?", SourceCredentialHealth.AfterSave(new XmlAuth("operator", "p")));
        Assert.AreEqual(SourceCredentialHealth.NotNeeded + "?", SourceCredentialHealth.AfterSave(new XmlAuth()));

        foreach (var state in new[] { SourceCredentialHealth.Missing, SourceCredentialHealth.Invalid, SourceCredentialHealth.Unreadable })
        {
            Assert.IsTrue(SourceCredentialHealth.ShouldFailFast(state), state); Assert.AreEqual(SeverityLevel.Blocking, SourceCredentialHealth.Describe(state).Level);
        }
        foreach (var state in new[] { SourceCredentialHealth.Unknown, SourceCredentialHealth.NotNeeded, SourceCredentialHealth.Valid, SourceCredentialHealth.Valid + "?", "", null })
            Assert.IsFalse(SourceCredentialHealth.ShouldFailFast(state), state ?? "null");
        StringAssert.Contains(SourceCredentialHealth.Describe(SourceCredentialHealth.Invalid).Word, "yenileme");
        StringAssert.Contains(SourceCredentialHealth.Describe(SourceCredentialHealth.Missing).Word, "eksik");
        StringAssert.Contains(SourceCredentialHealth.Describe(SourceCredentialHealth.Unreadable).Word, "yeniden kaydedin");
        Assert.AreEqual("bilinmiyor", SourceCredentialHealth.Describe("garbage").Word);

        // The typed refusal names the status and the fact of a credential, never the credential.
        var sent = new XmlSourceAuthException(401, credentialSent: true); var unsent = new XmlSourceAuthException(403, credentialSent: false);
        StringAssert.Contains(sent.Message, "yenileyip"); StringAssert.Contains(unsent.Message, "girin"); StringAssert.Contains(sent.Message, "401"); Assert.AreEqual(403, unsent.HttpStatus);
    }

    [TestMethod]
    public async Task TheReaderRefusesAuthAnswersWithATypedReasonAndTheProbeSendsTheSavedCredential()
    {
        using var http = new HttpClient(new BasicAuthHandler("operator", "s3cret-value"));
        var reader = new XmlSourceReader(http);
        var location = "https://feed.example.com/products.xml";

        // Without a credential the server asks for one: the refusal says "enter one" and never the status alone.
        var missing = await Assert.ThrowsExceptionAsync<XmlSourceAuthException>(() => reader.ReadAsync(location, null));
        Assert.IsFalse(missing.CredentialSent); Assert.AreEqual(401, missing.HttpStatus); StringAssert.Contains(missing.Message, "kayıtlı kullanıcı adı ve şifre yok");
        // With a wrong credential: "renew it".
        var wrong = await Assert.ThrowsExceptionAsync<XmlSourceAuthException>(() => reader.ReadAsync(location, new XmlAuth("operator", "wrong")));
        Assert.IsTrue(wrong.CredentialSent); StringAssert.Contains(wrong.Message, "yenileyip"); Assert.IsFalse(wrong.Message.Contains("wrong") || wrong.Message.Contains("operator"), "the refusal never quotes the credential");
        // With the right one the feed reads.
        StringAssert.Contains(await reader.ReadAsync(location, new XmlAuth("operator", "s3cret-value")), "<Products>");

        // The probe: without the saved credential a protected feed reads as an auth error; with it, healthy.
        var source = new XmlSource { Id = Guid.NewGuid().ToString("N"), Name = "Korumalı", Location = location };
        var bare = await XmlSourceHealthChecker.CheckAsync(http, source);
        Assert.AreEqual("AUTH_ERROR", bare.State); Assert.AreEqual(401, bare.HttpStatus);
        var withAuth = await XmlSourceHealthChecker.CheckAsync(http, source, auth: new XmlAuth("operator", "s3cret-value"));
        Assert.AreEqual("HEALTHY", withAuth.State);
        Assert.AreEqual(SourceCredentialHealth.Valid, SourceCredentialHealth.Evaluate(CredentialPresence.Saved, withAuth.HttpStatus, withAuth.State));
        Assert.AreEqual(SourceCredentialHealth.Missing, SourceCredentialHealth.Evaluate(CredentialPresence.None, bare.HttpStatus, bare.State));
    }

    [TestMethod]
    public void ACredentialIsBoundToItsSourceAndAnUnreadableBlobIsARenewal()
    {
        var root = Path.Combine(Path.GetTempPath(), "cred-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var a = Guid.NewGuid().ToString("N"); var b = Guid.NewGuid().ToString("N");
            XmlAuthStore.Save(a, new XmlAuth("operator", "s3cret-value"), root);
            Assert.AreEqual(CredentialPresence.Saved, SourceCredentialHealth.PresenceOf(XmlAuthStore.Load(a, root)));
            Assert.AreEqual(CredentialPresence.None, SourceCredentialHealth.PresenceOf(XmlAuthStore.Load(b, root)), "another source never sees it");
            Assert.IsFalse(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any(f => File.ReadAllText(f).Contains("s3cret-value")), "the value is never on disk in the clear");
            // A blob this machine cannot open (garbage) is a renewal, not a crash.
            var path = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Single(f => f.Contains(a, StringComparison.OrdinalIgnoreCase));
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5 });
            Assert.AreEqual(CredentialPresence.Unreadable, XmlAuthStore.Presence(a, root));
            Assert.AreEqual(CredentialPresence.None, XmlAuthStore.Presence(b, root), "presence is per source");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    /// <summary>A server that protects its feed with Basic auth and answers 401 to anything else.</summary>
    sealed class BasicAuthHandler(string user, string password) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var expected = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(user + ":" + password));
            if (request.Headers.Authorization is { Scheme: "Basic" } h && h.Parameter == expected)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<Products><Product><Code>S1</Code></Product></Products>", System.Text.Encoding.UTF8, "application/xml") });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("") });
        }
    }
}
