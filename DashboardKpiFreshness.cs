namespace TrMarketplaceHubDesktop;

public sealed record DashboardKpiFreshnessInfo(string State, string Label, string When, string Scope)
{
    public bool IsStale => State != DashboardKpiFreshness.Fresh;
}

public sealed record DashboardKpiCard(string Key, string Title, string Value, DashboardKpiFreshnessInfo Freshness);

/// <summary>
/// Per-card data currency for the dashboard (#807). The failure this exists to prevent is a board that keeps
/// showing yesterday's figures after a refresh fails, with nothing on screen to say so -- and the related one,
/// where a card's number is stamped with the moment the board was *rendered* rather than the moment its data
/// last changed. Each KPI therefore carries its own data time. Anything that is not demonstrably current --
/// stale, unknown, or produced before a failed refresh -- is marked, in words as well as colour.
/// The scope line says what the card counts; it is not a place for source locations, so anything that looks
/// like one is replaced rather than displayed.
/// </summary>
public static class DashboardKpiFreshness
{
    public const string Fresh = "fresh";
    public const string Stale = "stale";
    public const string NoData = "no-data";

    /// <summary>How old a figure may be before the board stops calling it current.</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromHours(6);

    public static DashboardKpiFreshnessInfo Describe(DateTime? dataAtUtc, DateTime nowUtc, TimeSpan maxAge, string scope)
    {
        var safeScope = SafeScope(scope);
        if (dataAtUtc is not { } at) return new(NoData, "Veri zamanı bilinmiyor", "veri zamanı bilinmiyor", safeScope);
        var age = nowUtc - at;
        var when = Ago(age);
        return age > maxAge
            ? new(Stale, $"Veri eski ({when})", when, safeScope)
            : new(Fresh, $"Güncel ({when})", when, safeScope);
    }

    /// <summary>The same card after a refresh that did not succeed: the figure predates it, whatever its age says.</summary>
    public static DashboardKpiFreshnessInfo AfterRefreshFailure(DashboardKpiFreshnessInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return info with { State = Stale, Label = $"Yenilenemedi; ekrandaki değer {info.When} durumunda" };
    }

    public static IReadOnlyList<DashboardKpiCard> ForSnapshot(DashboardSnapshot snapshot, DateTime nowUtc, TimeSpan? maxAge = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var age = maxAge ?? DefaultMaxAge;
        DashboardKpiCard Card(string key, string title, int value, DateTime? at, string scope) =>
            new(key, title, value.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), Describe(at, nowUtc, age, scope));

        return
        [
            Card("products", "Aktif ürün", snapshot.ActiveProducts, snapshot.ProductsAtUtc, "yerel ürün havuzu"),
            Card("out-of-stock", "Stokta olmayan", snapshot.OutOfStockProducts, snapshot.ProductsAtUtc, "yerel ürün havuzu"),
            Card("orders", "Açık sipariş", snapshot.OpenOrders, snapshot.OrdersAtUtc, "kayıtlı siparişler"),
            Card("sync", "Sync hatası", snapshot.FailedSyncs, snapshot.SyncAtUtc, "gönderim kuyruğu"),
            Card("xml", "XML kaynağı", snapshot.XmlSources, snapshot.LastXmlUtc, "tedarikçi kaynakları"),
            Card("connections", "Bağlantı uyarısı", snapshot.ConnectionIssues, snapshot.ConnectionsAtUtc, "kanal bağlantıları"),
        ];
    }

    // A coverage description, never a location. Anything resembling a URL or a path is replaced outright rather
    // than trimmed, because a KPI card has no business naming where the bytes came from.
    static string SafeScope(string? scope)
    {
        var text = (scope ?? "").Trim();
        if (text.Length == 0) return "yerel veri";
        var looksLikeLocation = text.Contains("://", StringComparison.Ordinal) || text.Contains('\\', StringComparison.Ordinal)
            || text.Contains('/', StringComparison.Ordinal) || text.Contains('?', StringComparison.Ordinal);
        return looksLikeLocation ? "yerel veri" : text;
    }

    static string Ago(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        if (elapsed.TotalMinutes < 1) return "az önce";
        if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes} dakika önce";
        if (elapsed.TotalDays < 1) return $"{(int)elapsed.TotalHours} saat önce";
        return $"{(int)elapsed.TotalDays} gün önce";
    }
}
