using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;

namespace TrMarketplaceHubDesktop.Catalog;

/// <summary>The operator's consent that one named source's stock may stand in for a product's primary, bound to that source's configuration revision at the time.</summary>
public sealed record FallbackStockApproval(string ProductId, string SourceId, int SourceRevision, DateTime ApprovedUtc, string Note);

/// <summary>One source judged as a stock fallback: its name, priority and configuration revision, whether it may stand in, the stock it last reported and when, the reasons.</summary>
public sealed record FallbackStockCandidate(string SourceId, string Name, int Priority, int Revision, bool Eligible, int? Stock, DateTime? SeenUtc, IReadOnlyList<string> Reasons);

/// <summary>The selection for one product: the status, the primary and the record's stock words, the selected candidate when one stands out, the approval on record, every candidate, the words.</summary>
public sealed record FallbackStockSelection(string Status, string ProductId, string PrimarySourceId, string PrimaryWords, FallbackStockCandidate? Selected, FallbackStockApproval? Approval, IReadOnlyList<FallbackStockCandidate> Candidates, string Words)
{
    public const string PrimaryOk = "PRIMARY_OK", Applied = "APPLIED", NeedsApproval = "NEEDS_APPROVAL", RevisionChanged = "REVISION_CHANGED", EqualCandidates = "EQUAL_CANDIDATES", NoFallback = "NO_FALLBACK", NoPrimary = "NO_PRIMARY";
    /// <summary>Only an approved selection at the fallback's current revision puts the fallback's stock into the projection.</summary>
    public bool StandsIn => Status == Applied && Selected is not null;
}

/// <summary>
/// Supplier fallback stock selection (#936). When the record's stock is no longer fresh (#932: stale, unobserved or
/// frozen), the sources that could stand in are judged twice: as a source by the #897 graph (enabled, credentials
/// and health, the product seen within the source's grace, the mapping complete, the stock not locked) and as a
/// stock by the #935 observations (a stock reported, fresh within the grace, not the primary's own feed behind other
/// credentials, one source per feed). The higher priority stands out; equal priorities are the operator's choice.
/// Nothing stands in by itself: the operator approves one source for one product, and the approval is bound to
/// that source's configuration revision — when the source is edited the approval lapses until renewed; when the
/// primary recovers, its own stock is back and the approval waits. Every verdict says which source, which revision,
/// what it waits for. Names, priorities, revisions, counts and hours only — never an address, never a credential.
/// </summary>
public static class FallbackStock
{
    public static FallbackStockSelection Select(CatalogProduct product, IReadOnlyList<XmlSource> sources, IReadOnlyDictionary<string, DateTime> seenBySource, IReadOnlyList<SourceStockObservation> observations, FallbackStockApproval? approval, FieldFreshness recordStock, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(product); ArgumentNullException.ThrowIfNull(sources); ArgumentNullException.ThrowIfNull(seenBySource); ArgumentNullException.ThrowIfNull(observations); ArgumentNullException.ThrowIfNull(recordStock);
        var primaryId = product.SourceId ?? "";
        if (primaryId.Length == 0) return new(FallbackStockSelection.NoPrimary, product.Id, "", recordStock.Words, null, approval, [], "elle oluşturulmuş ürün; birincil kaynak yok, yedek seçimi yapılmaz");
        var primaryFeed = MultiSourceStock.FeedKey(sources.FirstOrDefault(s => string.Equals(s.Id, primaryId, StringComparison.Ordinal)));
        var graph = LinkedSourceGraph.Evaluate(product, sources, seenBySource, nowUtc);

        // Every other source: the #897 judgement first, then the stock itself.
        var candidates = new List<FallbackStockCandidate>();
        foreach (var c in graph.Candidates)
        {
            var source = sources.First(s => string.Equals(s.Id, c.SourceId, StringComparison.Ordinal));
            var reasons = new List<string>(c.Reasons); var eligible = c.Eligible;
            var observation = observations.FirstOrDefault(o => string.Equals(o.SourceId, c.SourceId, StringComparison.Ordinal));
            if (eligible && !c.Refreshes.Contains("stok")) { eligible = false; reasons.Add("stok kilitli; yedek stoku yazamaz"); }
            if (eligible && (observation is null || observation.Stock is null)) { eligible = false; reasons.Add("bu kaynak ürünün stokunu bildirmedi"); }
            else if (eligible && nowUtc - observation!.SeenUtc > SourcePriority.StaleGrace(source)) { eligible = false; reasons.Add($"stok gözlemi bayat ({Ago(nowUtc - observation.SeenUtc)}, sınır {Hours(SourcePriority.StaleGrace(source))})"); }
            if (eligible && primaryFeed.Length > 0 && MultiSourceStock.FeedKey(source) == primaryFeed) { eligible = false; reasons.Add("birincil kaynakla aynı besleme; yedek sayılmaz"); }
            candidates.Add(new(c.SourceId, c.Name, c.Priority, source.ConfigRevision, eligible, observation?.Stock, observation?.SeenUtc, reasons));
        }
        // One source per feed: among eligible duplicates the higher priority, then the fresher, represents the feed.
        foreach (var group in candidates.Where(c => c.Eligible).GroupBy(c => MultiSourceStock.FeedKey(sources.First(s => s.Id == c.SourceId)), StringComparer.Ordinal).Where(g => g.Key.Length > 0 && g.Count() > 1))
            foreach (var extra in group.OrderByDescending(c => c.Priority).ThenByDescending(c => c.SeenUtc).Skip(1))
            {
                var i = candidates.IndexOf(extra);
                candidates[i] = extra with { Eligible = false, Reasons = extra.Reasons.Append("aynı beslemeyi başka bir aday temsil ediyor; sayılmadı").ToList() };
            }
        candidates = candidates.OrderByDescending(c => c.Eligible).ThenByDescending(c => c.Priority).ThenByDescending(c => c.SeenUtc ?? DateTime.MinValue).ThenBy(c => c.Name, StringComparer.Ordinal).ToList();
        var eligibleOnes = candidates.Where(c => c.Eligible).ToList();
        string NameOf(string sourceId) => candidates.FirstOrDefault(c => c.SourceId == sourceId)?.Name ?? (sourceId == primaryId ? "birincil kaynak" : "silinmiş kaynak");
        var approvalWords = approval is null ? "" : $"onay: {NameOf(approval.SourceId)} rev {N(approval.SourceRevision)} ({Stamp(approval.ApprovedUtc)})";

        if (recordStock.State == FreshnessState.Fresh)
            return new(FallbackStockSelection.PrimaryOk, product.Id, primaryId, recordStock.Words, null, approval, candidates, $"kayıtlı stok taze ({recordStock.Words}); yedek kullanılmaz" + (approval is null ? "" : $" ({approvalWords} beklemede)"));
        var trouble = $"kayıtlı stok {recordStock.State switch { FreshnessState.Stale => "bayat", FreshnessState.Frozen => "tazelenmez", _ => "gözlemsiz" }} ({recordStock.Words})";
        if (eligibleOnes.Count == 0)
            return new(FallbackStockSelection.NoFallback, product.Id, primaryId, recordStock.Words, null, approval, candidates,
                $"{trouble}; uygun yedek yok" + (candidates.Count > 0 ? " (" + string.Join("; ", candidates.Select(c => c.Name + ": " + string.Join(", ", c.Reasons))) + ")" : "") + (approval is null ? "" : $"; {approvalWords} kullanılamaz") + "; otomatik stok yazımı yapılmaz");

        FallbackStockCandidate selected;
        if (eligibleOnes.Count > 1 && eligibleOnes[0].Priority == eligibleOnes[1].Priority)
        {
            var tied = eligibleOnes.Where(c => c.Priority == eligibleOnes[0].Priority).ToList();
            var chosen = approval is null ? null : tied.FirstOrDefault(c => string.Equals(c.SourceId, approval.SourceId, StringComparison.Ordinal));
            if (chosen is null)
                return new(FallbackStockSelection.EqualCandidates, product.Id, primaryId, recordStock.Words, null, approval, candidates,
                    $"{trouble}; {N(tied.Count)} eşit öncelikli yedek ({string.Join(", ", tied.Select(c => c.Name))}) — seçim operatörün: birini onaylayın; geçiş otomatik değildir; otomatik stok yazımı yapılmaz");
            selected = chosen;
        }
        else selected = eligibleOnes[0];

        var standing = $"{selected.Name} rev {N(selected.Revision)} (öncelik {N(selected.Priority)}, stok gözlemi {Ago(nowUtc - selected.SeenUtc!.Value)})";
        if (approval is null || !string.Equals(approval.SourceId, selected.SourceId, StringComparison.Ordinal))
            return new(FallbackStockSelection.NeedsApproval, product.Id, primaryId, recordStock.Words, selected, approval, candidates,
                $"{trouble}; yedek uygun: {standing} — onay gerekiyor; otomatik stok yazımı yapılmaz" + (approval is null ? "" : $"; {approvalWords} başka kaynak için"));
        if (approval.SourceRevision != selected.Revision)
            return new(FallbackStockSelection.RevisionChanged, product.Id, primaryId, recordStock.Words, selected, approval, candidates,
                $"{trouble}; yedek {selected.Name} onayı rev {N(approval.SourceRevision)} için verildi, kaynak şimdi rev {N(selected.Revision)} — yeniden onaylayın; otomatik stok yazımı yapılmaz");
        return new(FallbackStockSelection.Applied, product.Id, primaryId, recordStock.Words, selected, approval, candidates,
            $"{trouble}; yedek kaynaktan: {standing}, onay {Stamp(approval.ApprovedUtc)} — stok {N(selected.Stock!.Value)}");
    }

    static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
    static string Hours(TimeSpan span) => span.TotalHours.ToString("0.#", CultureInfo.InvariantCulture) + " sa";
    static string Stamp(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
    static string Ago(TimeSpan age) { if (age < TimeSpan.Zero) age = TimeSpan.Zero; return age.TotalMinutes < 1 ? "az önce" : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} dk önce" : age.TotalDays < 1 ? $"{(int)age.TotalHours} sa önce" : $"{(int)age.TotalDays} gün önce"; }
}

/// <summary>The approvals: one per product, replaced on re-approval, removed on revocation; in catalog.db beside the products.</summary>
public sealed class FallbackStockApprovalStore
{
    public const int NoteLimit = 200;
    readonly string connectionString;

    public FallbackStockApprovalStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "catalog.db") }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS FallbackStockApprovals(ProductId TEXT PRIMARY KEY, SourceId TEXT NOT NULL, SourceRevision INTEGER NOT NULL, ApprovedUtc TEXT NOT NULL, Note TEXT NOT NULL DEFAULT '')";
        cmd.ExecuteNonQuery();
    }

    SqliteConnection Open() => SqliteConnectionPolicy.Open(connectionString);

    public FallbackStockApproval Approve(string productId, string sourceId, int sourceRevision, string? note, DateTime nowUtc)
    {
        var cleanProduct = (productId ?? "").Trim(); var cleanSource = (sourceId ?? "").Trim();
        if (cleanProduct.Length == 0) throw new ArgumentException("Ürün kimliği gerekli.");
        if (cleanSource.Length == 0) throw new ArgumentException("Yedek kaynak kimliği gerekli.");
        var cleanNote = AuditStore.Redact((note ?? "").Trim()); if (cleanNote.Length > NoteLimit) cleanNote = cleanNote[..NoteLimit];
        var at = DateTime.SpecifyKind(nowUtc.Kind == DateTimeKind.Local ? nowUtc.ToUniversalTime() : nowUtc, DateTimeKind.Utc);
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO FallbackStockApprovals(ProductId, SourceId, SourceRevision, ApprovedUtc, Note) VALUES($p, $s, $r, $t, $n) ON CONFLICT(ProductId) DO UPDATE SET SourceId=excluded.SourceId, SourceRevision=excluded.SourceRevision, ApprovedUtc=excluded.ApprovedUtc, Note=excluded.Note";
        cmd.Parameters.AddWithValue("$p", cleanProduct); cmd.Parameters.AddWithValue("$s", cleanSource); cmd.Parameters.AddWithValue("$r", sourceRevision); cmd.Parameters.AddWithValue("$t", at.ToString("O", CultureInfo.InvariantCulture)); cmd.Parameters.AddWithValue("$n", cleanNote);
        cmd.ExecuteNonQuery();
        return new(cleanProduct, cleanSource, sourceRevision, at, cleanNote);
    }

    public bool Revoke(string productId)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM FallbackStockApprovals WHERE ProductId=$p"; cmd.Parameters.AddWithValue("$p", (productId ?? "").Trim());
        return cmd.ExecuteNonQuery() > 0;
    }

    public FallbackStockApproval? Get(string productId) => List().FirstOrDefault(a => string.Equals(a.ProductId, (productId ?? "").Trim(), StringComparison.Ordinal));

    public IReadOnlyList<FallbackStockApproval> List()
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT ProductId, SourceId, SourceRevision, ApprovedUtc, Note FROM FallbackStockApprovals ORDER BY ApprovedUtc DESC";
        using var r = cmd.ExecuteReader(); var result = new List<FallbackStockApproval>();
        while (r.Read()) result.Add(new(r.GetString(0), r.GetString(1), r.GetInt32(2), DateTime.Parse(r.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime(), r.GetString(4)));
        return result;
    }
}

public partial class CatalogStore
{
    /// <summary>#936: which source would stand in for the product's stock and what that waits for, from persisted facts only.</summary>
    public FallbackStockSelection SelectFallbackStock(string productId, DateTime nowUtc)
    {
        var product = FindProduct(productId) ?? throw new InvalidOperationException("Ürün bulunamadı.");
        var sources = Sources();
        var recordStock = ProductFreshness.Evaluate(product, id => sources.FirstOrDefault(s => s.Id == id), nowUtc).Fields.Single(f => f.Field == "Stock");
        return FallbackStock.Select(product, sources, Sightings(productId), StockObservations(productId), new FallbackStockApprovalStore(dataDirectory).Get(productId), recordStock, nowUtc);
    }

    /// <summary>#936: the operator approves the named source as the product's stock fallback at that source's current revision; the source must exist and must not be the primary. Audited by name and revision.</summary>
    public FallbackStockApproval ApproveFallbackStock(string productId, string sourceId, string? note, DateTime nowUtc)
    {
        var product = FindProduct(productId) ?? throw new InvalidOperationException("Ürün bulunamadı.");
        var source = SourceById((sourceId ?? "").Trim()) ?? throw new InvalidOperationException("Yedek kaynak bulunamadı.");
        if (string.Equals(source.Id, product.SourceId, StringComparison.Ordinal)) throw new InvalidOperationException("Birincil kaynak kendi yedeği olamaz.");
        var approval = new FallbackStockApprovalStore(dataDirectory).Approve(product.Id, source.Id, source.ConfigRevision, note, nowUtc);
        try { new AuditStore(dataDirectory).Append(new AuditEvent { Module = "stock", Action = "fallback-approve", ProductId = product.Id, Outcome = "Info", Detail = AuditStore.Redact($"yedek stok onayı: {source.Name} rev {approval.SourceRevision.ToString(CultureInfo.InvariantCulture)}") }); } catch (Exception) { }
        return approval;
    }

    /// <summary>#936: removes the product's fallback approval; true when there was one. Audited.</summary>
    public bool RevokeFallbackStock(string productId, DateTime nowUtc)
    {
        var removed = new FallbackStockApprovalStore(dataDirectory).Revoke(productId);
        if (removed) { try { new AuditStore(dataDirectory).Append(new AuditEvent { Module = "stock", Action = "fallback-revoke", ProductId = (productId ?? "").Trim(), Outcome = "Info", Detail = "yedek stok onayı kaldırıldı" }); } catch (Exception) { } }
        return removed;
    }
}
