using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TrMarketplaceHubDesktop.Catalog;

/// <summary>One recorded refresh of a scope's taxonomy content: the version (the same content is the same version), the content hash, when it was recorded and last confirmed, and the outcome — OK, or FAILED with the reason, in which case the version is the last good one.</summary>
public sealed record TaxonomySnapshot(string Scope, long Version, string Hash, DateTime RecordedUtc, DateTime CheckedUtc, string Outcome, string Error)
{
    public const string Ok = "OK", Failed = "FAILED";
    public bool IsOk => Outcome == Ok;
}

/// <summary>
/// Taxonomy snapshot versioning (#918). Every refresh of the taxonomy metadata — the local dictionary of categories,
/// attributes, brands, their aliases and rules; and, per channel and shop, the local content plus that channel's
/// mappings — is recorded as an immutable snapshot: a content hash, a version that moves only when the content
/// changed (an unchanged refresh confirms the current version and moves its check time), the moment, and the scope.
/// A refresh that cannot read its content is recorded as FAILED with the reason, and the current version stays the
/// last good one. A channel plan says which snapshot it was validated with; a plan validated with an older snapshot
/// than the current one is stale, and the quality scan says so. Nothing is fetched from a marketplace here: the
/// content is the operator's own dictionary, and the version is the proof of what was in it.
/// </summary>
public sealed class TaxonomySnapshotStore
{
    public const string LocalScope = "local";
    readonly string directory; readonly string connectionString;

    public TaxonomySnapshotStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop"); Directory.CreateDirectory(directory);
        this.directory = directory;
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "catalog.db") }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS TaxonomySnapshots(Id INTEGER PRIMARY KEY AUTOINCREMENT, Scope TEXT NOT NULL, Version INTEGER NOT NULL, Hash TEXT NOT NULL, RecordedUtc TEXT NOT NULL, CheckedUtc TEXT NOT NULL, Outcome TEXT NOT NULL, Error TEXT NOT NULL DEFAULT '');CREATE INDEX IF NOT EXISTS IX_TaxonomySnapshots_Scope ON TaxonomySnapshots(Scope,Id)";
        cmd.ExecuteNonQuery();
    }

    SqliteConnection Open() => SqliteConnectionPolicy.Open(connectionString);

    public static string ChannelScope(string marketplace, string shopId) => "channel:" + (marketplace ?? "").Trim().ToLowerInvariant() + "/" + (shopId ?? "").Trim();

    /// <summary>Refreshes a scope: the content read and hashed; the same hash confirms the current version (its check time moves), a new hash is the next version; a content that cannot be read is a FAILED record and the current version stays.</summary>
    public TaxonomySnapshot Refresh(string scope, DateTime nowUtc) => Refresh(scope, nowUtc, () => Content(scope));

    /// <summary>The same with the content reader supplied — for a caller that already has the content, and for the failed-refresh test.</summary>
    public TaxonomySnapshot Refresh(string scope, DateTime nowUtc, Func<string> contentReader)
    {
        ArgumentNullException.ThrowIfNull(contentReader);
        scope = (scope ?? "").Trim(); if (scope.Length == 0) throw new ArgumentException("Kapsam gerekli.", nameof(scope));
        string hash;
        try { hash = Hash(contentReader()); }
        catch (Exception error) when (error is not OutOfMemoryException) { return RecordFailure(scope, error.Message, nowUtc); }
        var current = Current(scope);
        if (current is not null && current.Hash == hash)
        {
            using var c = Open(); using var touch = c.CreateCommand();
            touch.CommandText = "UPDATE TaxonomySnapshots SET CheckedUtc=$checked WHERE Scope=$scope AND Version=$version AND Outcome=$ok"; touch.Parameters.AddWithValue("$checked", nowUtc.ToString("O", CultureInfo.InvariantCulture)); touch.Parameters.AddWithValue("$scope", scope); touch.Parameters.AddWithValue("$version", current.Version); touch.Parameters.AddWithValue("$ok", TaxonomySnapshot.Ok);
            touch.ExecuteNonQuery();
            return current with { CheckedUtc = nowUtc };
        }
        var version = (current?.Version ?? 0) + 1;
        Insert(scope, version, hash, nowUtc, TaxonomySnapshot.Ok, "");
        return new(scope, version, hash, nowUtc, nowUtc, TaxonomySnapshot.Ok, "");
    }

    /// <summary>Records a refresh that failed: the reason kept (redacted), the version the last good one.</summary>
    public TaxonomySnapshot RecordFailure(string scope, string? error, DateTime nowUtc)
    {
        var current = Current(scope); var reason = AuditStore.Sanitize(error ?? "bilinmeyen hata");
        Insert(scope, current?.Version ?? 0, current?.Hash ?? "", nowUtc, TaxonomySnapshot.Failed, reason);
        return new(scope, current?.Version ?? 0, current?.Hash ?? "", nowUtc, nowUtc, TaxonomySnapshot.Failed, reason);
    }

    /// <summary>The latest good snapshot of a scope — a failure never becomes the current version — or null when the scope was never refreshed successfully.</summary>
    public TaxonomySnapshot? Current(string scope)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Scope,Version,Hash,RecordedUtc,CheckedUtc,Outcome,Error FROM TaxonomySnapshots WHERE Scope=$scope AND Outcome=$ok ORDER BY Version DESC LIMIT 1"; cmd.Parameters.AddWithValue("$scope", (scope ?? "").Trim()); cmd.Parameters.AddWithValue("$ok", TaxonomySnapshot.Ok);
        using var r = cmd.ExecuteReader(); return r.Read() ? Read(r) : null;
    }

    /// <summary>Every record of a scope, newest first — failures included, so a refresh that could not read its content is visible beside the versions.</summary>
    public IReadOnlyList<TaxonomySnapshot> History(string scope, int limit = 50)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Scope,Version,Hash,RecordedUtc,CheckedUtc,Outcome,Error FROM TaxonomySnapshots WHERE Scope=$scope ORDER BY Id DESC LIMIT $limit"; cmd.Parameters.AddWithValue("$scope", (scope ?? "").Trim()); cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        using var r = cmd.ExecuteReader(); var rows = new List<TaxonomySnapshot>(); while (r.Read()) rows.Add(Read(r)); return rows;
    }

    /// <summary>The words for a status line: "taksonomi sürümü 3 · 5 dk önce" or "taksonomi sürümü yok".</summary>
    public static string Describe(TaxonomySnapshot? snapshot, DateTime nowUtc)
    {
        if (snapshot is null || snapshot.Version == 0) return "taksonomi sürümü yok";
        var span = nowUtc - snapshot.RecordedUtc; if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        var ago = span.TotalMinutes < 1 ? "az önce" : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} dk önce" : span.TotalDays < 1 ? $"{(int)span.TotalHours} sa önce" : $"{(int)span.TotalDays} gün önce";
        return $"taksonomi sürümü {snapshot.Version.ToString(CultureInfo.CurrentCulture)} · {ago}" + (snapshot.IsOk ? "" : " · son yenileme başarısız");
    }

    /// <summary>The content of a scope as one canonical text: the local dictionary (entries, aliases, rules, value aliases, brand aliases), and for a channel scope that plus the channel's mappings.</summary>
    string Content(string scope)
    {
        var taxonomy = new TaxonomyStore(directory); var lines = new List<string>();
        foreach (var kind in Enum.GetValues<TaxonomyKind>()) foreach (var e in taxonomy.List(kind)) lines.Add($"entry|{(int)kind}|{e.Id}|{e.Name}|{e.Value}|{(e.Active ? 1 : 0)}");
        foreach (var a in new TaxonomyAliasStore(directory).List()) lines.Add($"alias|{a.Key}|{a.LocalId}|{(a.Approved ? 1 : 0)}");
        foreach (var r in new CategoryAttributeRuleStore(directory).List()) lines.Add($"rule|{r.CategoryId}|{r.AttributeKey}|{(r.Required ? 1 : 0)}");
        foreach (var v in new AttributeValueMappingStore(directory).ListAliases()) lines.Add($"value|{v.AttributeKey}|{v.AliasKey}|{v.CanonicalValue}|{(v.Approved ? 1 : 0)}");
        foreach (var b in new BrandMappingStore(directory).ListAliases()) lines.Add($"brand|{b.Key}|{b.BrandId}|{(b.Approved ? 1 : 0)}");
        if (scope.StartsWith("channel:", StringComparison.Ordinal))
        {
            var cut = scope.IndexOf('/'); var marketplace = cut < 0 ? scope["channel:".Length..] : scope["channel:".Length..cut]; var shop = cut < 0 ? "" : scope[(cut + 1)..];
            foreach (var kind in Enum.GetValues<TaxonomyKind>())
            {
                var times = taxonomy.MappingTimes(kind, marketplace, shop);
                foreach (var m in taxonomy.Mappings(kind).Where(m => m.Marketplace == marketplace && m.ShopId == shop)) lines.Add($"map|{(int)kind}|{m.ExternalKey}|{m.LocalId}|{times.GetValueOrDefault(m.ExternalKey).ToString("O", CultureInfo.InvariantCulture)}");
            }
        }
        lines.Sort(StringComparer.Ordinal);
        return string.Join("\n", lines);
    }

    void Insert(string scope, long version, string hash, DateTime nowUtc, string outcome, string error)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO TaxonomySnapshots(Scope,Version,Hash,RecordedUtc,CheckedUtc,Outcome,Error) VALUES($scope,$version,$hash,$at,$at,$outcome,$error)";
        cmd.Parameters.AddWithValue("$scope", scope); cmd.Parameters.AddWithValue("$version", version); cmd.Parameters.AddWithValue("$hash", hash); cmd.Parameters.AddWithValue("$at", nowUtc.ToString("O", CultureInfo.InvariantCulture)); cmd.Parameters.AddWithValue("$outcome", outcome); cmd.Parameters.AddWithValue("$error", error);
        cmd.ExecuteNonQuery();
    }

    static TaxonomySnapshot Read(SqliteDataReader r) => new(r.GetString(0), r.GetInt64(1), r.GetString(2), ParseUtc(r.GetString(3)), ParseUtc(r.GetString(4)), r.GetString(5), r.GetString(6));
    static DateTime ParseUtc(string value) => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) ? date : DateTime.MinValue;
    static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content ?? "")))[..32];
}
