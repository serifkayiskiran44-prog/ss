using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

/// Coverage for #2056: Ozon ClientId/ApiKey must have a bounded technical
/// length enforced before any HTTP request is built, in the single
/// Validate() shared by OzonSettingsStore.Save/Load and OzonConnection's
/// read calls.
[TestClass]
public sealed class OzonCredentialBoundsTests
{
    sealed class CountingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { RequestCount++; return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"result\":[]}") }); }
    }

    static OzonSettings Valid() => new(new string('1', 10), new string('a', 40));

    [TestMethod]
    public void NormalCredentialsPassValidation()
    {
        OzonConnection.Validate(Valid());
    }

    [TestMethod]
    public void EmptyClientIdIsRejected()
    {
        Assert.ThrowsException<ArgumentException>(() => OzonConnection.Validate(Valid() with { ClientId = "" }));
    }

    [TestMethod]
    public void ControlCharacterInApiKeyIsRejected()
    {
        Assert.ThrowsException<ArgumentException>(() => OzonConnection.Validate(Valid() with { ApiKey = "ab" }));
    }

    [TestMethod]
    public void ExactMaxLengthClientIdIsAccepted()
    {
        OzonConnection.Validate(Valid() with { ClientId = new string('1', 32) });
    }

    [TestMethod]
    public void OversizedClientIdIsRejected()
    {
        Assert.ThrowsException<ArgumentException>(() => OzonConnection.Validate(Valid() with { ClientId = new string('1', 33) }));
    }

    [TestMethod]
    public void ExactMaxLengthApiKeyIsAccepted()
    {
        OzonConnection.Validate(Valid() with { ApiKey = new string('a', 512) });
    }

    [TestMethod]
    public void OversizedApiKeyIsRejected()
    {
        Assert.ThrowsException<ArgumentException>(() => OzonConnection.Validate(Valid() with { ApiKey = new string('a', 513) }));
    }

    [TestMethod]
    public async Task NetworkHandlerIsNeverCalledForInvalidCredentials()
    {
        var handler = new CountingHandler();
        using var client = new HttpClient(handler);
        var connection = new OzonConnection(client);
        var oversized = Valid() with { ClientId = new string('1', 100) };

        await Assert.ThrowsExceptionAsync<ArgumentException>(() => connection.ReadWarehouseCountAsync(oversized));
        Assert.AreEqual(0, handler.RequestCount, "An invalid credential must never reach the network layer.");
    }

    [TestMethod]
    public void PersistedOversizedLegacySettingsFailClosedOnSave()
    {
        var path = Path.Combine(Path.GetTempPath(), "ozon-bounds-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            var store = new OzonSettingsStore(path);
            Assert.ThrowsException<ArgumentException>(() => store.Save(Valid() with { ApiKey = new string('a', 1000) }));
            Assert.IsFalse(File.Exists(path), "An invalid save must never write a settings file.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [TestMethod]
    public void ValidSettingsRoundTripThroughStoreAfterRestart()
    {
        var path = Path.Combine(Path.GetTempPath(), "ozon-bounds-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            new OzonSettingsStore(path).Save(Valid());
            var reopened = new OzonSettingsStore(path).Load();
            Assert.IsNotNull(reopened);
            Assert.AreEqual(Valid().ClientId, reopened!.ClientId);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [TestMethod]
    public void NonNumericClientIdIsStillRejected()
    {
        Assert.ThrowsException<ArgumentException>(() => OzonConnection.Validate(Valid() with { ClientId = "abc123" }));
    }
}
