using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;

namespace TrMarketplaceHubDesktop.Catalog;

/// <summary>A price a marketplace is last known to hold for one of our products on one shop: the value, its currency, when it was observed and where the observation came from.</summary>
public sealed record RemotePriceObservation(string Channel, string Shop, string ProductId, decimal Price, string Currency, DateTime ObservedUtc, string Source);

/// <summary>One cell of the comparison: the local price the chain would produce now (or why it would not), the last-known remote price with its freshness, and how they compare.</summary>
public sealed record ChannelPriceCell(string Channel, string Shop, string ProductId, string Sku, decimal? LocalPrice, string LocalCurrency, string LocalState, string LocalWords, decimal? RemotePrice, string RemoteCurrency, DateTime? RemoteObservedUtc, string RemoteState, string Comparison, decimal? DifferencePercent, string Words)
{
    public const string LocalOk = "OK", LocalBlocked = "BLOCKED", NoRule = "NO_RULE";
    public const string RemoteKnown = "KNOWN", RemoteStale = "STALE", RemoteUnknown = "UNKNOWN";
    public const string Same = "SAME", Higher = "HIGHER", Lower = "LOWER", CurrencyMismatch = "CURRENCY_MISMATCH", Unknown = "UNKNOWN";
}

/// <summary>The last-known remote prices, one per channel/shop/product, kept on catalog.db for whoever reads a marketplace; a newer observation replaces an older one, an older one is ignored.</summary>
public sealed class RemotePriceStore
{
    readonly string connectionString;

    public RemotePriceStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "catalog.db") }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS RemotePrices(Channel TEXT NOT NULL, Shop TEXT NOT NULL, ProductId TEXT NOT NULL, Price TEXT NOT NULL, Currency TEXT NOT NULL, ObservedUtc TEXT NOT NULL, Source TEXT NOT NULL, PRIMARY KEY(Channel, Shop, ProductId))";
        cmd.ExecuteNonQuery();
    }

    SqliteConnection Open() => SqliteConnectionPolicy.Open(connectionString);

    public RemotePriceObservation Record(RemotePriceObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var clean = observation with { Channel = observation.Channel.Trim().ToLowerInvariant(), Shop = observation.Shop.Trim(), Currency = (observation.Currency ?? "").Trim().ToUpperInvariant(), Source = (observation.Source ?? "").Trim() };
        if (clean.Channel.Length == 0 || clean.Shop.Length == 0 || string.IsNullOrWhiteSpace(clean.ProductId)) throw new ArgumentException("Pazaryeri, mağaza ve ürün gerekli.");
        if (clean.Price < 0) throw new ArgumentException("Uzak fiyat negatif olamaz.");
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO RemotePrices(Channel, Shop, ProductId, Price, Currency, ObservedUtc, Source) VALUES($c, $s, $p, $v, $cur, $t, $src) ON CONFLICT(Channel, Shop, ProductId) DO UPDATE SET Price=excluded.Price, Currency=excluded.Currency, ObservedUtc=excluded.ObservedUtc, Source=excluded.Source WHERE excluded.ObservedUtc >= RemotePrices.ObservedUtc";
        cmd.Parameters.AddWithValue("$c", clean.Channel); cmd.Parameters.AddWithValue("$s", clean.Shop); cmd.Parameters.AddWithValue("$p", clean.ProductId); cmd.Parameters.AddWithValue("$v", clean.Price.ToString(CultureInfo.InvariantCulture)); cmd.Parameters.AddWithValue("$cur", clean.Currency); cmd.Parameters.AddWithValue("$t", clean.ObservedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)); cmd.Parameters.AddWithValue("$src", clean.Source);
        cmd.ExecuteNonQuery();
        return clean;
    }

    public IReadOnlyList<RemotePriceObservation> List()
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Channel, Shop, ProductId, Price, Currency, ObservedUtc, Source FROM RemotePrices ORDER BY Channel, Shop, ProductId";
        using var r = cmd.ExecuteReader(); var result = new List<RemotePriceObservation>();
        while (r.Read()) result.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), decimal.Parse(r.GetString(3), CultureInfo.InvariantCulture), r.GetString(4), DateTime.Parse(r.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime(), r.GetString(6)));
        return result;
    }
}

/// <summary>
/// The channel price comparison (#929), read-only. For every product and every enabled shop: the price the pricing
/// chain would produce now (through the real preview, so every guard applies — a blocked price says why, a shop
/// without a rule says so), the last-known remote price with its currency and its age (fresh within a day, stale
/// after, unknown when nobody ever observed one), and the verdict: the same, ours higher, ours lower (with the
/// percentage), a currency mismatch, or unknown. Remote prices come from whoever observed them — a marketplace read
/// recorded in the remote price store, or the last price dispatch that succeeded, whose payload is the price the
/// marketplace was sent. Nothing here writes to a marketplace or to a product.
/// </summary>
public static class ChannelPriceComparison
{
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);
    public const string SyncSource = "sync";

    /// <summary>The last succeeded price dispatch per channel/shop/product as an observation: its payload is the price the marketplace was sent; the currency is the rule's, asked by channel and shop.</summary>
    public static IReadOnlyList<RemotePriceObservation> FromSync(IEnumerable<SyncJob> jobs, Func<string, string, string?> currencyOf)
    {
        ArgumentNullException.ThrowIfNull(jobs); ArgumentNullException.ThrowIfNull(currencyOf);
        var newest = new Dictionary<string, RemotePriceObservation>(StringComparer.Ordinal);
        foreach (var job in jobs.Where(j => j is not null && j.Status == SyncStatus.Succeeded && string.Equals(j.Operation, "price", StringComparison.OrdinalIgnoreCase)))
        {
            var parts = (job.Version ?? "").Split(':');
            if (parts.Length < 3 || !decimal.TryParse(parts[^1], NumberStyles.Number, CultureInfo.InvariantCulture, out var price)) continue;
            var channel = job.Channel.Trim().ToLowerInvariant(); var shop = job.ShopId.Trim(); var key = channel + "|" + shop + "|" + parts[0];
            var observation = new RemotePriceObservation(channel, shop, parts[0], price, (currencyOf(channel, shop) ?? "").Trim().ToUpperInvariant(), DateTime.SpecifyKind(job.UpdatedUtc, DateTimeKind.Utc), SyncSource);
            if (!newest.TryGetValue(key, out var existing) || existing.ObservedUtc < observation.ObservedUtc) newest[key] = observation;
        }
        return newest.Values.ToList();
    }

    public static IReadOnlyList<ChannelPriceCell> Build(CatalogStore catalog, IReadOnlyList<CatalogProduct> products, IEnumerable<MarketplaceConnection> connections, IEnumerable<RemotePriceObservation> remote, DateTime nowUtc, TimeSpan? staleAfter = null)
    {
        ArgumentNullException.ThrowIfNull(catalog); ArgumentNullException.ThrowIfNull(products); ArgumentNullException.ThrowIfNull(connections); ArgumentNullException.ThrowIfNull(remote);
        var window = staleAfter ?? StaleAfter;
        var latest = new Dictionary<string, RemotePriceObservation>(StringComparer.Ordinal);
        foreach (var observation in remote)
        {
            var key = observation.Channel.Trim().ToLowerInvariant() + "|" + observation.Shop.Trim() + "|" + observation.ProductId;
            if (!latest.TryGetValue(key, out var existing) || existing.ObservedUtc < observation.ObservedUtc) latest[key] = observation;
        }
        var shops = connections.Select(c => (Channel: (c.Channel ?? "").Trim().ToLowerInvariant(), Shop: (c.ShopId ?? "").Trim())).Where(s => s.Channel.Length > 0 && s.Shop.Length > 0).Distinct().OrderBy(s => s.Channel, StringComparer.Ordinal).ThenBy(s => s.Shop, StringComparer.Ordinal).ToList();
        var cells = new List<ChannelPriceCell>();
        foreach (var shop in shops)
        {
            var policy = catalog.GetPricePolicy(shop.Channel, shop.Shop);
            foreach (var product in products)
            {
                decimal? local = null; var localCurrency = ""; string localState, localWords;
                if (policy is null) { localState = ChannelPriceCell.NoRule; localWords = "bu mağaza için fiyat kuralı yok"; }
                else
                {
                    try { var preview = catalog.PreviewPrice(shop.Channel, shop.Shop, product.Id, new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc))); local = preview.Price; localCurrency = preview.Currency.Trim().ToUpperInvariant(); localState = ChannelPriceCell.LocalOk; localWords = Money(preview.Price, localCurrency); }
                    catch (InvalidOperationException ex) { localState = ChannelPriceCell.LocalBlocked; localWords = Cut(AuditStore.Redact(ex.Message)); }
                }
                latest.TryGetValue(shop.Channel + "|" + shop.Shop + "|" + product.Id, out var observed);
                var remoteState = observed is null ? ChannelPriceCell.RemoteUnknown : nowUtc - observed.ObservedUtc > window ? ChannelPriceCell.RemoteStale : ChannelPriceCell.RemoteKnown;
                var remoteWords = observed is null ? "uzak fiyat bilinmiyor" : Money(observed.Price, observed.Currency) + (remoteState == ChannelPriceCell.RemoteStale ? $" (bayat, {Days(nowUtc - observed.ObservedUtc)})" : $" ({Days(nowUtc - observed.ObservedUtc)})");
                string comparison; decimal? difference = null; string verdict;
                if (local is null || observed is null) { comparison = ChannelPriceCell.Unknown; verdict = "karşılaştırılamadı"; }
                else if (!string.Equals(localCurrency, observed.Currency, StringComparison.Ordinal)) { comparison = ChannelPriceCell.CurrencyMismatch; verdict = $"para birimleri farklı ({localCurrency} / {observed.Currency})"; }
                else
                {
                    difference = observed.Price == 0m ? (local.Value == 0m ? 0m : 100m) : decimal.Round((local.Value - observed.Price) / observed.Price * 100m, 2, MidpointRounding.AwayFromZero);
                    comparison = Math.Abs(local.Value - observed.Price) < 0.005m ? ChannelPriceCell.Same : local.Value > observed.Price ? ChannelPriceCell.Higher : ChannelPriceCell.Lower;
                    verdict = comparison == ChannelPriceCell.Same ? "aynı" : comparison == ChannelPriceCell.Higher ? $"yerel %{difference.Value.ToString("0.##", CultureInfo.InvariantCulture)} yüksek" : $"yerel %{Math.Abs(difference.Value).ToString("0.##", CultureInfo.InvariantCulture)} düşük";
                }
                cells.Add(new(shop.Channel, shop.Shop, product.Id, (product.Sku ?? "").Trim(), local, localCurrency, localState, localWords, observed?.Price, observed?.Currency ?? "", observed?.ObservedUtc, remoteState, comparison, difference, $"{(product.Sku ?? "").Trim()} · {shop.Channel}/{shop.Shop}: yerel {localWords} · uzak {remoteWords} · {verdict}"));
            }
        }
        return cells;
    }

    static string Money(decimal value, string currency) => value.ToString("0.##", CultureInfo.InvariantCulture) + (currency.Length > 0 ? " " + currency : "");
    static string Days(TimeSpan age) { if (age < TimeSpan.Zero) age = TimeSpan.Zero; return age.TotalHours < 1 ? "az önce" : age.TotalDays < 1 ? $"{(int)age.TotalHours} sa önce" : $"{(int)age.TotalDays} gün önce"; }
    static string Cut(string text) => text.Length > 160 ? text[..160] + "…" : text;
}
