using System.Globalization;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public sealed record SupplierSource(string SourceId, string SupplierId, int Priority, bool Included, bool Healthy);
public sealed record SourceSelection(string Status, SupplierSource? Selected, IReadOnlyList<string> Reasons);

/// <summary>One source's eligibility to stand in for a product's primary source: whether it may, why or why not, when it last observed the product and which fields it could refresh.</summary>
public sealed record SourceFallbackCandidate(string SourceId, string Name, int Priority, bool Eligible, IReadOnlyList<string> Reasons, DateTime? SeenUtc, IReadOnlyList<string> Refreshes);

/// <summary>The verdict for one product: the primary's state word and reasons, every other source as a candidate in order, the recommended one (null when the primary is fine, none is eligible or the best two tie) and one headline. Words only — nothing here switches anything.</summary>
public sealed record SourceFallbackVerdict(string Status, string PrimarySourceId, string PrimaryState, IReadOnlyList<string> PrimaryReasons, SourceFallbackCandidate? Recommended, IReadOnlyList<SourceFallbackCandidate> Candidates, string Headline);

/// <summary>
/// The multi-supplier source graph. <see cref="Select"/> picks the first healthy included supplier by rank (the
/// linked-supplier flow). <see cref="Evaluate"/> (#897) says whether a product's primary source is healthy, stale or
/// unavailable and which other source could stand in for it — judged by that source's own availability (enabled,
/// credential state, last health check), by how fresh its last observation of this product is (price and stock
/// freshness, the #896 grace: three intervals, at least six hours), by whether its mapping covers the required
/// fields, and by the operator's locks (a fallback that could refresh neither price nor stock is no fallback).
/// Equal top priorities are left to the operator. The verdict is words for a person; the switch itself is never
/// automatic and never silent. Names only — never a feed address, never a value.
/// </summary>
public static class LinkedSourceGraph
{
    public const string PrimaryHealthy = "PRIMARY_HEALTHY";
    public const string FallbackAvailable = "FALLBACK_AVAILABLE";
    public const string EqualCandidates = "EQUAL_CANDIDATES";
    public const string NoFallback = "NO_FALLBACK";
    public const string NoPrimary = "NO_PRIMARY";
    public const string Healthy = "sağlıklı";
    public const string Stale = "bayat";
    public const string Unavailable = "erişilemez";
    public const string Missing = "silinmiş";
    public const string Tag = "source-fallback";

    public static SourceSelection Select(IEnumerable<SupplierSource> sources)
    {
        var rows = sources.Where(x => x.Included).OrderBy(x => x.Priority).ToArray();
        var healthy = rows.FirstOrDefault(x => x.Healthy);
        return healthy is null ? new("SOURCE_UNAVAILABLE", null, rows.Select(x => $"{x.SourceId}:unhealthy").ToArray()) : new("SELECTED", healthy, []);
    }

    /// <summary>Judges the product's primary source and every other source as a fallback candidate from persisted facts only: the source records, and when each source last carried the product.</summary>
    public static SourceFallbackVerdict Evaluate(CatalogProduct product, IReadOnlyList<XmlSource> sources, IReadOnlyDictionary<string, DateTime> seenBySource, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(product); ArgumentNullException.ThrowIfNull(sources); ArgumentNullException.ThrowIfNull(seenBySource);
        var primaryId = product.SourceId ?? "";
        if (primaryId.Length == 0) return new(NoPrimary, "", "", [], null, [], "Elle oluşturulmuş ürün; birincil kaynak yok.");
        var primary = sources.FirstOrDefault(s => string.Equals(s.Id, primaryId, StringComparison.Ordinal));
        var (primaryState, primaryReasons) = PrimaryState(product, primary, seenBySource, nowUtc);

        var candidates = sources.Where(s => !string.Equals(s.Id, primaryId, StringComparison.Ordinal))
            .Select(s => Candidate(product, s, seenBySource, nowUtc))
            .OrderByDescending(c => c.Eligible).ThenByDescending(c => c.Priority).ThenByDescending(c => c.SeenUtc ?? DateTime.MinValue).ThenBy(c => c.Name, StringComparer.CurrentCulture)
            .ToArray();
        var eligible = candidates.Where(c => c.Eligible).ToArray();

        if (primaryState == Healthy)
            return new(PrimaryHealthy, primaryId, primaryState, primaryReasons, null, candidates,
                eligible.Length == 0 ? "Birincil kaynak sağlıklı; yedek gerekmiyor." : $"Birincil kaynak sağlıklı; yedek gerekmiyor ({Count(eligible.Length)} uygun aday hazır).");
        var trouble = $"Birincil kaynak {primaryState}" + (primaryReasons.Count > 0 ? $" ({string.Join("; ", primaryReasons)})" : "");
        if (eligible.Length == 0) return new(NoFallback, primaryId, primaryState, primaryReasons, null, candidates, $"{trouble}; uygun yedek yok.");
        if (eligible.Length > 1 && eligible[0].Priority == eligible[1].Priority)
        {
            var tied = eligible.Where(c => c.Priority == eligible[0].Priority).Select(c => c.Name).ToArray();
            return new(EqualCandidates, primaryId, primaryState, primaryReasons, null, candidates, $"{trouble}; {Count(tied.Length)} eşit öncelikli aday ({string.Join(", ", tied)}) — seçim operatörün, geçiş otomatik değildir.");
        }
        var best = eligible[0];
        return new(FallbackAvailable, primaryId, primaryState, primaryReasons, best, candidates, $"{trouble}; yedek uygun: {best.Name} (öncelik {best.Priority.ToString(CultureInfo.CurrentCulture)}, gözlem {Ago(nowUtc - best.SeenUtc!.Value)}) — geçiş otomatik değildir.");
    }

    static (string State, IReadOnlyList<string> Reasons) PrimaryState(CatalogProduct product, XmlSource? primary, IReadOnlyDictionary<string, DateTime> seen, DateTime nowUtc)
    {
        if (primary is null) return (Missing, ["kaynak kaydı bulunamadı"]);
        var reasons = Availability(primary);
        if (product.SourceMissing) reasons.Add("ürün birincil beslemede artık yok");
        if (reasons.Count > 0) return (Unavailable, reasons);
        var observed = seen.TryGetValue(primary.Id, out var at) ? at : primary.LastSuccessfulFeedUtc;
        if (observed is null) return (Stale, ["hiç okunmadı"]);
        var grace = SourcePriority.StaleGrace(primary);
        return nowUtc - observed.Value > grace ? (Stale, [$"son gözlem {Ago(nowUtc - observed.Value)}, sınır {Hours(grace)}"]) : (Healthy, []);
    }

    /// <summary>What makes a source unusable at all, before freshness or mapping are considered.</summary>
    static List<string> Availability(XmlSource s)
    {
        var reasons = new List<string>();
        if (!s.Enabled) reasons.Add("kaynak devre dışı");
        if (SourceCredentialHealth.ShouldFailFast(s.LastCredentialState)) reasons.Add("kimlik bilgisi engelliyor (" + SafeWord(s.LastCredentialState) + ")");
        // A source that was never checked is not a failed one; only a recorded failure (TIMEOUT, AUTH_ERROR, CLIENT_ERROR, ...) blocks.
        var health = (s.LastHealthState ?? "").Trim().ToUpperInvariant();
        if (health.Length > 0 && health is not ("HEALTHY" or "UNKNOWN" or "NEVER_CHECKED")) reasons.Add("sağlık kontrolü: " + SafeWord(health));
        return reasons;
    }

    static SourceFallbackCandidate Candidate(CatalogProduct product, XmlSource s, IReadOnlyDictionary<string, DateTime> seen, DateTime nowUtc)
    {
        var blocking = Availability(s);
        DateTime? seenUtc = seen.TryGetValue(s.Id, out var at) ? at : null;
        if (seenUtc is null) blocking.Add("ürün bu kaynakta hiç görülmedi");
        else if (nowUtc - seenUtc.Value > SourcePriority.StaleGrace(s)) blocking.Add($"fiyat/stok gözlemi bayat ({Ago(nowUtc - seenUtc.Value)}, sınır {Hours(SourcePriority.StaleGrace(s))})");
        var missing = XmlCatalog.MissingRequiredMappings(s);
        if (missing.Count > 0) blocking.Add("eşleme eksik: " + string.Join(", ", missing));
        var refreshes = new List<string>(); var locked = new List<string>();
        (product.LockPrice ? locked : refreshes).Add("fiyat");
        (product.LockStock ? locked : refreshes).Add("stok");
        if (s.UpdateName) (product.LockName ? locked : refreshes).Add("başlık");
        if (s.UpdateDescription) (product.LockDescription ? locked : refreshes).Add("açıklama");
        if (s.UpdateImages) (product.LockImages ? locked : refreshes).Add("görseller");
        if (!refreshes.Contains("fiyat") && !refreshes.Contains("stok")) blocking.Add("fiyat ve stok kilitli; yedek hiçbir alanı tazeleyemez");
        var reasons = new List<string>(blocking);
        if (blocking.Count == 0)
        {
            reasons.Insert(0, $"öncelik {s.Priority.ToString(CultureInfo.CurrentCulture)} · gözlem {Ago(nowUtc - seenUtc!.Value)} · tazeler: {string.Join(", ", refreshes)}");
            if (locked.Count > 0) reasons.Add("kilitli alanlar yedekten güncellenmez: " + string.Join(", ", locked));
        }
        return new(s.Id, SafeName(s), s.Priority, blocking.Count == 0, reasons, seenUtc, refreshes);
    }

    static string SafeName(XmlSource s) => string.IsNullOrWhiteSpace(s.Name) ? "adsız kaynak" : AuditStore.Redact(s.Name).Trim();
    static string SafeWord(string? word) { var w = AuditStore.Redact(word ?? "").Trim(); return w.Length > 40 ? w[..40] : w; }
    static string Count(int n) => n.ToString(CultureInfo.CurrentCulture);
    static string Hours(TimeSpan grace) => $"{(int)Math.Round(grace.TotalHours)} sa";

    static string Ago(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalMinutes < 1) return "az önce";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} dk önce";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} sa önce";
        return $"{(int)span.TotalDays} gün önce";
    }
}
