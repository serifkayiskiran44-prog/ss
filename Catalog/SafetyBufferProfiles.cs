using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;

namespace TrMarketplaceHubDesktop.Catalog;

/// <summary>One safety buffer profile: for a product, a source or a store, the units held back from the available stock, effective from a moment to another or open, with a note and when it was written.</summary>
public sealed record SafetyBufferProfile(long Id, string Scope, string ScopeId, int Buffer, DateTime EffectiveFromUtc, DateTime? EffectiveToUtc, string Note, DateTime CreatedUtc)
{
    public const string ProductScope = "product", SourceScope = "source", StoreScope = "store";
    public static readonly IReadOnlyList<string> Scopes = new[] { ProductScope, SourceScope, StoreScope };
    /// <summary>Start inclusive, end exclusive.</summary>
    public bool Covers(DateTime atUtc) => EffectiveFromUtc <= atUtc && (EffectiveToUtc is null || atUtc < EffectiveToUtc.Value);
    public string Period => $"{Stamp(EffectiveFromUtc)} → {(EffectiveToUtc is { } to ? Stamp(to) : "açık")}";
    public static string StoreKey(string channel, string shop) => (channel ?? "").Trim().ToLowerInvariant() + "/" + (shop ?? "").Trim();
    public static string Stamp(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
}

/// <summary>Which buffer the projection used and why: the units, where they came from (a product, source or store profile, or the shop policy), the profile when one applied, the words.</summary>
public sealed record SafetyBufferResolution(int Buffer, string Origin, SafetyBufferProfile? Profile, string Words)
{
    public const string Product = "product", Source = "source", Store = "store", Policy = "policy";
}

/// <summary>
/// Safety buffer profiles (#933). The units held back from a product's available stock used to be the shop policy's
/// single safety stock; a profile now says it for a product, a source or a store, effective from a date (to another
/// or open), and the most specific one in force at the projection's moment wins — product over source over store —
/// with the shop policy's safety stock last. Profiles of one scope never overlap in time (a period may begin exactly
/// where another ends); a zero buffer is a buffer of nothing, a negative one is refused. The projection names the
/// buffer it used and where it came from, and says when the stock sits below the buffer — nothing available, and
/// said so — instead of hiding it in a zero.
/// </summary>
public sealed class SafetyBufferProfileStore
{
    public const int NoteLimit = 200;
    readonly string connectionString;

    public SafetyBufferProfileStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "catalog.db") }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS SafetyBufferProfiles(Id INTEGER PRIMARY KEY AUTOINCREMENT, Scope TEXT NOT NULL, ScopeId TEXT NOT NULL, Buffer INTEGER NOT NULL, EffectiveFromUtc TEXT NOT NULL, EffectiveToUtc TEXT NULL, Note TEXT NOT NULL DEFAULT '', CreatedUtc TEXT NOT NULL); CREATE INDEX IF NOT EXISTS IX_SafetyBufferProfiles_Scope ON SafetyBufferProfiles(Scope, ScopeId)";
        cmd.ExecuteNonQuery();
    }

    SqliteConnection Open() => SqliteConnectionPolicy.Open(connectionString);

    public SafetyBufferProfile Save(string scope, string scopeId, int buffer, DateTime effectiveFromUtc, DateTime? effectiveToUtc, string? note, DateTime nowUtc)
    {
        var cleanScope = (scope ?? "").Trim().ToLowerInvariant(); var cleanId = (scopeId ?? "").Trim();
        if (!SafetyBufferProfile.Scopes.Contains(cleanScope)) throw new ArgumentException("Profil kapsamı product, source ya da store olmalı.");
        if (cleanId.Length == 0) throw new ArgumentException("Profil kapsamının kimliği gerekli.");
        if (buffer < 0) throw new ArgumentException("Güvenlik tamponu negatif olamaz; 0 hiç tampon demektir.");
        if (effectiveToUtc is { } end && end <= effectiveFromUtc) throw new ArgumentException("Profil bitişi başlangıçtan sonra olmalı.");
        var cleanNote = AuditStore.Redact((note ?? "").Trim()); if (cleanNote.Length > NoteLimit) cleanNote = cleanNote[..NoteLimit];
        using var c = Open(); using var tx = c.BeginTransaction();
        foreach (var existing in Read(c, tx, cleanScope, cleanId))
            if (existing.EffectiveFromUtc < (effectiveToUtc ?? DateTime.MaxValue) && effectiveFromUtc < (existing.EffectiveToUtc ?? DateTime.MaxValue))
                throw new InvalidOperationException($"Güvenlik tamponu profili çakışıyor: {existing.Id.ToString(CultureInfo.InvariantCulture)} numaralı profil ({existing.Period}) ile örtüşüyor; tarihleri değiştirin.");
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO SafetyBufferProfiles(Scope, ScopeId, Buffer, EffectiveFromUtc, EffectiveToUtc, Note, CreatedUtc) VALUES($s, $i, $b, $from, $to, $n, $t); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$s", cleanScope); cmd.Parameters.AddWithValue("$i", cleanId); cmd.Parameters.AddWithValue("$b", buffer);
        cmd.Parameters.AddWithValue("$from", T(effectiveFromUtc)); cmd.Parameters.AddWithValue("$to", effectiveToUtc is { } to ? T(to) : DBNull.Value); cmd.Parameters.AddWithValue("$n", cleanNote); cmd.Parameters.AddWithValue("$t", T(nowUtc));
        var id = (long)cmd.ExecuteScalar()!; tx.Commit();
        return new(id, cleanScope, cleanId, buffer, effectiveFromUtc, effectiveToUtc, cleanNote, nowUtc);
    }

    /// <summary>Profiles, newest first; a scope and an id narrow them.</summary>
    public IReadOnlyList<SafetyBufferProfile> List(string? scope = null, string? scopeId = null)
    {
        using var c = Open();
        return Read(c, null, scope is null ? null : scope.Trim().ToLowerInvariant(), scopeId?.Trim()).OrderByDescending(p => p.Id).ToList();
    }

    /// <summary>The buffer for a product on a shop at a moment: the product's profile, else the source's, else the store's, else the shop policy's safety stock.</summary>
    public SafetyBufferResolution Resolve(string productId, string? sourceId, string channel, string shop, int policySafetyStock, DateTime atUtc)
    {
        using var c = Open();
        var candidates = new (string Scope, string Id, string Origin, string Label)[]
        {
            (SafetyBufferProfile.ProductScope, productId ?? "", SafetyBufferResolution.Product, "ürün profili"),
            (SafetyBufferProfile.SourceScope, sourceId ?? "", SafetyBufferResolution.Source, "kaynak profili"),
            (SafetyBufferProfile.StoreScope, SafetyBufferProfile.StoreKey(channel, shop), SafetyBufferResolution.Store, "mağaza profili"),
        };
        foreach (var (scope, id, origin, label) in candidates)
        {
            if (id.Length == 0) continue;
            var hit = Read(c, null, scope, id).Where(p => p.Covers(atUtc)).OrderByDescending(p => p.Id).FirstOrDefault();
            if (hit is not null) return new(hit.Buffer, origin, hit, $"güvenlik tamponu {hit.Buffer.ToString(CultureInfo.InvariantCulture)} · {label} #{hit.Id.ToString(CultureInfo.InvariantCulture)} ({hit.Period})");
        }
        return new(policySafetyStock, SafetyBufferResolution.Policy, null, $"güvenlik tamponu {policySafetyStock.ToString(CultureInfo.InvariantCulture)} · mağaza politikası");
    }

    static List<SafetyBufferProfile> Read(SqliteConnection c, SqliteTransaction? tx, string? scope, string? scopeId)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT Id, Scope, ScopeId, Buffer, EffectiveFromUtc, EffectiveToUtc, Note, CreatedUtc FROM SafetyBufferProfiles WHERE ($s IS NULL OR Scope=$s) AND ($i IS NULL OR ScopeId=$i) ORDER BY Id";
        cmd.Parameters.AddWithValue("$s", (object?)scope ?? DBNull.Value); cmd.Parameters.AddWithValue("$i", (object?)scopeId ?? DBNull.Value);
        using var r = cmd.ExecuteReader(); var result = new List<SafetyBufferProfile>();
        while (r.Read()) result.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3), U(r.GetString(4)), r.IsDBNull(5) ? null : U(r.GetString(5)), r.GetString(6), U(r.GetString(7))));
        return result;
    }

    static string T(DateTime utc) => utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    static DateTime U(string text) => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}
