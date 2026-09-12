using System.Globalization;

namespace TrMarketplaceHubDesktop;

/// <summary>Counts and oldest-occurrence times for the anomaly states the app already tracks.</summary>
public sealed class DashboardAnomalyInput
{
    public string Scope { get; set; } = "";
    public int OversellRiskProducts { get; set; }
    public DateTime? OversellOldestUtc { get; set; }
    public int StaleSources { get; set; }
    public DateTime? StaleSourceOldestUtc { get; set; }
    public int FailedSyncJobs { get; set; }
    public DateTime? FailedSyncOldestUtc { get; set; }
    public int UnmappedOrders { get; set; }
    public DateTime? UnmappedOrderOldestUtc { get; set; }
}

public sealed record DashboardAnomalyCard(
    string Key, string Title, string Severity, int Count,
    string Impact, string Age, string NextAction, string Route, string Scope);

public sealed record DashboardAnomalyView(IReadOnlyList<DashboardAnomalyCard> Cards, string Headline)
{
    public bool HasAnomalies => Cards.Count > 0;
}

/// <summary>
/// The dashboard's anomaly cards (#808). The board already listed these situations, but as undifferentiated
/// notification lines: a card has to answer what the problem costs, how long it has been true, and what to do
/// about it. Only states the app genuinely tracks are projected -- overselling risk, a supplier feed that has
/// gone quiet, failed dispatches, and orders whose lines match no product -- rather than inventing anomaly
/// types the data cannot support. Counts and timestamps are supplied by the caller (the queries have their own
/// owners); this decides severity, wording and order. Cards carry counts and a scope label only: no product,
/// order or customer identifiers, and a scope that looks like a location is replaced rather than shown.
/// </summary>
public static class DashboardAnomalies
{
    public const string Critical = "critical";
    public const string Warning = "warning";

    public static DashboardAnomalyView Project(DashboardAnomalyInput input, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(input);
        var scope = SafeScope(input.Scope);
        var cards = new List<(DashboardAnomalyCard Card, DateTime? Oldest)>();

        void Add(string key, string title, string severity, int count, DateTime? oldest, string impact, string action, string route)
        {
            // A count of zero is not an anomaly, whatever stale timestamp is still lying around next to it.
            if (count <= 0) return;
            cards.Add((new(key, title, severity, count, impact, Age(oldest, nowUtc), action, route, scope), oldest));
        }

        Add("oversell", "Aşırı satış riski", Critical, input.OversellRiskProducts, input.OversellOldestUtc,
            "Aktif ilanların stoğu yok; gelen satış karşılanamaz ve iptal/ceza riski doğar.",
            "Stok kilidi veya pasife alma kararını ürün havuzunda verin.", "products");
        Add("failed-sync", "Başarısız gönderim", Warning, input.FailedSyncJobs, input.FailedSyncOldestUtc,
            "Fiyat ve stok güncellemeleri kanala ulaşmadı; ilanlar eski değerlerle satışta.",
            "Sync kuyruğunda hatayı inceleyip yeniden deneyin.", "sync");
        Add("stale-source", "Bayat tedarikçi verisi", Warning, input.StaleSources, input.StaleSourceOldestUtc,
            "Kaynak uzun süredir güncellenmedi; fiyat ve stok tedarikçideki gerçeği yansıtmıyor olabilir.",
            "XML kaynağını çalıştırın veya bağlantı sağlığını kontrol edin.", "xml");
        Add("unmapped-order", "Eşleşmeyen sipariş satırı", Warning, input.UnmappedOrders, input.UnmappedOrderOldestUtc,
            "Sipariş satırları bir ürünle eşleşmiyor; stok düşümü ve raporlama eksik kalıyor.",
            "Sipariş istisna kuyruğunda SKU eşlemesini düzeltin.", "orders");

        // Severity first -- the one that costs money leads regardless of age -- then oldest first within a
        // severity, because a problem that has been true for a month is worse than one a minute old.
        var ordered = cards
            .OrderBy(x => x.Card.Severity == Critical ? 0 : 1)
            .ThenBy(x => x.Oldest ?? DateTime.MaxValue)
            .Select(x => x.Card)
            .ToArray();

        var headline = ordered.Length == 0
            ? "Açık anomali yok."
            : $"{ordered.Length.ToString("N0", CultureInfo.CurrentCulture)} anomali türü · {ordered.Sum(c => c.Count).ToString("N0", CultureInfo.CurrentCulture)} kayıt";
        return new(ordered, headline);
    }

    static string Age(DateTime? oldest, DateTime nowUtc)
    {
        if (oldest is not { } at) return "süresi bilinmiyor";
        var elapsed = nowUtc - at;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        if (elapsed.TotalMinutes < 1) return "az önce başladı";
        if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes} dakikadır sürüyor";
        if (elapsed.TotalDays < 1) return $"{(int)elapsed.TotalHours} saattir sürüyor";
        return $"{(int)elapsed.TotalDays} gündür sürüyor";
    }

    static string SafeScope(string? scope)
    {
        var text = (scope ?? "").Trim();
        if (text.Length == 0) return "yerel veri";
        var looksLikeLocation = text.Contains("://", StringComparison.Ordinal) || text.Contains('/', StringComparison.Ordinal)
            || text.Contains('\\', StringComparison.Ordinal) || text.Contains('?', StringComparison.Ordinal);
        return looksLikeLocation ? "yerel veri" : text;
    }
}
