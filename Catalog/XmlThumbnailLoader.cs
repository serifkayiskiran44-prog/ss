using System.IO;
using System.Net.Http;

namespace TrMarketplaceHubDesktop.Catalog;

public static class XmlThumbnailLoader
{
    public const int MaxBytes = 5 * 1024 * 1024;
    public static async Task<byte[]> ReadAsync(HttpClient http, string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0)
            throw new InvalidOperationException("Görsel önizlemesi için HTTPS adresi gerekli.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(12));
        for (var redirect = 0; redirect <= 3; redirect++)
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var next = response.Headers.Location ?? throw new InvalidOperationException("Görsel yönlendirmesi eksik.");
                uri = next.IsAbsoluteUri ? next : new Uri(uri, next);
                if (uri.Scheme != "https" || uri.UserInfo.Length > 0) throw new InvalidOperationException("Görsel yönlendirmesi HTTPS olmalı.");
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaxBytes) throw new InvalidOperationException("Önizleme görseli 5 MB sınırını aşıyor.");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var output = new MemoryStream(); var buffer = new byte[32768];
            while (true)
            {
                var read = await input.ReadAsync(buffer, timeout.Token); if (read == 0) break;
                if (output.Length + read > MaxBytes) throw new InvalidOperationException("Önizleme görseli 5 MB sınırını aşıyor.");
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        throw new InvalidOperationException("Görsel yönlendirme sınırı aşıldı.");
    }
}
