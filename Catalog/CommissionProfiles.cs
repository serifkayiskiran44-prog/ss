using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;

namespace TrMarketplaceHubDesktop.Catalog;

/// <summary>One commission period as one immutable revision of a channel/shop: the dates (start inclusive, end exclusive, or open), the rate and the fixed fee, the note, when it was written, and whether a later revision replaced it.</summary>
public sealed record CommissionProfile(long Id, string Channel, string Shop, int Revision, DateTime EffectiveFromUtc, DateTime? EffectiveToUtc, decimal CommissionPercent, decimal FixedFeeTry, string Note, DateTime CreatedUtc, string Status, int RetiredByRevision)
{
    public const string Active = "ACTIVE", Retired = "RETIRED";
    /// <summary>Start inclusive, end exclusive: at exactly the end the next period applies.</summary>
    public bool Covers(DateTime atUtc) => EffectiveFromUtc <= atUtc && (EffectiveToUtc is null || atUtc < EffectiveToUtc.Value);
    public string Period => $"{Stamp(EffectiveFromUtc)} → {(EffectiveToUtc is { } to ? Stamp(to) : "açık")}";
    public static string Stamp(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
}

/// <summary>What the pricing chain gets for a date: the period that applies, a gap between periods (nothing applies, named), or none defined (the rule's flat rate stands).</summary>
public sealed record CommissionResolution(string State, CommissionProfile? Profile, string Words)
{
    public const string Applies = "APPLIES", Gap = "GAP", None = "NONE";
}

/// <summary>
/// Commission effective-date versioning (#923). A channel/shop's commission is a sequence of dated periods, each an
/// immutable revision: a period is never edited — closing an open period writes the closed copy as a new revision and
/// retires the old one by pointer, so the history shows every value that was ever in force and who replaced it.
/// Periods of one channel/shop never overlap (start inclusive, end exclusive, so a period may begin exactly where
/// another ends); an overlapping save is refused by name. Resolving a date yields the period that applies, a named
/// gap (the previous end and the next start), or none — with none the price rule's flat rate stands as before; with
/// a gap the pricing chain refuses, never guessing a rate. A past date resolves the period in force then, so a
/// historical calculation uses the commission of its day. Notes are redacted; nothing here holds a credential.
/// </summary>
public sealed class CommissionProfileStore
{
    public const int NoteLimit = 200;
    readonly string connectionString;

    public CommissionProfileStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "catalog.db") }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS CommissionProfiles(Id INTEGER PRIMARY KEY AUTOINCREMENT, Channel TEXT NOT NULL, Shop TEXT NOT NULL, Revision INTEGER NOT NULL, EffectiveFromUtc TEXT NOT NULL, EffectiveToUtc TEXT NULL, CommissionPercent TEXT NOT NULL, FixedFeeTry TEXT NOT NULL, Note TEXT NOT NULL DEFAULT '', CreatedUtc TEXT NOT NULL, Status TEXT NOT NULL, RetiredByRevision INTEGER NOT NULL DEFAULT 0, UNIQUE(Channel, Shop, Revision))";
        cmd.ExecuteNonQuery();
    }

    SqliteConnection Open() => SqliteConnectionPolicy.Open(connectionString);

    static (string Channel, string Shop) Key(string? channel, string? shop)
    {
        var c = (channel ?? "").Trim().ToLowerInvariant(); var s = (shop ?? "").Trim();
        if (c.Length == 0 || s.Length == 0) throw new ArgumentException("Pazaryeri ve mağaza gerekli.");
        return (c, s);
    }

    /// <summary>Writes a new period as the next revision; refused when it overlaps an active period of the same channel/shop, or when its numbers or dates are not a period.</summary>
    public CommissionProfile Save(string channel, string shop, DateTime effectiveFromUtc, DateTime? effectiveToUtc, decimal commissionPercent, decimal fixedFeeTry, string? note, DateTime nowUtc)
    {
        var key = Key(channel, shop);
        if (commissionPercent < 0 || commissionPercent > 100) throw new ArgumentException("Komisyon oranı 0 ile 100 arasında olmalı.");
        if (fixedFeeTry < 0) throw new ArgumentException("Sabit ücret negatif olamaz.");
        if (effectiveToUtc is { } end && end <= effectiveFromUtc) throw new ArgumentException("Dönem bitişi başlangıçtan sonra olmalı.");
        var cleanNote = AuditStore.Redact((note ?? "").Trim()); if (cleanNote.Length > NoteLimit) cleanNote = cleanNote[..NoteLimit];
        using var c = Open(); using var tx = c.BeginTransaction();
        var existing = Read(c, tx, key.Channel, key.Shop);
        foreach (var period in existing.Where(x => x.Status == CommissionProfile.Active))
            if (Overlaps(period, effectiveFromUtc, effectiveToUtc)) throw new InvalidOperationException($"Komisyon dönemi çakışıyor: {period.Revision.ToString(CultureInfo.InvariantCulture)} numaralı dönem ({period.Period}) ile örtüşüyor; önce o dönemi kapatın ya da tarihleri değiştirin.");
        var revision = existing.Count == 0 ? 1 : existing.Max(x => x.Revision) + 1;
        var id = Insert(c, tx, key.Channel, key.Shop, revision, effectiveFromUtc, effectiveToUtc, commissionPercent, fixedFeeTry, cleanNote, nowUtc);
        tx.Commit();
        return new(id, key.Channel, key.Shop, revision, effectiveFromUtc, effectiveToUtc, commissionPercent, fixedFeeTry, cleanNote, nowUtc, CommissionProfile.Active, 0);
    }

    /// <summary>Ends a period at a date: the closed copy is written as the next revision and the old revision is retired by pointer — its own content never changes. A closed period cannot be extended; add a period instead.</summary>
    public CommissionProfile Close(string channel, string shop, int revision, DateTime effectiveToUtc, DateTime nowUtc)
    {
        var key = Key(channel, shop);
        using var c = Open(); using var tx = c.BeginTransaction();
        var existing = Read(c, tx, key.Channel, key.Shop);
        var target = existing.FirstOrDefault(x => x.Revision == revision) ?? throw new InvalidOperationException($"{revision.ToString(CultureInfo.InvariantCulture)} numaralı komisyon dönemi bulunamadı.");
        if (target.Status != CommissionProfile.Active) throw new InvalidOperationException($"{revision.ToString(CultureInfo.InvariantCulture)} numaralı dönem artık geçerli değil; {target.RetiredByRevision.ToString(CultureInfo.InvariantCulture)} numaralı sürüm onun yerini aldı.");
        if (effectiveToUtc <= target.EffectiveFromUtc) throw new ArgumentException("Dönem bitişi başlangıçtan sonra olmalı.");
        if (target.EffectiveToUtc is { } current && effectiveToUtc > current) throw new InvalidOperationException("Kapalı bir dönem uzatılamaz; yeni bir dönem ekleyin.");
        var next = existing.Max(x => x.Revision) + 1;
        var id = Insert(c, tx, key.Channel, key.Shop, next, target.EffectiveFromUtc, effectiveToUtc, target.CommissionPercent, target.FixedFeeTry, target.Note, nowUtc);
        using (var retire = c.CreateCommand())
        {
            retire.Transaction = tx; retire.CommandText = "UPDATE CommissionProfiles SET Status=$s, RetiredByRevision=$r WHERE Id=$id";
            retire.Parameters.AddWithValue("$s", CommissionProfile.Retired); retire.Parameters.AddWithValue("$r", next); retire.Parameters.AddWithValue("$id", target.Id); retire.ExecuteNonQuery();
        }
        tx.Commit();
        return new(id, key.Channel, key.Shop, next, target.EffectiveFromUtc, effectiveToUtc, target.CommissionPercent, target.FixedFeeTry, target.Note, nowUtc, CommissionProfile.Active, 0);
    }

    /// <summary>Every revision of a channel/shop, newest first — the retired ones included, as the history.</summary>
    public IReadOnlyList<CommissionProfile> List(string channel, string shop)
    {
        var key = Key(channel, shop); using var c = Open();
        return Read(c, null, key.Channel, key.Shop).OrderByDescending(x => x.Revision).ToList();
    }

    /// <summary>The periods in force — the active revisions — by start.</summary>
    public IReadOnlyList<CommissionProfile> Periods(string channel, string shop)
    {
        var key = Key(channel, shop); using var c = Open();
        return Read(c, null, key.Channel, key.Shop).Where(x => x.Status == CommissionProfile.Active).OrderBy(x => x.EffectiveFromUtc).ThenBy(x => x.Revision).ToList();
    }

    public CommissionResolution Resolve(string channel, string shop, DateTime atUtc)
    {
        var periods = Periods(channel, shop);
        if (periods.Count == 0) return new(CommissionResolution.None, null, "komisyon dönemi tanımlı değil; kuralın komisyon oranı geçerli");
        var hit = periods.Where(p => p.Covers(atUtc)).OrderByDescending(p => p.Revision).FirstOrDefault();
        if (hit is not null)
            return new(CommissionResolution.Applies, hit, $"komisyon %{hit.CommissionPercent.ToString("0.##", CultureInfo.InvariantCulture)}" + (hit.FixedFeeTry > 0 ? $" + {hit.FixedFeeTry.ToString("0.##", CultureInfo.InvariantCulture)} TL sabit" : "") + $" · dönem {hit.Revision.ToString(CultureInfo.InvariantCulture)} ({hit.Period})");
        var previous = periods.Where(p => p.EffectiveToUtc is { } to && to <= atUtc).OrderByDescending(p => p.EffectiveToUtc).FirstOrDefault();
        var next = periods.Where(p => p.EffectiveFromUtc > atUtc).OrderBy(p => p.EffectiveFromUtc).FirstOrDefault();
        var words = $"komisyon dönemi boşluğu: {CommissionProfile.Stamp(atUtc)} için dönem yok"
            + (previous is null ? "" : $"; önceki dönem {previous.Revision.ToString(CultureInfo.InvariantCulture)} {CommissionProfile.Stamp(previous.EffectiveToUtc!.Value)} tarihinde bitti")
            + (next is null ? "" : $"; sonraki dönem {next.Revision.ToString(CultureInfo.InvariantCulture)} {CommissionProfile.Stamp(next.EffectiveFromUtc)} tarihinde başlıyor")
            + "; fiyat üretilmez";
        return new(CommissionResolution.Gap, null, words);
    }

    static bool Overlaps(CommissionProfile period, DateTime from, DateTime? to) => period.EffectiveFromUtc < (to ?? DateTime.MaxValue) && from < (period.EffectiveToUtc ?? DateTime.MaxValue);

    static long Insert(SqliteConnection c, SqliteTransaction tx, string channel, string shop, int revision, DateTime from, DateTime? to, decimal percent, decimal fee, string note, DateTime nowUtc)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO CommissionProfiles(Channel, Shop, Revision, EffectiveFromUtc, EffectiveToUtc, CommissionPercent, FixedFeeTry, Note, CreatedUtc, Status, RetiredByRevision) VALUES($c, $s, $r, $from, $to, $p, $f, $n, $now, $st, 0); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$c", channel); cmd.Parameters.AddWithValue("$s", shop); cmd.Parameters.AddWithValue("$r", revision);
        cmd.Parameters.AddWithValue("$from", from.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)); cmd.Parameters.AddWithValue("$to", to is { } end ? end.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
        cmd.Parameters.AddWithValue("$p", percent.ToString(CultureInfo.InvariantCulture)); cmd.Parameters.AddWithValue("$f", fee.ToString(CultureInfo.InvariantCulture)); cmd.Parameters.AddWithValue("$n", note);
        cmd.Parameters.AddWithValue("$now", nowUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)); cmd.Parameters.AddWithValue("$st", CommissionProfile.Active);
        return (long)cmd.ExecuteScalar()!;
    }

    static List<CommissionProfile> Read(SqliteConnection c, SqliteTransaction? tx, string channel, string shop)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT Id, Channel, Shop, Revision, EffectiveFromUtc, EffectiveToUtc, CommissionPercent, FixedFeeTry, Note, CreatedUtc, Status, RetiredByRevision FROM CommissionProfiles WHERE Channel=$c AND Shop=$s ORDER BY Revision";
        cmd.Parameters.AddWithValue("$c", channel); cmd.Parameters.AddWithValue("$s", shop);
        using var r = cmd.ExecuteReader(); var result = new List<CommissionProfile>();
        while (r.Read())
            result.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3), Utc(r.GetString(4)), r.IsDBNull(5) ? null : Utc(r.GetString(5)), decimal.Parse(r.GetString(6), CultureInfo.InvariantCulture), decimal.Parse(r.GetString(7), CultureInfo.InvariantCulture), r.GetString(8), Utc(r.GetString(9)), r.GetString(10), r.GetInt32(11)));
        return result;
    }

    static DateTime Utc(string text) => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}
