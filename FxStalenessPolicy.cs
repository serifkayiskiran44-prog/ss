using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;

namespace TrMarketplaceHubDesktop;

/// <summary>The operator's FX staleness policy for a channel/shop: how old an observed rate may be, and what a staler one does to a manual preview — block it, or warn and proceed. An automatic live write is blocked on a stale rate whatever the mode.</summary>
public sealed class FxStalenessPolicy
{
    public const string Block = "BLOCK", Warn = "WARN";
    public const int DefaultHours = 24, MinHours = 1, MaxHours = 168;
    public string Channel { get; set; } = "";
    public string Shop { get; set; } = "";
    public int StaleAfterHours { get; set; } = DefaultHours;
    public string Mode { get; set; } = Block;
    public int Version { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public TimeSpan StaleAfter => TimeSpan.FromHours(StaleAfterHours);
    /// <summary>Never saved: the built-in 24 hours, block.</summary>
    public bool IsDefault => Version == 0;
}

/// <summary>What an observed rate's age means under a policy: fresh, stale or missing; allowed, warned or blocked; the age in hours; the words.</summary>
public sealed record FxStalenessVerdict(string State, string Decision, double? AgeHours, string Words)
{
    public const string Fresh = "FRESH", Stale = "STALE", Missing = "MISSING";
    public const string Allow = "ALLOW", WarnDecision = "WARN", BlockDecision = "BLOCK";
    public bool Blocks => Decision == BlockDecision;
}

/// <summary>
/// FX staleness policy (#924). The money gate (#285) blocked a rate older than a fixed 24 hours; the threshold is now
/// the operator's, per channel/shop, together with what a staler rate does to a manual preview: BLOCK refuses it,
/// WARN computes with the stale rate and says so. Two things never move: a missing observation time blocks in every
/// mode (there is no rate to fall back to), and an automatic live write — the automation runner's price dispatch —
/// blocks on a stale rate in every mode, so a warning the operator accepted on screen never reaches a marketplace
/// by itself. The policy lives on catalog.db with an optimistic version; nothing here holds a credential.
/// </summary>
public static class FxStaleness
{
    public static FxStalenessVerdict Evaluate(FxStalenessPolicy policy, DateTimeOffset? observedUtc, DateTimeOffset nowUtc, bool automatic)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (observedUtc is null) return new(FxStalenessVerdict.Missing, FxStalenessVerdict.BlockDecision, null, "kur gözlem tarihi yok; kur olmadan fiyat üretilmez");
        var age = nowUtc - observedUtc.Value; if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        var hours = Math.Round(age.TotalHours, 1); var limit = policy.StaleAfterHours.ToString(CultureInfo.InvariantCulture);
        if (age <= policy.StaleAfter) return new(FxStalenessVerdict.Fresh, FxStalenessVerdict.Allow, hours, $"kur {Hours(hours)} saatlik; eşik {limit} saat");
        if (automatic) return new(FxStalenessVerdict.Stale, FxStalenessVerdict.BlockDecision, hours, $"kur bayat: {Hours(hours)} saatlik, eşik {limit} saat; otomatik canlı yazım bayat kurla yapılmaz");
        if (string.Equals(policy.Mode, FxStalenessPolicy.Warn, StringComparison.OrdinalIgnoreCase))
            return new(FxStalenessVerdict.Stale, FxStalenessVerdict.WarnDecision, hours, $"kur bayat: {Hours(hours)} saatlik, eşik {limit} saat; politika uyarıyor, elle önizleme bayat kurla hesaplandı; canlı yazım engellenir");
        return new(FxStalenessVerdict.Stale, FxStalenessVerdict.BlockDecision, hours, $"kur bayat: {Hours(hours)} saatlik, eşik {limit} saat; politika engelliyor; kuru yenileyin");
    }

    public static void Validate(FxStalenessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Channel = (policy.Channel ?? "").Trim().ToLowerInvariant(); policy.Shop = (policy.Shop ?? "").Trim(); policy.Mode = (policy.Mode ?? "").Trim().ToUpperInvariant();
        if (policy.Channel.Length == 0 || policy.Shop.Length == 0) throw new ArgumentException("Pazaryeri ve mağaza gerekli.");
        if (policy.StaleAfterHours < FxStalenessPolicy.MinHours || policy.StaleAfterHours > FxStalenessPolicy.MaxHours) throw new ArgumentException($"Kur bayatlık eşiği {FxStalenessPolicy.MinHours} ile {FxStalenessPolicy.MaxHours} saat arasında olmalı.");
        if (policy.Mode is not (FxStalenessPolicy.Block or FxStalenessPolicy.Warn)) throw new ArgumentException("Bayat kur politikası BLOCK ya da WARN olmalı.");
    }

    static string Hours(double hours) => hours.ToString("0.#", CultureInfo.InvariantCulture);
}

public sealed class FxStalenessPolicyStore
{
    readonly string connectionString;

    public FxStalenessPolicyStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "catalog.db") }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS FxStalenessPolicies(Channel TEXT NOT NULL, Shop TEXT NOT NULL, StaleAfterHours INTEGER NOT NULL, Mode TEXT NOT NULL, Version INTEGER NOT NULL, UpdatedUtc TEXT NOT NULL, PRIMARY KEY(Channel, Shop))";
        cmd.ExecuteNonQuery();
    }

    SqliteConnection Open() => SqliteConnectionPolicy.Open(connectionString);

    /// <summary>The saved policy of a channel/shop, or the built-in default (24 hours, block; version 0) named for that channel/shop.</summary>
    public FxStalenessPolicy Get(string channel, string shop)
    {
        var key = ((channel ?? "").Trim().ToLowerInvariant(), (shop ?? "").Trim());
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT StaleAfterHours, Mode, Version, UpdatedUtc FROM FxStalenessPolicies WHERE Channel=$c AND Shop=$s";
        cmd.Parameters.AddWithValue("$c", key.Item1); cmd.Parameters.AddWithValue("$s", key.Item2);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return new() { Channel = key.Item1, Shop = key.Item2 };
        return new() { Channel = key.Item1, Shop = key.Item2, StaleAfterHours = r.GetInt32(0), Mode = r.GetString(1), Version = r.GetInt32(2), UpdatedUtc = DateTime.Parse(r.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) };
    }

    public FxStalenessPolicy Save(FxStalenessPolicy policy)
    {
        FxStaleness.Validate(policy);
        var existing = Get(policy.Channel, policy.Shop);
        if (existing.Version != policy.Version) throw new InvalidOperationException("Kur politikası başka işlemde değişti; yeniden yükleyin.");
        policy.Version = existing.Version + 1; policy.UpdatedUtc = DateTime.UtcNow;
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO FxStalenessPolicies(Channel, Shop, StaleAfterHours, Mode, Version, UpdatedUtc) VALUES($c, $s, $h, $m, $v, $u) ON CONFLICT(Channel, Shop) DO UPDATE SET StaleAfterHours=excluded.StaleAfterHours, Mode=excluded.Mode, Version=excluded.Version, UpdatedUtc=excluded.UpdatedUtc";
        cmd.Parameters.AddWithValue("$c", policy.Channel); cmd.Parameters.AddWithValue("$s", policy.Shop); cmd.Parameters.AddWithValue("$h", policy.StaleAfterHours); cmd.Parameters.AddWithValue("$m", policy.Mode); cmd.Parameters.AddWithValue("$v", policy.Version); cmd.Parameters.AddWithValue("$u", policy.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
        return policy;
    }

    public IReadOnlyList<FxStalenessPolicy> List()
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Channel, Shop, StaleAfterHours, Mode, Version, UpdatedUtc FROM FxStalenessPolicies ORDER BY Channel, Shop";
        using var r = cmd.ExecuteReader(); var result = new List<FxStalenessPolicy>();
        while (r.Read()) result.Add(new() { Channel = r.GetString(0), Shop = r.GetString(1), StaleAfterHours = r.GetInt32(2), Mode = r.GetString(3), Version = r.GetInt32(4), UpdatedUtc = DateTime.Parse(r.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) });
        return result;
    }
}
