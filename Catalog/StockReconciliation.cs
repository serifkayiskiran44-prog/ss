using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;

namespace TrMarketplaceHubDesktop.Catalog;

/// <summary>A stock a marketplace is last known to show for one of our products on one shop: the units, when observed, where the observation came from.</summary>
public sealed record RemoteStockObservation(string Channel, string Shop, string ProductId, int Stock, DateTime ObservedUtc, string Source);

/// <summary>The last-known channel stocks, one per channel/shop/product, kept in catalog.db for whoever reads a marketplace; a newer observation replaces an older one, an older one is ignored.</summary>
public sealed class RemoteStockStore
{
    readonly string connectionString;

    public RemoteStockStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "catalog.db") }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS RemoteStocks(Channel TEXT NOT NULL, Shop TEXT NOT NULL, ProductId TEXT NOT NULL, Stock INTEGER NOT NULL, ObservedUtc TEXT NOT NULL, Source TEXT NOT NULL, PRIMARY KEY(Channel, Shop, ProductId))";
        cmd.ExecuteNonQuery();
    }

    SqliteConnection Open() => SqliteConnectionPolicy.Open(connectionString);

    public RemoteStockObservation Record(RemoteStockObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var clean = observation with { Channel = (observation.Channel ?? "").Trim().ToLowerInvariant(), Shop = (observation.Shop ?? "").Trim(), Source = AuditStore.Redact((observation.Source ?? "").Trim()), ObservedUtc = DateTime.SpecifyKind(observation.ObservedUtc.Kind == DateTimeKind.Local ? observation.ObservedUtc.ToUniversalTime() : observation.ObservedUtc, DateTimeKind.Utc) };
        if (clean.Channel.Length == 0 || clean.Shop.Length == 0 || string.IsNullOrWhiteSpace(clean.ProductId)) throw new ArgumentException("Pazaryeri, mağaza ve ürün gerekli.");
        if (clean.Stock < 0) throw new ArgumentException("Kanal stoku negatif olamaz.");
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO RemoteStocks(Channel, Shop, ProductId, Stock, ObservedUtc, Source) VALUES($c, $s, $p, $v, $t, $src) ON CONFLICT(Channel, Shop, ProductId) DO UPDATE SET Stock=excluded.Stock, ObservedUtc=excluded.ObservedUtc, Source=excluded.Source WHERE excluded.ObservedUtc >= RemoteStocks.ObservedUtc";
        cmd.Parameters.AddWithValue("$c", clean.Channel); cmd.Parameters.AddWithValue("$s", clean.Shop); cmd.Parameters.AddWithValue("$p", clean.ProductId); cmd.Parameters.AddWithValue("$v", clean.Stock); cmd.Parameters.AddWithValue("$t", clean.ObservedUtc.ToString("O", CultureInfo.InvariantCulture)); cmd.Parameters.AddWithValue("$src", clean.Source);
        cmd.ExecuteNonQuery();
        return clean;
    }

    public IReadOnlyList<RemoteStockObservation> List()
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Channel, Shop, ProductId, Stock, ObservedUtc, Source FROM RemoteStocks ORDER BY Channel, Shop, ProductId";
        using var r = cmd.ExecuteReader(); var result = new List<RemoteStockObservation>();
        while (r.Read()) result.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), DateTime.Parse(r.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime(), r.GetString(5)));
        return result;
    }
}

/// <summary>One row of the reconciliation, product × store: the latest source stock, the units held, the calculated available with its state, the last channel-observed stock with its age, the verdict, the difference, the words.</summary>
public sealed record ChannelStockReconciliationRow(string Channel, string Shop, string ProductId, string Sku, int? SourceStock, DateTime? SourceObservedUtc, string SourceWords, int Reserved, int? Available, string LocalState, string LocalWords, int? RemoteStock, DateTime? RemoteObservedUtc, string RemoteState, string Verdict, int? Difference, string Words)
{
    public const string RemoteKnown = "KNOWN", RemoteStale = "STALE", RemoteUnknown = "UNKNOWN";
    public const string Equal = "EQUAL", Mismatch = "MISMATCH", Stale = "STALE", MissingRemote = "MISSING_REMOTE", LocalBlocked = "LOCAL_BLOCKED", NoRule = "NO_RULE";
}

/// <summary>The report: every row (mismatches first), the counts by verdict, the headline. Read-only.</summary>
public sealed record StockReconciliationReport(IReadOnlyList<ChannelStockReconciliationRow> Rows, int Equal, int Mismatch, int Stale, int MissingRemote, int Blocked, string Headline)
{
    public IReadOnlyList<ChannelStockReconciliationRow> ForStore(string channel, string shop) { var c = (channel ?? "").Trim().ToLowerInvariant(); var s = (shop ?? "").Trim(); return Rows.Where(r => r.Channel == c && r.Shop == s).ToList(); }
}

/// <summary>
/// The source-versus-local stock reconciliation (#940), read-only. For every product and every store with a stock
/// policy: the latest stock the primary source reported (#935), the units held for orders (#937), the available
/// figure the real projection would give now with its state (#932, #933, #936, #937 — every guard applies; a blocked
/// projection says why, a store without a policy says so), the last stock the channel is known to show with its age
/// (fresh within a day, stale after, unknown when nobody ever observed one), and the verdict: equal, a mismatch with
/// the difference, a stale channel observation, no channel observation, or a local figure that could not be made.
/// Channel stocks come from whoever observed them — a marketplace read recorded in the remote stock store, or the
/// last stock dispatch that succeeded, whose payload is the figure the marketplace was sent. Nothing here writes to
/// a marketplace, a product, a reservation or a sync queue.
/// </summary>
public static class StockReconciliation
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);
    public const string SyncSource = "sync";

    /// <summary>The last succeeded stock dispatch per channel/shop/product as an observation: its payload is the figure the marketplace was sent.</summary>
    public static IReadOnlyList<RemoteStockObservation> FromSync(IEnumerable<SyncJob> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        var newest = new Dictionary<string, RemoteStockObservation>(StringComparer.Ordinal);
        foreach (var job in jobs.Where(j => j is not null && j.Status == SyncStatus.Succeeded && string.Equals(j.Operation, "stock", StringComparison.OrdinalIgnoreCase)))
        {
            var parts = (job.Version ?? "").Split(':');
            if (parts.Length < 3 || !int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var stock)) continue;
            var channel = job.Channel.Trim().ToLowerInvariant(); var shop = job.ShopId.Trim(); var key = channel + "|" + shop + "|" + parts[0];
            var observation = new RemoteStockObservation(channel, shop, parts[0], stock, DateTime.SpecifyKind(job.UpdatedUtc, DateTimeKind.Utc), SyncSource);
            if (!newest.TryGetValue(key, out var existing) || existing.ObservedUtc < observation.ObservedUtc) newest[key] = observation;
        }
        return newest.Values.ToList();
    }

    public static StockReconciliationReport Build(CatalogStore catalog, IReadOnlyList<CatalogProduct> products, IEnumerable<(string Channel, string Shop)> stores, IEnumerable<RemoteStockObservation> remote, DateTime nowUtc, TimeSpan? staleAfter = null)
    {
        ArgumentNullException.ThrowIfNull(catalog); ArgumentNullException.ThrowIfNull(products); ArgumentNullException.ThrowIfNull(stores); ArgumentNullException.ThrowIfNull(remote);
        var window = staleAfter ?? StaleAfter;
        var latest = new Dictionary<string, RemoteStockObservation>(StringComparer.Ordinal);
        foreach (var observation in remote)
        {
            var key = (observation.Channel ?? "").Trim().ToLowerInvariant() + "|" + (observation.Shop ?? "").Trim() + "|" + observation.ProductId;
            if (!latest.TryGetValue(key, out var existing) || existing.ObservedUtc < observation.ObservedUtc) latest[key] = observation;
        }
        var shops = stores.Select(s => (Channel: (s.Channel ?? "").Trim().ToLowerInvariant(), Shop: (s.Shop ?? "").Trim())).Where(s => s.Channel.Length > 0 && s.Shop.Length > 0).Distinct().OrderBy(s => s.Channel, StringComparer.Ordinal).ThenBy(s => s.Shop, StringComparer.Ordinal).ToList();
        var rows = new List<ChannelStockReconciliationRow>();
        foreach (var product in products)
        {
            var observations = catalog.StockObservations(product.Id);
            var primary = string.IsNullOrEmpty(product.SourceId) ? null : observations.FirstOrDefault(o => string.Equals(o.SourceId, product.SourceId, StringComparison.Ordinal));
            int? sourceStock = primary?.Stock;
            var sourceWords = primary is null || primary.Stock is null ? "kaynak stoku gözlemi yok" : $"{N(primary.Stock.Value)} ({Ago(nowUtc - primary.SeenUtc)})";
            foreach (var shop in shops)
            {
                int? available = null; var reserved = 0; string localState, localWords;
                if (catalog.GetStockPolicy(shop.Channel, shop.Shop) is null) { localState = ChannelStockReconciliationRow.NoRule; localWords = "bu mağaza için stok ayarı yok"; }
                else
                {
                    try { var projection = catalog.ProjectStock(shop.Channel, shop.Shop, product.Id, nowUtc); available = projection.Available; reserved = projection.Reserved; localState = projection.State; localWords = $"{N(projection.Available)} ({projection.State}; stok {N(projection.Stock)}, tampon {N(projection.SafetyStock)}, rezerve {N(projection.Reserved)})"; }
                    catch (InvalidOperationException ex) { localState = ChannelStockReconciliationRow.LocalBlocked; localWords = Cut(AuditStore.Redact(ex.Message)); }
                }
                latest.TryGetValue(shop.Channel + "|" + shop.Shop + "|" + product.Id, out var observed);
                var remoteState = observed is null ? ChannelStockReconciliationRow.RemoteUnknown : nowUtc - observed.ObservedUtc > window ? ChannelStockReconciliationRow.RemoteStale : ChannelStockReconciliationRow.RemoteKnown;
                var remoteWords = observed is null ? "kanal stoku bilinmiyor" : $"{N(observed.Stock)} ({Ago(nowUtc - observed.ObservedUtc)}{(remoteState == ChannelStockReconciliationRow.RemoteStale ? ", bayat" : "")}; {observed.Source})";
                string verdict, verdictWords; int? difference = null;
                if (available is null) { verdict = localState == ChannelStockReconciliationRow.NoRule ? ChannelStockReconciliationRow.NoRule : ChannelStockReconciliationRow.LocalBlocked; verdictWords = "yerel rakam yok; karşılaştırılamadı"; }
                else if (observed is null) { verdict = ChannelStockReconciliationRow.MissingRemote; verdictWords = "kanal gözlemi yok; karşılaştırılamadı"; }
                else
                {
                    difference = available.Value - observed.Stock;
                    if (remoteState == ChannelStockReconciliationRow.RemoteStale) { verdict = ChannelStockReconciliationRow.Stale; verdictWords = $"kanal gözlemi bayat; fark {Signed(difference.Value)} güvenilmez"; }
                    else if (difference.Value == 0) { verdict = ChannelStockReconciliationRow.Equal; verdictWords = "eşit"; }
                    else { verdict = ChannelStockReconciliationRow.Mismatch; verdictWords = $"uyumsuz: yerel {N(available.Value)}, kanal {N(observed.Stock)} (fark {Signed(difference.Value)})"; }
                }
                rows.Add(new(shop.Channel, shop.Shop, product.Id, (product.Sku ?? "").Trim(), sourceStock, primary?.SeenUtc, sourceWords, reserved, available, localState, localWords, observed?.Stock, observed?.ObservedUtc, remoteState, verdict, difference,
                    $"{(product.Sku ?? "").Trim()} · {shop.Channel}/{shop.Shop}: kaynak {sourceWords} · rezerve {N(reserved)} · hesaplanan {localWords} · kanal {remoteWords} · {verdictWords}"));
            }
        }
        var order = new Dictionary<string, int> { [ChannelStockReconciliationRow.Mismatch] = 0, [ChannelStockReconciliationRow.Stale] = 1, [ChannelStockReconciliationRow.LocalBlocked] = 2, [ChannelStockReconciliationRow.MissingRemote] = 3, [ChannelStockReconciliationRow.NoRule] = 4, [ChannelStockReconciliationRow.Equal] = 5 };
        rows = rows.OrderBy(r => order[r.Verdict]).ThenBy(r => r.Sku, StringComparer.Ordinal).ThenBy(r => r.Channel, StringComparer.Ordinal).ThenBy(r => r.Shop, StringComparer.Ordinal).ToList();
        int Count(string verdict) => rows.Count(r => r.Verdict == verdict);
        var equal = Count(ChannelStockReconciliationRow.Equal); var mismatch = Count(ChannelStockReconciliationRow.Mismatch); var stale = Count(ChannelStockReconciliationRow.Stale); var missing = Count(ChannelStockReconciliationRow.MissingRemote); var blocked = Count(ChannelStockReconciliationRow.LocalBlocked) + Count(ChannelStockReconciliationRow.NoRule);
        var headline = rows.Count == 0 ? "Mutabakat satırı yok (ürün ya da mağaza stok ayarı yok); salt okunur — hiçbir şey yazılmadı." : $"{N(rows.Count)} satır: {N(equal)} eşit, {N(mismatch)} uyumsuz, {N(stale)} bayat kanal gözlemi, {N(missing)} kanal gözlemi yok, {N(blocked)} yerel rakam yok; salt okunur — hiçbir şey yazılmadı.";
        return new(rows, equal, mismatch, stale, missing, blocked, headline);
    }

    static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
    static string Signed(int value) => (value > 0 ? "+" : "") + value.ToString(CultureInfo.InvariantCulture);
    static string Ago(TimeSpan age) { if (age < TimeSpan.Zero) age = TimeSpan.Zero; return age.TotalMinutes < 1 ? "az önce" : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} dk önce" : age.TotalDays < 1 ? $"{(int)age.TotalHours} sa önce" : $"{(int)age.TotalDays} gün önce"; }
    static string Cut(string text) => text.Length > 160 ? text[..160] + "…" : text;
}

public partial class CatalogStore
{
    /// <summary>#940: the reconciliation over every product and every enabled stock policy store, with the channel stocks from the remote stock store and the last succeeded stock dispatches. Read-only.</summary>
    public StockReconciliationReport ReconcileStock(DateTime nowUtc)
        => StockReconciliation.Build(this, Products(), ListStockPolicies().Where(p => p.Enabled).Select(p => (p.Channel, p.Shop)), new RemoteStockStore(dataDirectory).List().Concat(StockReconciliation.FromSync(new SyncStore(dataDirectory).List())), nowUtc);
}
