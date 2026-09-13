using System.Globalization;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public enum FreshnessState { Fresh, Stale, Unknown, Frozen }

/// <summary>One field's freshness: when it was last observed, by which source, against which threshold, and the words.</summary>
public sealed record FieldFreshness(string Field, string Label, FreshnessState State, DateTime? ObservedUtc, string SourceId, TimeSpan Threshold, string Words);

public sealed record ProductFreshnessView(IReadOnlyList<FieldFreshness> Fields, int Stale, int Unknown, int Frozen, string Headline)
{
    public bool AnyStale => Stale + Frozen > 0;
}

/// <summary>
/// Field-level freshness (#903). Each tracked field — price, stock, and the content fields (title, description,
/// images) — reads its own last observation from the #895 origin and its own threshold from the source that
/// observed it (the #896 grace: three of the source's intervals, at least six hours). A field is fresh within the
/// threshold, stale past it, unknown when no observation was ever recorded (a product from before provenance),
/// and frozen when its source is off or gone — it cannot be refreshed, which is worse than stale. An operator's
/// value has no refresh promise and never goes stale. The same rule runs in the catalogue search as SQL, so the
/// product list can be filtered by it; every moment is UTC.
/// </summary>
public static class ProductFreshness
{
    public const string Any = "any", Content = "Content";
    public const string Stale = "stale", Fresh = "fresh", Unknown = "unknown";
    public static readonly IReadOnlyList<(string Field, string Label)> Fields = new[] { ("Price", "fiyat"), ("Stock", "stok"), ("Name", "başlık"), ("Description", "açıklama"), ("ImageUrls", "görseller") };
    public static readonly IReadOnlyList<(string Key, string Label)> FilterChoices = new[]
    {
        ("", "Tümü"), ("stale:any", "Bayat (herhangi bir alan)"), ("stale:Price", "Bayat fiyat"), ("stale:Stock", "Bayat stok"), ("stale:Content", "Bayat içerik"), ("fresh:any", "Taze (bütün alanlar)"), ("unknown:any", "Zaman damgası yok"),
    };
    /// <summary>The cutoff text for a source that cannot refresh (off or gone): every recorded observation lies behind it.</summary>
    public const string FrozenCutoff = "9999-12-31T00:00:00";

    public static TimeSpan Threshold(XmlSource? source) => SourcePriority.StaleGrace(source);

    public static ProductFreshnessView Evaluate(CatalogProduct product, Func<string, XmlSource?> sourceById, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(product); ArgumentNullException.ThrowIfNull(sourceById);
        var fields = new List<FieldFreshness>();
        foreach (var (field, label) in Fields)
        {
            var origin = FieldProvenance.Of(product, field);
            if (origin is null) { fields.Add(new(field, label, FreshnessState.Unknown, null, "", Threshold(null), "zaman damgası yok")); continue; }
            var age = nowUtc - origin.ObservedUtc;
            if (string.Equals(origin.Kind, FieldProvenance.ManualKind, StringComparison.OrdinalIgnoreCase)) { fields.Add(new(field, label, FreshnessState.Fresh, origin.ObservedUtc, "", TimeSpan.Zero, $"elle · {Ago(age)}")); continue; }
            var source = origin.SourceId.Length > 0 ? sourceById(origin.SourceId) : null;
            var threshold = Threshold(source);
            if (source is null) { fields.Add(new(field, label, FreshnessState.Frozen, origin.ObservedUtc, origin.SourceId, threshold, $"kaynak silinmiş · tazelenmez · son gözlem {Ago(age)}")); continue; }
            if (!source.Enabled) { fields.Add(new(field, label, FreshnessState.Frozen, origin.ObservedUtc, origin.SourceId, threshold, $"kaynak devre dışı · tazelenmez · son gözlem {Ago(age)}")); continue; }
            var stale = age > threshold;
            fields.Add(new(field, label, stale ? FreshnessState.Stale : FreshnessState.Fresh, origin.ObservedUtc, origin.SourceId, threshold, $"{(stale ? "bayat" : "taze")} · {Ago(age)} (eşik {Hours(threshold)})"));
        }
        var staleCount = fields.Count(f => f.State == FreshnessState.Stale); var unknown = fields.Count(f => f.State == FreshnessState.Unknown); var frozen = fields.Count(f => f.State == FreshnessState.Frozen);
        var parts = new List<string>();
        if (staleCount > 0) parts.Add($"{N(staleCount)} alan bayat");
        if (frozen > 0) parts.Add($"{N(frozen)} alan tazelenmez");
        if (unknown > 0) parts.Add($"{N(unknown)} alan zaman damgasız");
        return new(fields, staleCount, unknown, frozen, parts.Count == 0 ? "bütün alanlar taze" : string.Join(" · ", parts));
    }

    /// <summary>The catalogue search's predicate: each field's recorded observation (ISO text) against its own source's cutoff — now minus that source's threshold, or the frozen mark for a source that is off or gone. Parameters are added to the caller's map.</summary>
    public static string FilterSql(string freshness, string field, IReadOnlyList<XmlSource> sources, DateTime nowUtc, IDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(sources); ArgumentNullException.ThrowIfNull(parameters);
        var wanted = field switch { "Price" => new[] { "Price" }, "Stock" => new[] { "Stock" }, Content => new[] { "Name", "Description", "ImageUrls" }, _ => Fields.Select(f => f.Field).ToArray() };
        var cases = new List<string>();
        foreach (var s in sources)
        {
            var idKey = "$fz" + parameters.Count.ToString(CultureInfo.InvariantCulture); parameters[idKey] = s.Id;
            var cutKey = "$fz" + parameters.Count.ToString(CultureInfo.InvariantCulture); parameters[cutKey] = s.Enabled ? (nowUtc - Threshold(s)).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) : FrozenCutoff;
            cases.Add($"WHEN {idKey} THEN {cutKey}");
        }
        var elseKey = "$fz" + parameters.Count.ToString(CultureInfo.InvariantCulture); parameters[elseKey] = FrozenCutoff;
        string Obs(string f) => $"json_extract(Json,'$.FieldOrigins.{f}.ObservedUtc')";
        string Kind(string f) => $"json_extract(Json,'$.FieldOrigins.{f}.Kind')";
        string Cutoff(string f) => cases.Count == 0 ? elseKey : $"(CASE json_extract(Json,'$.FieldOrigins.{f}.SourceId') {string.Join(" ", cases)} ELSE {elseKey} END)";
        string StaleOf(string f) => $"({Obs(f)} IS NOT NULL AND {Kind(f)}='xml' AND {Obs(f)} < {Cutoff(f)})";
        string FreshOf(string f) => $"({Obs(f)} IS NOT NULL AND ({Kind(f)}<>'xml' OR {Obs(f)} >= {Cutoff(f)}))";
        string UnknownOf(string f) => $"({Obs(f)} IS NULL)";
        return freshness switch
        {
            Stale => string.Join(" OR ", wanted.Select(StaleOf)),
            Fresh => string.Join(" AND ", wanted.Select(FreshOf)),
            Unknown => string.Join(" AND ", wanted.Select(UnknownOf)),
            _ => "1=1",
        };
    }

    static string N(int value) => value.ToString(CultureInfo.CurrentCulture);
    static string Hours(TimeSpan t) => $"{(int)Math.Round(t.TotalHours)} sa";

    static string Ago(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalMinutes < 1) return "az önce";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} dk önce";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} sa önce";
        return $"{(int)span.TotalDays} gün önce";
    }
}
