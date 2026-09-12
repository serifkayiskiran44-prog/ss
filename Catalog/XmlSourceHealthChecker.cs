using System.IO;
using System.Net.Http;

namespace TrMarketplaceHubDesktop.Catalog;

// Reuses ApiHealthClassifier (ApiHealth.cs) for status classification instead of a second scheme; adds only
// what that classifier doesn't cover -- latency measurement and the local-file vs. HTTPS distinction an XML
// source (unlike a marketplace API) actually has.
public sealed record XmlSourceHealthResult(string State, int? HttpStatus, long LatencyMs, string ErrorMessage, DateTimeOffset CheckedUtc);

public static class XmlSourceHealthChecker
{
    public static async Task<XmlSourceHealthResult> CheckAsync(HttpClient http, XmlSource source, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        var checkedUtc = DateTimeOffset.UtcNow;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var location = (source.Location ?? "").Trim();
        if (location.Length == 0) return new("NOT_CONFIGURED", null, 0, "XML adresi veya dosyası tanımlı değil.", checkedUtc);

        if (File.Exists(location))
        {
            clock.Stop();
            var info = new FileInfo(location);
            return info.Length > 0
                ? new("HEALTHY", null, clock.ElapsedMilliseconds, "", checkedUtc)
                : new("CLIENT_ERROR", null, clock.ElapsedMilliseconds, "Dosya boş.", checkedUtc);
        }
        if (!Uri.TryCreate(location, UriKind.Absolute, out var uri) || uri.Scheme != "https")
        {
            clock.Stop();
            return new("CLIENT_ERROR", null, clock.ElapsedMilliseconds, "HTTPS adresi veya var olan bir dosya gerekli.", checkedUtc);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            clock.Stop();
            var observation = ApiHealthClassifier.FromResponse(response, checkedUtc);
            return new(observation.State, observation.HttpStatus, clock.ElapsedMilliseconds, observation.ErrorMessage, checkedUtc);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            clock.Stop();
            return new("TIMEOUT", null, clock.ElapsedMilliseconds, "XML kaynağı zaman aşımına uğradı.", checkedUtc);
        }
        catch (HttpRequestException error)
        {
            clock.Stop();
            var observation = ApiHealthClassifier.FromException(error, checkedUtc);
            return new(observation.State, null, clock.ElapsedMilliseconds, observation.ErrorMessage, checkedUtc);
        }
    }
}
