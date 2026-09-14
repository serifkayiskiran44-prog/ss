using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;

namespace TrMarketplaceHubDesktop.Catalog;

/// <summary>One rounding profile revision of a channel/shop: the decimals the sale price keeps and how a midpoint (or any remainder) is settled; the note; when it took effect.</summary>
public sealed record RoundingProfile(long Id, string Channel, string Shop, int Revision, int Decimals, string Mode, string Note, DateTime CreatedUtc)
{
    public const string AwayFromZero = "AwayFromZero", ToEven = "ToEven", ToZero = "ToZero", Up = "Up", Down = "Down";
    public static readonly IReadOnlyList<string> Modes = new[] { AwayFromZero, ToEven, ToZero, Up, Down };
    public const int MaxDecimals = 4;

    public static MidpointRounding MidpointOf(string mode) => (mode ?? "").Trim() switch
    {
        AwayFromZero => MidpointRounding.AwayFromZero, ToEven => MidpointRounding.ToEven, ToZero => MidpointRounding.ToZero,
        Up => MidpointRounding.ToPositiveInfinity, Down => MidpointRounding.ToNegativeInfinity,
        _ => throw new ArgumentException("Yuvarlama modu desteklenmiyor: " + Modes.Aggregate((a, b) => a + ", " + b) + " kullanın."),
    };

    public static decimal Round(decimal value, int decimals, string mode) => Math.Round(value, decimals, MidpointOf(mode));
    public decimal Apply(decimal value) => Round(value, Decimals, Mode);
    public string Words => $"{Decimals.ToString(CultureInfo.InvariantCulture)} hane, {Mode} · profil sürüm {Revision.ToString(CultureInfo.InvariantCulture)}";
}

/// <summary>The rounding the chain used for a date: a profile revision, or the currency's own precision with half away from zero when the shop has none — the words say which, with the revision.</summary>
public sealed record RoundingResolution(RoundingProfile? Profile, int Decimals, string Mode, string Words)
{
    public bool IsDefault => Profile is null;
    public decimal Apply(decimal value) => RoundingProfile.Round(value, Decimals, Mode);
}

/// <summary>What a currency carries: two decimals for the money most shops trade in, none for the yen-like, three for the dinars.</summary>
public static class CurrencyPrecision
{
    public static int Of(string? currency) => (currency ?? "").Trim().ToUpperInvariant() switch { "JPY" or "KRW" or "HUF" or "ISK" or "CLP" => 0, "KWD" or "BHD" or "OMR" or "JOD" or "TND" => 3, _ => 2 };
}

/// <summary>
/// Channel rounding profiles (#925). The sale price the chain produces was rounded to two decimals half away from
/// zero for every shop; a shop may now say how many decimals it keeps (never more than its currency carries) and
/// how a remainder is settled — half away from zero, half to even, toward zero, always up, always down. A profile is
/// a revision: saving writes the next one, none is edited, and a date resolves the revision in force then, so a
/// historical calculation rounds as its day did and the preview names the revision it used. Without a profile the
/// currency's precision with half away from zero stands, exactly as before.
/// </summary>
public sealed class RoundingProfileStore
{
    public const int NoteLimit = 200;
    readonly string connectionString;

    public RoundingProfileStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "catalog.db") }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS RoundingProfiles(Id INTEGER PRIMARY KEY AUTOINCREMENT, Channel TEXT NOT NULL, Shop TEXT NOT NULL, Revision INTEGER NOT NULL, Decimals INTEGER NOT NULL, Mode TEXT NOT NULL, Note TEXT NOT NULL DEFAULT '', CreatedUtc TEXT NOT NULL, UNIQUE(Channel, Shop, Revision))";
        cmd.ExecuteNonQuery();
    }

    SqliteConnection Open() => SqliteConnectionPolicy.Open(connectionString);

    static (string Channel, string Shop) Key(string? channel, string? shop)
    {
        var c = (channel ?? "").Trim().ToLowerInvariant(); var s = (shop ?? "").Trim();
        if (c.Length == 0 || s.Length == 0) throw new ArgumentException("Pazaryeri ve mağaza gerekli.");
        return (c, s);
    }

    /// <summary>Writes the next revision; refused when the decimals exceed the currency's precision or the mode is not supported.</summary>
    public RoundingProfile Save(string channel, string shop, int decimals, string mode, string? currency, string? note, DateTime nowUtc)
    {
        var key = Key(channel, shop); var cleanMode = (mode ?? "").Trim();
        if (decimals < 0 || decimals > RoundingProfile.MaxDecimals) throw new ArgumentException($"Ondalık hane 0 ile {RoundingProfile.MaxDecimals.ToString(CultureInfo.InvariantCulture)} arasında olmalı.");
        if (!string.IsNullOrWhiteSpace(currency) && decimals > CurrencyPrecision.Of(currency)) throw new ArgumentException($"{currency.Trim().ToUpperInvariant()} en çok {CurrencyPrecision.Of(currency).ToString(CultureInfo.InvariantCulture)} ondalık hane taşır; profil daha fazlasını isteyemez.");
        _ = RoundingProfile.MidpointOf(cleanMode);
        var cleanNote = AuditStore.Redact((note ?? "").Trim()); if (cleanNote.Length > NoteLimit) cleanNote = cleanNote[..NoteLimit];
        using var c = Open(); using var tx = c.BeginTransaction();
        var existing = Read(c, tx, key.Channel, key.Shop);
        var revision = existing.Count == 0 ? 1 : existing.Max(x => x.Revision) + 1;
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO RoundingProfiles(Channel, Shop, Revision, Decimals, Mode, Note, CreatedUtc) VALUES($c, $s, $r, $d, $m, $n, $t); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$c", key.Channel); cmd.Parameters.AddWithValue("$s", key.Shop); cmd.Parameters.AddWithValue("$r", revision); cmd.Parameters.AddWithValue("$d", decimals); cmd.Parameters.AddWithValue("$m", cleanMode); cmd.Parameters.AddWithValue("$n", cleanNote); cmd.Parameters.AddWithValue("$t", nowUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            var id = (long)cmd.ExecuteScalar()!;
            tx.Commit();
            return new(id, key.Channel, key.Shop, revision, decimals, cleanMode, cleanNote, nowUtc);
        }
    }

    /// <summary>Every revision, newest first.</summary>
    public IReadOnlyList<RoundingProfile> List(string channel, string shop)
    {
        var key = Key(channel, shop); using var c = Open();
        return Read(c, null, key.Channel, key.Shop).OrderByDescending(x => x.Revision).ToList();
    }

    /// <summary>The revision in force at a date — the newest written at or before it — or the currency's default.</summary>
    public RoundingResolution Resolve(string channel, string shop, DateTime atUtc, string? currency)
    {
        var key = Key(channel, shop); using var c = Open();
        var hit = Read(c, null, key.Channel, key.Shop).Where(x => x.CreatedUtc <= atUtc).OrderByDescending(x => x.Revision).FirstOrDefault();
        if (hit is not null) return new(hit, hit.Decimals, hit.Mode, hit.Words);
        var decimals = CurrencyPrecision.Of(currency);
        return new(null, decimals, RoundingProfile.AwayFromZero, $"profil yok; varsayılan {decimals.ToString(CultureInfo.InvariantCulture)} hane, {RoundingProfile.AwayFromZero}");
    }

    static List<RoundingProfile> Read(SqliteConnection c, SqliteTransaction? tx, string channel, string shop)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT Id, Channel, Shop, Revision, Decimals, Mode, Note, CreatedUtc FROM RoundingProfiles WHERE Channel=$c AND Shop=$s ORDER BY Revision";
        cmd.Parameters.AddWithValue("$c", channel); cmd.Parameters.AddWithValue("$s", shop);
        using var r = cmd.ExecuteReader(); var result = new List<RoundingProfile>();
        while (r.Read()) result.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4), r.GetString(5), r.GetString(6), DateTime.Parse(r.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime()));
        return result;
    }
}
