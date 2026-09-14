using System.Globalization;

namespace TrMarketplaceHubDesktop.Catalog;

/// <summary>What one source last reported for a product: its stock (null when the sighting predates the stock column) and when.</summary>
public sealed record SourceStockObservation(string SourceId, int? Stock, DateTime SeenUtc);

/// <summary>One source's part in the aggregate: its name and priority, what it reported and when, its state, whether it counted, the words.</summary>
public sealed record SourceStockContribution(string SourceId, string Name, int Priority, int? Stock, DateTime? SeenUtc, string State, bool Counted, string Words)
{
    public const string Fresh = "FRESH", Stale = "STALE", Missing = "MISSING", Duplicate = "DUPLICATE", Off = "OFF";
}

/// <summary>The aggregate: the stock, the policy that made it, its state, the source it came from under the priority policy, every contribution, the duplicate feed groups, the words.</summary>
public sealed record AggregateStockProjection(int Stock, string Policy, string State, string ChosenSourceId, IReadOnlyList<SourceStockContribution> Contributions, IReadOnlyList<string> DuplicateGroups, string Words)
{
    public const string PriorityPolicy = "priority", SumPolicy = "sum";
    public const string Aggregated = "AGGREGATED", Fallback = "FALLBACK", StaleOnly = "STALE_ONLY", NoObservation = "NO_OBSERVATION";
}

/// <summary>
/// The multi-source aggregate projection (#935). Every import writes, beside the sighting, the stock a source reported
/// for a product — the lower-priority source's too, even when the priority rules (#896) kept its value off the record.
/// The aggregate reads those observations and judges each: fresh within the source's grace (#896: three intervals,
/// at least six hours), stale beyond it, missing without a stock, off when the source is disabled or gone; two sources
/// that point at the same feed (the same host and path, credentials and query set aside, never printed) are a
/// duplicate mapping and count once — the higher priority, then the fresher. Under the priority policy the stock is
/// the best fresh source's (the primary's when it is fresh, a fallback's otherwise, said so); under the sum policy
/// the fresh distinct sources add up. Nothing fresh is STALE_ONLY, nothing observed is NO_OBSERVATION — never a number
/// pretending otherwise. Words carry source names, counts and hours only.
/// </summary>
public static class MultiSourceStock
{
    /// <summary>The feed a source points at, for the duplicate judgement only: host and path, lower-cased, without credentials or query; empty when there is no address.</summary>
    public static string FeedKey(XmlSource? source)
    {
        if (source is null || string.IsNullOrWhiteSpace(source.Location) || !Uri.TryCreate(source.Location.Trim(), UriKind.Absolute, out var uri)) return "";
        return (uri.Host + uri.AbsolutePath).ToLowerInvariant().TrimEnd('/');
    }

    public static AggregateStockProjection Aggregate(CatalogProduct product, IReadOnlyList<XmlSource> sources, IReadOnlyList<SourceStockObservation> observations, DateTime nowUtc, string? policy = null)
    {
        ArgumentNullException.ThrowIfNull(product); ArgumentNullException.ThrowIfNull(sources); ArgumentNullException.ThrowIfNull(observations);
        var mode = string.Equals(policy, AggregateStockProjection.SumPolicy, StringComparison.OrdinalIgnoreCase) ? AggregateStockProjection.SumPolicy : AggregateStockProjection.PriorityPolicy;
        var byId = sources.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var rows = new List<(SourceStockContribution Contribution, XmlSource? Source)>();
        foreach (var observation in observations)
        {
            byId.TryGetValue(observation.SourceId, out var source);
            var name = source is null ? "silinmiş kaynak" : AuditStore.Redact(source.Name ?? "").Trim();
            var priority = source?.Priority ?? SourcePriority.DefaultPriority;
            var age = nowUtc - observation.SeenUtc;
            string state, words;
            if (source is null) { state = SourceStockContribution.Off; words = $"{name}: kaynak kaydı yok"; }
            else if (!source.Enabled) { state = SourceStockContribution.Off; words = $"{name}: kaynak devre dışı"; }
            else if (observation.Stock is null) { state = SourceStockContribution.Missing; words = $"{name}: stok bildirilmedi"; }
            else if (age > SourcePriority.StaleGrace(source)) { state = SourceStockContribution.Stale; words = $"{name}: {N(observation.Stock.Value)} adet, bayat ({Ago(age)}, sınır {Hours(SourcePriority.StaleGrace(source))})"; }
            else { state = SourceStockContribution.Fresh; words = $"{name}: {N(observation.Stock.Value)} adet, {Ago(age)}"; }
            rows.Add((new(observation.SourceId, name, priority, observation.Stock, observation.SeenUtc, state, false, words), source));
        }
        // Duplicate mappings: fresh contributions on the same feed count once -- the higher priority, then the fresher.
        var duplicates = new List<string>();
        var contributions = rows.Select(r => r.Contribution).ToList();
        foreach (var group in rows.Where(r => r.Contribution.State == SourceStockContribution.Fresh && FeedKey(r.Source).Length > 0).GroupBy(r => FeedKey(r.Source), StringComparer.Ordinal).Where(g => g.Count() > 1))
        {
            var ordered = group.OrderByDescending(r => r.Contribution.Priority).ThenByDescending(r => r.Contribution.SeenUtc).ToList(); // #896: the higher number is the higher priority
            duplicates.Add($"aynı besleme: {string.Join(" ve ", ordered.Select(r => r.Contribution.Name))}; {ordered[0].Contribution.Name} sayıldı");
            foreach (var extra in ordered.Skip(1))
            {
                var index = contributions.FindIndex(c => c.SourceId == extra.Contribution.SourceId);
                contributions[index] = extra.Contribution with { State = SourceStockContribution.Duplicate, Words = extra.Contribution.Words + " · mükerrer eşleme, sayılmadı" };
            }
        }
        var counted = contributions.Where(c => c.State == SourceStockContribution.Fresh).OrderByDescending(c => c.Priority).ThenByDescending(c => c.SeenUtc).ToList();
        for (var i = 0; i < contributions.Count; i++) if (counted.Any(c => c.SourceId == contributions[i].SourceId)) contributions[i] = contributions[i] with { Counted = true };
        var notes = string.Join("; ", contributions.Where(c => !c.Counted).Select(c => c.Words).Concat(duplicates));
        if (counted.Count == 0)
        {
            var state = contributions.Count == 0 ? AggregateStockProjection.NoObservation : AggregateStockProjection.StaleOnly;
            var words = state == AggregateStockProjection.NoObservation ? "hiçbir kaynak bu ürünün stokunu bildirmedi; birleştirilmiş stok yok" : "hiçbir kaynağın stok gözlemi taze değil; birleştirilmiş stok yok (" + notes + ")";
            return new(0, mode, state, "", contributions, duplicates, words);
        }
        if (mode == AggregateStockProjection.SumPolicy)
        {
            var total = counted.Sum(c => c.Stock!.Value);
            return new(total, mode, AggregateStockProjection.Aggregated, "", contributions, duplicates, $"birleştirilmiş stok {N(total)} (toplam: {string.Join(" + ", counted.Select(c => $"{c.Name} {N(c.Stock!.Value)}"))})" + (notes.Length > 0 ? "; " + notes : ""));
        }
        var best = counted[0];
        var primaryCounted = string.Equals(best.SourceId, product.SourceId, StringComparison.Ordinal);
        var state2 = primaryCounted || string.IsNullOrWhiteSpace(product.SourceId) ? AggregateStockProjection.Aggregated : AggregateStockProjection.Fallback;
        var primaryWords = state2 == AggregateStockProjection.Fallback ? $"; birincil kaynak sayılmadı ({contributions.FirstOrDefault(c => c.SourceId == product.SourceId)?.Words ?? "gözlem yok"})" : "";
        return new(best.Stock!.Value, mode, state2, best.SourceId, contributions, duplicates,
            $"stok {N(best.Stock.Value)} · {(state2 == AggregateStockProjection.Fallback ? "yedek kaynak" : "öncelikli kaynak")} {best.Name} (öncelik {N(best.Priority)}, {Ago(nowUtc - best.SeenUtc!.Value)})" + primaryWords + (notes.Length > 0 ? "; " + notes : ""));
    }

    static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
    static string Hours(TimeSpan span) => span.TotalHours.ToString("0.#", CultureInfo.InvariantCulture) + " sa";
    static string Ago(TimeSpan age) { if (age < TimeSpan.Zero) age = TimeSpan.Zero; return age.TotalMinutes < 1 ? "az önce" : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} dk önce" : age.TotalDays < 1 ? $"{(int)age.TotalHours} sa önce" : $"{(int)age.TotalDays} gün önce"; }
}

public partial class CatalogStore
{
    /// <summary>#935: the aggregate of every source's last reported stock for a product, under the priority policy by default.</summary>
    public AggregateStockProjection AggregateStock(string productId, DateTime nowUtc, string? policy = null)
    {
        var product = FindProduct(productId) ?? throw new InvalidOperationException("Ürün bulunamadı.");
        return MultiSourceStock.Aggregate(product, Sources(), StockObservations(productId), nowUtc, policy);
    }
}
