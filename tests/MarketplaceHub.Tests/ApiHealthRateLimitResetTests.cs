using System;
using System.Net;
using System.Net.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #2561 (API HEALTH: X-RateLimit-Reset Unix epoch parse bug). A numeric reset is read as a relative delta when it is
// small, an absolute Unix epoch second when it is large enough to be one, and left null -- never guessed, never an
// overflow -- when it is neither, including the classic epoch-milliseconds mistake. Multiple values under one header
// name are each tried before the header is abandoned; the RFC date form, Retry-After and the 429 backoff path are
// unaffected.
[TestClass]
public sealed class ApiHealthRateLimitResetTests
{
    static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    static HttpResponseMessage Response(HttpStatusCode status, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(status);
        foreach (var (name, value) in headers) response.Headers.TryAddWithoutValidation(name, value);
        return response;
    }

    [TestMethod]
    public void ARelativeDeltaAndARealEpochSecondAreEachReadCorrectlyAndTheFormerBugsInputsAreRejectedNeverOverflowing()
    {
        // A real Unix epoch second (2026-09-15T00:00:00Z, well into the billions) now reaches the epoch branch -- the bug made this impossible.
        var epoch = ((DateTimeOffset)new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeSeconds();
        var future = ApiHealthClassifier.FromResponse(Response(HttpStatusCode.TooManyRequests, ("X-RateLimit-Reset", epoch.ToString())), Now);
        Assert.AreEqual(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero), future.RateLimitResetUtc);

        // A relative delta of 60 seconds: now + 60s. The upper boundary of the relative range still reads as relative; one past it must not silently become an epoch second either (it lands in the ambiguous gap and is null).
        Assert.AreEqual(Now.AddSeconds(60), Reset("60"));
        Assert.AreEqual(Now.AddSeconds(ApiHealthClassifier.RelativeSecondsMax), Reset(ApiHealthClassifier.RelativeSecondsMax.ToString()));
        Assert.IsNull(Reset((ApiHealthClassifier.RelativeSecondsMax + 1).ToString()), "one past the relative ceiling, still far short of a real epoch second: ambiguous, not guessed");

        // A real epoch second at the boundary reads correctly; one below the epoch floor (and above the relative ceiling) is the ambiguous gap.
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(ApiHealthClassifier.EpochSecondsMin), Reset(ApiHealthClassifier.EpochSecondsMin.ToString()));
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(ApiHealthClassifier.EpochSecondsMax), Reset(ApiHealthClassifier.EpochSecondsMax.ToString()));
        Assert.IsNull(Reset((ApiHealthClassifier.EpochSecondsMin - 1).ToString()));

        // reset=0 is a valid relative delta of zero seconds -- now.
        Assert.AreEqual(Now, Reset("0"));

        // Epoch milliseconds sent by mistake (13 digits, far past the epoch-seconds ceiling): never misread as a wildly wrong date, never an overflow -- null.
        var epochMillis = epoch * 1000; Assert.IsNull(Reset(epochMillis.ToString()));
        Assert.IsNull(Reset(((DateTimeOffset)new DateTime(2100, 6, 1, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeSeconds().ToString()), "past the generous future bound: ambiguous, not guessed");

        // Int64 extremes, negative, malformed and whitespace never throw and never resolve to a value.
        Assert.IsNull(Reset(long.MaxValue.ToString())); Assert.IsNull(Reset(long.MinValue.ToString())); Assert.IsNull(Reset("-1")); Assert.IsNull(Reset("not-a-number")); Assert.IsNull(Reset("   ")); Assert.IsNull(Reset(""));

        // Multiple values under one header name: a malformed first value does not give up on the header when a later value parses.
        var multi = ApiHealthClassifier.FromResponse(Response(HttpStatusCode.TooManyRequests, ("X-RateLimit-Reset", "not-a-number"), ("X-RateLimit-Reset", "45")), Now);
        Assert.AreEqual(Now.AddSeconds(45), multi.RateLimitResetUtc);

        // The RFC date form is a regression check: unaffected by the numeric-range change.
        Assert.AreEqual(new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero), Reset("Sun, 20 Sep 2026 00:00:00 GMT"));

        // 429 Retry-After / backoff behavior is unaffected by this fix (regression): a 429 with no explicit reset still backs off for a minute by default.
        var rateLimited = ApiHealthClassifier.FromResponse(Response(HttpStatusCode.TooManyRequests), Now);
        Assert.AreEqual((429, "RATE_LIMITED", 60), (rateLimited.HttpStatus, rateLimited.State, rateLimited.RetryAfterSeconds)); Assert.AreEqual(Now.AddSeconds(60), rateLimited.BackoffUntilUtc);

        // No header at all is null; timezone-independent fixtures throughout (every expectation above is expressed in UTC via DateTimeOffset).
        Assert.IsNull(Reset(null));
    }

    static DateTimeOffset? Reset(string? value)
        => ApiHealthClassifier.FromResponse(value is null ? Response(HttpStatusCode.OK) : Response(HttpStatusCode.TooManyRequests, ("X-RateLimit-Reset", value)), Now).RateLimitResetUtc;
}
