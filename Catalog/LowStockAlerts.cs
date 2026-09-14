using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;

namespace TrMarketplaceHubDesktop.Catalog;

/// <summary>A low-stock threshold for a product, a source or a store (channel/shop); a disabled one raises nothing.</summary>
public sealed record LowStockAlertProfile(long Id, string Scope, string ScopeId, int Threshold, bool Enabled, string Note, DateTime UpdatedUtc)
{
    public const string ProductScope = "product", SourceScope = "source", StoreScope = "store";
    public static readonly IReadOnlyList<string> Scopes = new[] { ProductScope, SourceScope, StoreScope };
    public string Words => $"{Scope} {ScopeId}: eşik {Threshold.ToString(CultureInfo.InvariantCulture)} ({(Enabled ? "açık" : "kapalı")})";
}

/// <summary>One live low-stock finding: the product, the profile that raised it, the figure judged against the threshold, the store (empty for a global one), the alert's title and detail.</summary>
public sealed record LowStockAlert(string ProductId, string Sku, string Scope, long ProfileId, int Threshold, int Observed, string StoreKey, string Title, string Detail);

/// <summary>
/// Low-stock alert profiles (#941). A threshold is set for a product, a source or a store; the most specific one
/// decides for a product — the product's own, else its source's, judged on the record's stock — and a store's
/// profile judges the store's available figure (the real projection when the store has a stock policy, with its
/// buffer and holds; the record's stock otherwise) for the products no product or source profile covers. The
/// findings become dashboard alerts whose identity is the ledger's fingerprint (severity, route, store, title), so
/// a repeat is one more sighting of the same alert, a recovery — the stock back above the threshold, or the profile
/// disabled — resolves it on the next sync, and a relapse reopens it. Skus, counts and store keys only.
/// </summary>
public static class LowStockAlerts
{
    public const string Source = "low-stock";
    public static string TitleFor(string sku) => "Düşük stok: " + (sku ?? "").Trim();

    public static IReadOnlyList<LowStockAlert> Evaluate(IReadOnlyList<CatalogProduct> products, IReadOnlyList<LowStockAlertProfile> profiles, Func<string, string, string, int?>? availableOf, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(products); ArgumentNullException.ThrowIfNull(profiles);
        var enabled = profiles.Where(p => p.Enabled).ToList();
        var byProduct = enabled.Where(p => p.Scope == LowStockAlertProfile.ProductScope).GroupBy(p => p.ScopeId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var bySource = enabled.Where(p => p.Scope == LowStockAlertProfile.SourceScope).GroupBy(p => p.ScopeId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var stores = enabled.Where(p => p.Scope == LowStockAlertProfile.StoreScope).OrderBy(p => p.ScopeId, StringComparer.Ordinal).ToList();
        var alerts = new List<LowStockAlert>();
        foreach (var product in products.Where(p => p.Active).OrderBy(p => p.Sku, StringComparer.Ordinal))
        {
            var global = byProduct.TryGetValue(product.Id, out var own) ? own : (!string.IsNullOrEmpty(product.SourceId) && bySource.TryGetValue(product.SourceId, out var source) ? source : null);
            if (global is not null)
            {
                if (product.Stock <= global.Threshold)
                    alerts.Add(new(product.Id, product.Sku, global.Scope, global.Id, global.Threshold, product.Stock, "", TitleFor(product.Sku), $"stok {N(product.Stock)} ≤ eşik {N(global.Threshold)} ({ScopeWord(global.Scope)} profili #{N(global.Id)})"));
                continue; // the most specific profile decides; a store profile adds no second alert for this product
            }
            foreach (var store in stores)
            {
                var (channel, shop) = SplitStore(store.ScopeId); if (channel.Length == 0 || shop.Length == 0) continue;
                var observed = availableOf?.Invoke(channel, shop, product.Id) ?? product.Stock;
                if (observed <= store.Threshold)
                    alerts.Add(new(product.Id, product.Sku, LowStockAlertProfile.StoreScope, store.Id, store.Threshold, observed, DashboardStoreFilter.KeyFor(channel, shop), TitleFor(product.Sku), $"{channel}/{shop}: gösterilebilir {N(observed)} ≤ eşik {N(store.Threshold)} (mağaza profili #{N(store.Id)})"));
            }
        }
        return alerts;
    }

    public static (string Channel, string Shop) SplitStore(string scopeId)
    {
        var text = (scopeId ?? "").Trim(); var i = text.IndexOf('/');
        return i <= 0 ? ("", "") : (text[..i].Trim().ToLowerInvariant(), text[(i + 1)..].Trim());
    }

    static string ScopeWord(string scope) => scope switch { LowStockAlertProfile.ProductScope => "ürün", LowStockAlertProfile.SourceScope => "kaynak", _ => "mağaza" };
    static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The profiles, one per scope and id, in catalog.db.</summary>
public sealed class LowStockAlertProfileStore
{
    public const int NoteLimit = 200;
    readonly string connectionString;

    public LowStockAlertProfileStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "catalog.db") }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS LowStockAlertProfiles(Id INTEGER PRIMARY KEY AUTOINCREMENT, Scope TEXT NOT NULL, ScopeId TEXT NOT NULL, Threshold INTEGER NOT NULL, Enabled INTEGER NOT NULL, Note TEXT NOT NULL DEFAULT '', UpdatedUtc TEXT NOT NULL, UNIQUE(Scope, ScopeId))";
        cmd.ExecuteNonQuery();
    }

    SqliteConnection Open() => SqliteConnectionPolicy.Open(connectionString);

    public LowStockAlertProfile Save(string scope, string scopeId, int threshold, bool enabled, string? note, DateTime nowUtc)
    {
        var cleanScope = (scope ?? "").Trim().ToLowerInvariant(); var cleanId = (scopeId ?? "").Trim();
        if (!LowStockAlertProfile.Scopes.Contains(cleanScope)) throw new ArgumentException("Profil kapsamı product, source ya da store olmalı.");
        if (cleanScope == LowStockAlertProfile.StoreScope) { var (channel, shop) = LowStockAlerts.SplitStore(cleanId); if (channel.Length == 0 || shop.Length == 0) throw new ArgumentException("Mağaza profili kimliği pazaryeri/mağaza biçiminde olmalı (örn. etsy/S1)."); cleanId = channel + "/" + shop; }
        if (cleanId.Length == 0) throw new ArgumentException("Profil kapsamının kimliği gerekli.");
        if (threshold < 0) throw new ArgumentException("Düşük stok eşiği negatif olamaz.");
        var cleanNote = AuditStore.Redact((note ?? "").Trim()); if (cleanNote.Length > NoteLimit) cleanNote = cleanNote[..NoteLimit];
        var at = DateTime.SpecifyKind(nowUtc.Kind == DateTimeKind.Local ? nowUtc.ToUniversalTime() : nowUtc, DateTimeKind.Utc);
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO LowStockAlertProfiles(Scope, ScopeId, Threshold, Enabled, Note, UpdatedUtc) VALUES($s, $i, $t, $e, $n, $u) ON CONFLICT(Scope, ScopeId) DO UPDATE SET Threshold=excluded.Threshold, Enabled=excluded.Enabled, Note=excluded.Note, UpdatedUtc=excluded.UpdatedUtc; SELECT Id FROM LowStockAlertProfiles WHERE Scope=$s AND ScopeId=$i";
        cmd.Parameters.AddWithValue("$s", cleanScope); cmd.Parameters.AddWithValue("$i", cleanId); cmd.Parameters.AddWithValue("$t", threshold); cmd.Parameters.AddWithValue("$e", enabled ? 1 : 0); cmd.Parameters.AddWithValue("$n", cleanNote); cmd.Parameters.AddWithValue("$u", at.ToString("O", CultureInfo.InvariantCulture));
        var id = (long)cmd.ExecuteScalar()!;
        return new(id, cleanScope, cleanId, threshold, enabled, cleanNote, at);
    }

    public IReadOnlyList<LowStockAlertProfile> List()
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id, Scope, ScopeId, Threshold, Enabled, Note, UpdatedUtc FROM LowStockAlertProfiles ORDER BY Scope, ScopeId";
        using var r = cmd.ExecuteReader(); var result = new List<LowStockAlertProfile>();
        while (r.Read()) result.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4) == 1, r.GetString(5), DateTime.Parse(r.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime()));
        return result;
    }
}

public partial class CatalogStore
{
    /// <summary>#941: the live low-stock findings over every active product and every enabled profile; a store profile judges the real projection where the store has a policy.</summary>
    public IReadOnlyList<LowStockAlert> LowStockAlerts(DateTime nowUtc)
        => Catalog.LowStockAlerts.Evaluate(Products(), new LowStockAlertProfileStore(dataDirectory).List(), (channel, shop, productId) =>
        {
            if (GetStockPolicy(channel, shop) is null) return null;
            try { return ProjectStock(channel, shop, productId, nowUtc).Available; } catch (InvalidOperationException) { return null; }
        }, nowUtc);
}
