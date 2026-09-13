using System.Globalization;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

/// <summary>Where a product's cost came from: the state, the value and its currency, the origin's kind, source, revision, run and moment; whether the operator overrode a feed's value and what that value was (its source, revision and moment); a short label and the words.</summary>
public sealed record CostProvenanceView(string State, decimal Cost, string Currency, string Kind, string SourceId, string SourceName, int SourceRevision, string RunId, DateTime? ObservedUtc, bool ManualOverride, decimal? SupersededCost, string SupersededSourceName, int SupersededRevision, DateTime? SupersededObservedUtc, string OriginLabel, string Words)
{
    public const string Missing = "MISSING", Unrecorded = "UNRECORDED", Feed = "FEED", Manual = "MANUAL", Override = "OVERRIDE";
    /// <summary>The operator owns the value: a manual entry or an override; a feed's or an unrecorded value is not theirs.</summary>
    public bool OperatorOwned => State is Manual or Override;
}

/// <summary>
/// Supplier cost provenance (#922). The cost a margin is computed from is the one product field whose origin
/// decides whether the margin can be trusted, so it is read as one view: the value and its currency (the
/// product's cost currency, the source's at import), the source that wrote it with its configuration revision,
/// the import run and the moment observed (#895), or the operator's entry — and, when the operator wrote over a
/// feed's value, that value with its source, revision and moment (the origin a manual edit replaced rides along,
/// through further manual edits, so the override never forgets what it overrides). A cost nobody entered is
/// missing, said as such: no margin is computed against it — not on the card, not by the simulator, not by the
/// pricing chain. A value with no recorded origin (before #895) says so instead of guessing. Names come from the
/// source by id, redacted; a source that is gone is said to be gone; no address, no credential, no free text.
/// </summary>
public static class CostProvenance
{
    public const string Field = "Cost";

    public static CostProvenanceView Resolve(CatalogProduct product, Func<string, XmlSource?>? sourceById, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(product);
        var currency = (product.CostCurrency ?? "").Trim().ToUpperInvariant();
        var origin = FieldProvenance.Of(product, Field);
        var feedBacked = !string.Equals(product.SourceKind, FieldProvenance.ManualKind, StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(product.SourceId);
        if (product.Cost <= 0)
            return new(CostProvenanceView.Missing, product.Cost, currency, origin?.Kind ?? "", "", "", 0, "", origin?.ObservedUtc, false, null, "", 0, null, "Girilmemiş", "maliyet yok; net kâr hesaplanamaz");
        var money = Money(product.Cost, currency);
        if (origin is null)
            return new(CostProvenanceView.Unrecorded, product.Cost, currency, "", "", "", 0, "", null, false, null, "", 0, null, feedBacked ? "Kaynak" : "Elle", $"{money} · kökeni kaydedilmedi");
        if (!string.Equals(origin.Kind, FieldProvenance.ManualKind, StringComparison.OrdinalIgnoreCase))
        {
            var who = Who(origin.SourceId, sourceById);
            return new(CostProvenanceView.Feed, product.Cost, currency, origin.Kind, origin.SourceId, who, origin.SourceRevision, origin.RunId, origin.ObservedUtc, false, null, "", 0, null, who,
                $"{money} · {who}{Revision(origin.SourceRevision)}{Run(origin.RunId)} · {FieldProvenance.Ago(nowUtc - origin.ObservedUtc)}");
        }
        var rewrite = feedBacked ? " · içe aktarma bu alanı yeniden yazabilir" : "";
        var superseded = origin.Superseded is { } s && !string.Equals(s.Kind, FieldProvenance.ManualKind, StringComparison.OrdinalIgnoreCase) ? s : null;
        if (superseded is null)
            return new(CostProvenanceView.Manual, product.Cost, currency, origin.Kind, "", "", 0, "", origin.ObservedUtc, false, null, "", 0, null, "Elle", $"{money} · elle · {FieldProvenance.Ago(nowUtc - origin.ObservedUtc)}{rewrite}");
        var supersededCost = decimal.TryParse(origin.SupersededValue, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : (decimal?)null;
        var supersededWho = Who(superseded.SourceId, sourceById);
        var last = supersededCost is { } c ? Money(c, currency) : "bilinmiyor";
        return new(CostProvenanceView.Override, product.Cost, currency, origin.Kind, "", "", 0, "", origin.ObservedUtc, true, supersededCost, supersededWho, superseded.SourceRevision, superseded.ObservedUtc, "Elle (kaynağın üstüne)",
            $"{money} · elle · {FieldProvenance.Ago(nowUtc - origin.ObservedUtc)} · kaynağın son değeri {last} ({supersededWho}{Revision(superseded.SourceRevision)}, {FieldProvenance.Ago(nowUtc - superseded.ObservedUtc)}){rewrite}");
    }

    public static string Describe(CatalogProduct product, Func<string, XmlSource?>? sourceById, DateTime nowUtc) => Resolve(product, sourceById, nowUtc).Words;

    static string Money(decimal value, string currency) => value.ToString("0.##", CultureInfo.CurrentCulture) + (currency.Length > 0 ? " " + currency : "");
    static string Revision(int revision) => revision > 0 ? $" · rev. {revision.ToString(CultureInfo.CurrentCulture)}" : "";
    static string Run(string runId) => runId.Length > 0 ? $" · çalıştırma {runId[..Math.Min(8, runId.Length)]}" : "";

    /// <summary>The source's redacted name; "kaynak silinmiş" when the resolver knows it no more; the id when there is no resolver to ask; "kaynak bilinmiyor" without an id.</summary>
    static string Who(string sourceId, Func<string, XmlSource?>? sourceById)
    {
        var id = (sourceId ?? "").Trim();
        if (id.Length == 0) return "kaynak bilinmiyor";
        if (sourceById is null) return "kaynak " + id[..Math.Min(8, id.Length)];
        var source = sourceById(id);
        return source is null ? "kaynak silinmiş" : AuditStore.Redact(source.Name ?? "").Trim();
    }
}
