using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.IO;

namespace TrMarketplaceHubDesktop.Catalog;

public sealed record MediaValidationResult(MediaStatus Status, string Error = "", string ContentHash = "", long? Bytes = null, string ContentType = "");

public sealed class MediaValidationService
{
    public const long MaxBytes = 20 * 1024 * 1024;
    static readonly HashSet<string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/png", "image/gif", "image/webp", "image/bmp", "image/tiff" };
    readonly HttpClient http;

    public MediaValidationService(HttpClient? httpClient = null) => http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

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
                var path = new Uri(normalized).LocalPath;
                if (!File.Exists(path)) return new(MediaStatus.NotFound, "Yerel görsel dosyası bulunamadı.");
                var info = new FileInfo(path);
                if (info.Length > MaxBytes) return new(MediaStatus.TooLarge, "Görsel 20 MB sınırını aşıyor.", Bytes: info.Length);
                bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                contentType = ContentTypeFromExtension(path);
            }
            else
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, normalized);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound) return new(MediaStatus.NotFound, "Görsel adresi 404 döndürdü.");
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
                contentType = response.Content.Headers.ContentType?.MediaType ?? ContentTypeFromExtension(new Uri(normalized).AbsolutePath);
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

    static string ContentTypeFromExtension(string value) => Path.GetExtension(value).ToLowerInvariant() switch { ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".gif" => "image/gif", ".webp" => "image/webp", ".bmp" => "image/bmp", ".tif" or ".tiff" => "image/tiff", _ => "" };
}
