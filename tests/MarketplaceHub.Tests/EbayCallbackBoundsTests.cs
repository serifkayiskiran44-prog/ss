using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

/// Coverage for #2535: EbayConnection.CompleteAsync's callback/query parsing
/// must be bounded (length, parameter count, per-parameter length) and
/// resilient to malformed percent-encoding, and must never let an invalid
/// callback reach the token endpoint.
[TestClass]
public sealed class EbayCallbackBoundsTests
{
    sealed class CountingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { RequestCount++; return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)); }
    }

    static EbaySettings Settings() => new("client123", "secret123", "ru-name-123", "https://example.com/ebay/callback", false);

    static (EbayAuthorization Attempt, CountingHandler Handler, EbayConnection Connector) NewAttempt()
    {
        var handler = new CountingHandler();
        var connector = new EbayConnection(new HttpClient(handler));
        var attempt = EbayConnection.Begin(Settings());
        return (attempt, handler, connector);
    }

    static string ValidCallback(EbayAuthorization attempt, string code = "auth-code-123") =>
        $"https://example.com/ebay/callback?state={attempt.State}&code={code}";

    [TestMethod]
    public async Task ValidCallbackParsesCorrectlyUpToTokenExchange()
    {
        var (attempt, handler, connector) = NewAttempt();
        // The fake handler returns 200 with no body, so token parsing fails after
        // the network call - proving the callback itself parsed successfully and
        // the code reached the token exchange step.
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => connector.CompleteAsync(attempt, ValidCallback(attempt)));
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task OversizedCallbackIsRejectedBeforeAnyRequest()
    {
        var (attempt, handler, connector) = NewAttempt();
        var oversized = ValidCallback(attempt) + "&pad=" + new string('x', 9000);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => connector.CompleteAsync(attempt, oversized));
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    public async Task TooManyParametersAreRejectedBeforeAnyRequest()
    {
        var (attempt, handler, connector) = NewAttempt();
        var extra = string.Join("&", Enumerable.Range(0, 60).Select(i => $"p{i}=v"));
        var callback = $"https://example.com/ebay/callback?state={attempt.State}&code=x&{extra}";
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => connector.CompleteAsync(attempt, callback));
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    public async Task OversizedSingleParameterIsRejectedBeforeAnyRequest()
    {
        var (attempt, handler, connector) = NewAttempt();
        var callback = $"https://example.com/ebay/callback?state={attempt.State}&code=x&junk={new string('y', 3000)}";
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => connector.CompleteAsync(attempt, callback));
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    public async Task DuplicateKeysAreRejected()
    {
        var (attempt, handler, connector) = NewAttempt();
        var callback = $"https://example.com/ebay/callback?state={attempt.State}&code=x&code=y";
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => connector.CompleteAsync(attempt, callback));
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    public async Task MissingStateIsRejected()
    {
        var (attempt, handler, connector) = NewAttempt();
        var callback = "https://example.com/ebay/callback?code=x";
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => connector.CompleteAsync(attempt, callback));
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    public async Task WrongStateIsRejected()
    {
        var (attempt, handler, connector) = NewAttempt();
        var callback = "https://example.com/ebay/callback?state=" + new string('0', 64) + "&code=x";
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => connector.CompleteAsync(attempt, callback));
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    public async Task ErrorCallbackConsumesAttemptWithoutAnyTokenRequest()
    {
        var (attempt, handler, connector) = NewAttempt();
        var callback = $"https://example.com/ebay/callback?state={attempt.State}&error=access_denied";
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => connector.CompleteAsync(attempt, callback));
        Assert.AreEqual(0, handler.RequestCount);
        // The attempt is now consumed - even a subsequent valid-looking callback
        // for the same attempt must be rejected.
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => connector.CompleteAsync(attempt, ValidCallback(attempt)));
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    public async Task ExpiredAttemptIsRejectedBeforeAnyRequest()
    {
        var handler = new CountingHandler();
        var connector = new EbayConnection(new HttpClient(handler));
        var attempt = EbayConnection.Begin(Settings(), DateTimeOffset.UtcNow.AddMinutes(-11));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => connector.CompleteAsync(attempt, ValidCallback(attempt)));
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    public async Task MalformedPercentEncodingNeverThrowsAnUnhandledExceptionType()
    {
        var (attempt, _, connector) = NewAttempt();
        // Uri.UnescapeDataString tolerates malformed sequences (passes them through
        // literally) rather than throwing - the guarantee this locks in is that
        // CompleteAsync never lets an unhandled UriFormatException/ArgumentException
        // escape from parsing; whatever happens next (state check, or a bogus code
        // reaching the token exchange, which the real eBay endpoint would reject)
        // is still a normal, typed outcome.
        var callback = $"https://example.com/ebay/callback?state={attempt.State}&code=%G0%zz";
        try { await connector.CompleteAsync(attempt, callback); }
        catch (InvalidOperationException) { /* expected family of outcome */ }
    }

    [TestMethod]
    public async Task TruncatedPercentEncodingNeverThrowsAnUnhandledExceptionType()
    {
        var (attempt, _, connector) = NewAttempt();
        var callback = $"https://example.com/ebay/callback?state={attempt.State}&code=abc%4";
        try { await connector.CompleteAsync(attempt, callback); }
        catch (InvalidOperationException) { /* expected family of outcome */ }
    }

    [TestMethod]
    public async Task RetryWithAValidCallbackStillWorksAfterAMalformedAttempt()
    {
        var (attempt, handler, connector) = NewAttempt();
        var malformed = $"https://example.com/ebay/callback?state={attempt.State}&code=x&junk={new string('y', 3000)}";
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => connector.CompleteAsync(attempt, malformed));
        Assert.AreEqual(0, handler.RequestCount);

        // A bounds/parse failure must not consume the attempt - the same attempt
        // can still complete with a well-formed callback afterward.
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => connector.CompleteAsync(attempt, ValidCallback(attempt)));
        Assert.AreEqual(1, handler.RequestCount, "The retry must have reached the token exchange step.");
    }

    [TestMethod]
    public async Task MaxLengthCodeIsAcceptedAndOneOverIsRejected()
    {
        var (attemptOk, handlerOk, connectorOk) = NewAttempt();
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => connectorOk.CompleteAsync(attemptOk, ValidCallback(attemptOk, new string('a', 1024))));
        Assert.AreEqual(1, handlerOk.RequestCount, "Exactly max-length code must still reach the token exchange step.");

        var (attemptBad, handlerBad, connectorBad) = NewAttempt();
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => connectorBad.CompleteAsync(attemptBad, ValidCallback(attemptBad, new string('a', 1025))));
        Assert.AreEqual(0, handlerBad.RequestCount, "Over-max-length code must be rejected before any request.");
    }
}
