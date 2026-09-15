using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Net;
using System.Net.Http;
using TrMarketplaceHubDesktop;

namespace MarketplaceHub.Tests;

/// Coverage for #2561: ApiHealthClassifier.ParseResetHeader's numeric branch
/// had an unreachable epoch case - the outer bound (<=31_536_000) always
/// rejected a real epoch-seconds value before the inner ">1_000_000_000" check
/// could ever see it, so any provider sending an absolute epoch reset was
/// silently dropped to null instead of being converted.
[TestClass]
public sealed class ApiHealthRateLimitResetTests
{
    static HttpResponseMessage Response(string headerValue)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", headerValue);
        return response;
    }

    static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void SmallRelativeSecondsIsNowPlusDelta()
    {
        var observation = TrMarketplaceHubDesktop.ApiHealthClassifier.FromResponse(Response("60"), Now);
        Assert.AreEqual(Now.AddSeconds(60), observation.RateLimitResetUtc);
    }

    [TestMethod]
    public void ExactRelativeUpperBoundIsAcceptedAndOneOverIsRejected()
    {
        var atBound = TrMarketplaceHubDesktop.ApiHealthClassifier.FromResponse(Response("31536000"), Now);
        Assert.AreEqual(Now.AddSeconds(31_536_000), atBound.RateLimitResetUtc);

        // One past the relative bound but still well below the epoch-plausible
        // window - this is the ambiguous gap and must be null, not silently
        // reinterpreted as some other unit.
        var overBound = TrMarketplaceHubDesktop.ApiHealthClassifier.FromResponse(Response("31536001"), Now);
        Assert.IsNull(overBound.RateLimitResetUtc);
    }

    [TestMethod]
    public void ValidFutureUnixEpochSecondsConvertsToExactUtc()
    {
        var epoch = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();
        var observation = TrMarketplaceHubDesktop.ApiHealthClassifier.FromResponse(Response(epoch.ToString()), Now);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(epoch), observation.RateLimitResetUtc);
    }

    [TestMethod]
    public void PastUnixEpochSecondsStillConvertsDeterministically()
    {
        var epoch = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var observation = TrMarketplaceHubDesktop.ApiHealthClassifier.FromResponse(Response(epoch.ToString()), Now);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(epoch), observation.RateLimitResetUtc);
    }

    [DataTestMethod]
    [DataRow("9223372036854775807")] // long.MaxValue
    [DataRow("-1")]
    [DataRow("not-a-number")]
    [DataRow("   ")]
    [DataRow("")]
    public void MalformedOrExtremeValuesNeverThrowAndResultInNull(string raw)
    {
        var observation = TrMarketplaceHubDesktop.ApiHealthClassifier.FromResponse(Response(raw), Now);
        Assert.IsNull(observation.RateLimitResetUtc);
    }

    [TestMethod]
    public void EpochMillisecondsMistakeIsRejectedNotMisinterpreted()
    {
        // A provider mistakenly sending epoch *milliseconds* instead of seconds
        // (13 digits, ~1700000000000) must never be silently accepted as a
        // plausible epoch-seconds value (which would land far in the future).
        var msMistake = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var observation = TrMarketplaceHubDesktop.ApiHealthClassifier.FromResponse(Response(msMistake.ToString()), Now);
        Assert.IsNull(observation.RateLimitResetUtc);
    }

    [TestMethod]
    public void ZeroResetIsTreatedAsRelativeNow()
    {
        var observation = TrMarketplaceHubDesktop.ApiHealthClassifier.FromResponse(Response("0"), Now);
        Assert.AreEqual(Now, observation.RateLimitResetUtc);
    }

    [TestMethod]
    public void RfcDateFormResetStillWorks()
    {
        var observation = TrMarketplaceHubDesktop.ApiHealthClassifier.FromResponse(Response("Wed, 21 Oct 2026 07:28:00 GMT"), Now);
        Assert.AreEqual(new DateTimeOffset(2026, 10, 21, 7, 28, 0, TimeSpan.Zero), observation.RateLimitResetUtc);
    }

    [TestMethod]
    public void MultipleHeaderValuesUsesTheFirst()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", new[] { "60", "120" });
        var observation = TrMarketplaceHubDesktop.ApiHealthClassifier.FromResponse(response, Now);
        Assert.AreEqual(Now.AddSeconds(60), observation.RateLimitResetUtc);
    }

    [TestMethod]
    public void FallsBackToNextHeaderNameWhenFirstIsMalformed()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", "garbage");
        response.Headers.TryAddWithoutValidation("RateLimit-Reset", "60");
        var observation = TrMarketplaceHubDesktop.ApiHealthClassifier.FromResponse(response, Now);
        Assert.AreEqual(Now.AddSeconds(60), observation.RateLimitResetUtc);
    }

    [TestMethod]
    public void RetryAfterAnd429BackoffRegressionUnaffected()
    {
        var response = new HttpResponseMessage((HttpStatusCode)429);
        response.Headers.TryAddWithoutValidation("Retry-After", "30");
        var observation = TrMarketplaceHubDesktop.ApiHealthClassifier.FromResponse(response, Now);
        Assert.AreEqual("RATE_LIMITED", observation.State);
        Assert.AreEqual(30, observation.RetryAfterSeconds);
    }
}
