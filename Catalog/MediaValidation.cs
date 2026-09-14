using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.IO;
using System.Collections.Concurrent;

namespace TrMarketplaceHubDesktop.Catalog;

public sealed record MediaValidationResult(MediaStatus Status, string Error = "", string ContentHash = "", long? Bytes = null, string ContentType = "");

public sealed class MediaValidationService
{
    public const long MaxBytes = 20 * 1024 * 1024;
    const int MaxRedirects = 5;
    static readonly HashSet<string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/png", "image/gif", "image/webp", "image/bmp", "image/tiff" };
    readonly HttpClient http;
    readonly IReadOnlyCollection<string> approvedLocalRoots;
    readonly ConcurrentDictionary<string, (DateTimeOffset At, MediaValidationResult Result)> cache = new(StringComparer.Ordinal);

    public MediaValidationService(HttpClient? httpClient = null, IReadOnlyCollection<string>? approvedLocalRoots = null)
    {
        http = httpClient ?? SafeRemoteHttp.CreateClient(TimeSpan.FromSeconds(15));
        this.approvedLocalRoots = approvedLocalRoots ?? MediaFileAccessPolicy.DefaultApprovedRoots;
    }

    public void Invalidate(string url)
    {
        var key = MediaStore.NormalizeUrl(url);
        if (key.Length > 0) cache.TryRemove(key, out _);
    }

    public async Task<MediaValidationResult> ValidateWithRetryAsync(ProductMediaRecord media, CancellationToken cancellationToken = default)
    {
        var key = MediaStore.NormalizeUrl(media.Url);
        if (key.Length > 0 && cache.TryGetValue(key, out var cached) && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromMinutes(5)) return cached.Result;
        MediaValidationResult result = default!;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            result = await ValidateAsync(media, cancellationToken).ConfigureAwait(false);
            if (result.Status is not (MediaStatus.Timeout or MediaStatus.Error or MediaStatus.RateLimited) || attempt == 1) break;
            await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
        }
        if (key.Length > 0 && result.Status == MediaStatus.Ready) cache[key] = (DateTimeOffset.UtcNow, result);
        return result;
    }

    public async Task<MediaValidationResult> ValidateAsync(ProductMediaRecord media, CancellationToken cancellationToken = default)
    {
        var normalized = MediaStore.NormalizeUrl(media.Url);
        if (normalized.Length == 0) return new(MediaStatus.InvalidUrl, "Yalnız HTTPS veya yerel file adresleri desteklenir.");
        try
        {
            byte[] bytes;
            string contentType;
            if (normalized.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                var requestedPath = new Uri(normalized).LocalPath;
                if (!MediaFileAccessPolicy.TryResolveApprovedFile(requestedPath, approvedLocalRoots, out var path, out var accessError))
                    return new(MediaStatus.InvalidUrl, accessError);
                if (!File.Exists(path)) return new(MediaStatus.NotFound, "Yerel görsel dosyası bulunamadı.");
                var info = new FileInfo(path);
                if (info.Length > MaxBytes) return new(MediaStatus.TooLarge, "Görsel 20 MB sınırını aşıyor.", Bytes: info.Length);
                bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                contentType = ContentTypeFromExtension(path);
            }
            else
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var currentUri = new Uri(normalized);
                HttpResponseMessage response;
                var redirects = 0;
                while (true)
                {
                    if (!currentUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                        return new(MediaStatus.InvalidUrl, "Yönlendirme yalnız HTTPS hedeflerine izin verir.");
                    using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
                    response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                    if (!IsRedirect(response.StatusCode)) break;
                    var location = response.Headers.Location;
                    response.Dispose();
                    if (location is null) return new(MediaStatus.Error, "Yönlendirme adresi eksik.");
                    if (++redirects > MaxRedirects) return new(MediaStatus.Error, "Yönlendirme sınırı aşıldı.");
                    currentUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                }
                using (response)
                {
                    if (response.StatusCode == HttpStatusCode.NotFound) return new(MediaStatus.NotFound, "Görsel adresi 404 döndürdü.");
                    if ((int)response.StatusCode == 429) return new(MediaStatus.RateLimited, "Görsel sunucusu istek sınırı uyguluyor.");
                    if (!response.IsSuccessStatusCode) return new(MediaStatus.Error, $"Görsel adresi HTTP {(int)response.StatusCode} döndürdü.");
                    if (response.Content.Headers.ContentLength is > MaxBytes) return new(MediaStatus.TooLarge, "Görsel 20 MB sınırını aşıyor.", Bytes: response.Content.Headers.ContentLength);
                    await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                    await using var memory = new MemoryStream();
                    var buffer = new byte[81920];
                    while (true)
                    {
                        var count = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                        if (count == 0) break;
                        if (memory.Length + count > MaxBytes) return new(MediaStatus.TooLarge, "Görsel 20 MB sınırını aşıyor.", Bytes: memory.Length + count);
                        await memory.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                    }
                    bytes = memory.ToArray();
                    contentType = response.Content.Headers.ContentType?.MediaType ?? ContentTypeFromExtension(currentUri.AbsolutePath);
                }
            }
            if (bytes.Length == 0) return new(MediaStatus.Error, "Görsel boş.");
            if (!AllowedTypes.Contains(contentType) && !AllowedTypes.Contains(ContentTypeFromExtension(media.Url))) return new(MediaStatus.UnsupportedFormat, $"Desteklenmeyen görsel biçimi: {contentType}.", Bytes: bytes.Length, ContentType: contentType);
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            return new(MediaStatus.Ready, Bytes: bytes.Length, ContentHash: hash, ContentType: contentType);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(MediaStatus.Timeout, "Görsel doğrulama zaman aşımına uğradı."); }
        catch (HttpRequestException error) { return new(MediaStatus.Error, "Görsel alınamadı: " + error.Message); }
        catch (IOException error) { return new(MediaStatus.Error, "Görsel dosyası okunamadı: " + error.Message); }
    }

    static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    static string ContentTypeFromExtension(string value) => Path.GetExtension(value).ToLowerInvariant() switch { ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".gif" => "image/gif", ".webp" => "image/webp", ".bmp" => "image/bmp", ".tif" or ".tiff" => "image/tiff", _ => "" };
}
