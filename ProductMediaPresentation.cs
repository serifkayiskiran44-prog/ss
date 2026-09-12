using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public sealed record ProductMediaPresentationInfo(string Key, string Label, string Glyph, string Hint, double BoxSize);

/// <summary>
/// The four states a product image can be in on a card (#797) -- loading, missing, broken, ready (and ready
/// from cache) -- as one shared description, so the placeholder and the picture are never sized or worded by
/// two different places. Every state reports the same box size: a placeholder that differs in size from the
/// image is precisely what makes a card jump when the picture lands. The remote URL is not display material --
/// a supplier link routinely carries a signature or a token in its query string, and a local path names
/// somebody's home folder -- so nothing here ever renders more than a host.
/// </summary>
public static class ProductMediaPresentation
{
    public const string Loading = "loading";
    public const string Missing = "missing";
    public const string Broken = "broken";
    public const string Ready = "ready";
    public const string Cached = "cached";

    /// <summary>One reserved box for every state, in device-independent pixels.</summary>
    public const double BoxSize = 160;

    public static ProductMediaPresentationInfo Describe(string key) => key switch
    {
        Loading => new(Loading, "Yükleniyor", "◌", "Görsel alınıyor…", BoxSize),
        Broken => new(Broken, "Görsel açılamadı", "⚠", "Bağlantı geçersiz veya görsel okunamadı.", BoxSize),
        Ready => new(Ready, "Görsel", "", "", BoxSize),
        Cached => new(Cached, "Görsel (önbellek)", "", "Önbellekten gösteriliyor.", BoxSize),
        _ => new(Missing, "Görsel yok", "▧", "Bu ürün için kayıtlı görsel yok.", BoxSize),
    };

    public static ProductMediaPresentationInfo Classify(ProductMediaRecord? record, bool loading, bool loaded, bool fromCache, bool failed = false)
    {
        if (record is null || string.IsNullOrWhiteSpace(record.Url)) return Describe(Missing);
        if (failed || record.Status is MediaStatus.InvalidUrl or MediaStatus.Timeout or MediaStatus.NotFound or MediaStatus.TooLarge or MediaStatus.RateLimited or MediaStatus.UnsupportedFormat or MediaStatus.Error) return Describe(Broken);
        if (loaded) return Describe(fromCache ? Cached : Ready);
        return Describe(loading ? Loading : Missing);
    }

    /// <summary>Host only -- never the path, the query, or any credentials the URL carries; a local file says only that it is local.</summary>
    public static string SafeSourceLabel(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "—";
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return "—";
        if (uri.IsFile) return "yerel dosya";
        return string.IsNullOrWhiteSpace(uri.Host) ? "—" : uri.Host;
    }

    /// <summary>One line for a failed image, in the operator's words, naming the host and nothing else from the URL.</summary>
    public static string FailureText(ProductMediaRecord? record, string? rawError)
    {
        var reason = record?.Status switch
        {
            MediaStatus.NotFound => "Görsel bulunamadı (404)",
            MediaStatus.Timeout => "Görsel zaman aşımına uğradı",
            MediaStatus.InvalidUrl => "Görsel bağlantısı geçersiz",
            MediaStatus.TooLarge => "Görsel boyutu sınırı aşıyor",
            MediaStatus.RateLimited => "Görsel sunucusu istek sınırı uyguladı",
            MediaStatus.UnsupportedFormat => "Görsel biçimi desteklenmiyor",
            _ => "Görsel açılamadı",
        };
        // rawError is deliberately not echoed: it is the transport's own message and, in this app, has carried
        // the full signed URL. Only its shape is used -- the reason above -- plus the host.
        _ = rawError;
        var text = $"{reason} · kaynak: {SafeSourceLabel(record?.Url)}";
        return text.Length > 200 ? text[..200] : text;
    }
}
