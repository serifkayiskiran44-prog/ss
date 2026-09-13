using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

/// <summary>
/// The part of a source that is configuration (#893): its address, schedule, mapping, price and stock rules,
/// filters and update flags — what an operator or a profile decides — as opposed to the state the runs write
/// (last run, last health, feed hashes, revisions applied). Two sources with the same configuration have the same
/// fingerprint whatever their state; a change to any of these fields is a new revision.
/// </summary>
public sealed class SourceConfig
{
    public string Name { get; set; } = ""; public string Location { get; set; } = ""; public bool Enabled { get; set; } = true; public int IntervalMinutes { get; set; } = 30; public bool AutoImport { get; set; }
    public string ItemPath { get; set; } = ""; public string DecimalSeparator { get; set; } = "."; public string NumberCultureName { get; set; } = "en-US"; public SortedDictionary<string, string> Fields { get; set; } = new(StringComparer.Ordinal);
    public string PriceMode { get; set; } = "Simple"; public string Formula { get; set; } = ""; public string CostCurrency { get; set; } = "TRY"; public string Currency { get; set; } = "USD"; public bool AutoFx { get; set; }
    public string FxKind { get; set; } = "ForexSelling"; public decimal TryPerTargetUnit { get; set; } public decimal ExchangeRate { get; set; } = 1; public decimal MarkupPercent { get; set; } = 40; public decimal FixedAmount { get; set; } public decimal MinimumPrice { get; set; }
    public int SafetyStock { get; set; } = 3; public int MinimumStock { get; set; } public int MaximumStock { get; set; } = 20; public string BrandFilter { get; set; } = ""; public string CategoryFilter { get; set; } = "";
    public bool UpdateName { get; set; } public bool UpdateDescription { get; set; } public bool UpdateImages { get; set; } public int MissingSourceGraceMinutes { get; set; } = 120;
    public int Priority { get; set; } = 100;
    public int SlaRefreshMinutes { get; set; } public int SlaGraceMinutes { get; set; }

    static readonly JsonSerializerOptions Canonical = new() { WriteIndented = false };

    public static SourceConfig Of(XmlSource s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new SourceConfig
        {
            Name = s.Name ?? "", Location = s.Location ?? "", Enabled = s.Enabled, IntervalMinutes = s.IntervalMinutes, AutoImport = s.AutoImport,
            ItemPath = s.ItemPath ?? "", DecimalSeparator = s.DecimalSeparator ?? ".", NumberCultureName = s.NumberCultureName ?? "", Fields = new SortedDictionary<string, string>(s.Fields ?? new Dictionary<string, string>(), StringComparer.Ordinal),
            PriceMode = s.PriceMode ?? "", Formula = s.Formula ?? "", CostCurrency = s.CostCurrency ?? "", Currency = s.Currency ?? "", AutoFx = s.AutoFx,
            FxKind = s.FxKind ?? "", TryPerTargetUnit = s.TryPerTargetUnit, ExchangeRate = s.ExchangeRate, MarkupPercent = s.MarkupPercent, FixedAmount = s.FixedAmount, MinimumPrice = s.MinimumPrice,
            SafetyStock = s.SafetyStock, MinimumStock = s.MinimumStock, MaximumStock = s.MaximumStock, BrandFilter = s.BrandFilter ?? "", CategoryFilter = s.CategoryFilter ?? "",
            UpdateName = s.UpdateName, UpdateDescription = s.UpdateDescription, UpdateImages = s.UpdateImages, MissingSourceGraceMinutes = s.MissingSourceGraceMinutes, Priority = s.Priority, SlaRefreshMinutes = s.SlaRefreshMinutes, SlaGraceMinutes = s.SlaGraceMinutes,
        };
    }

    /// <summary>Writes this configuration onto a source, leaving its identity and its state untouched.</summary>
    public void ApplyTo(XmlSource s)
    {
        ArgumentNullException.ThrowIfNull(s);
        s.Name = Name; s.Location = Location; s.Enabled = Enabled; s.IntervalMinutes = IntervalMinutes; s.AutoImport = AutoImport;
        s.ItemPath = ItemPath; s.DecimalSeparator = DecimalSeparator; s.NumberCultureName = NumberCultureName; s.Fields = new Dictionary<string, string>(Fields);
        s.PriceMode = PriceMode; s.Formula = Formula; s.CostCurrency = CostCurrency; s.Currency = Currency; s.AutoFx = AutoFx;
        s.FxKind = FxKind; s.TryPerTargetUnit = TryPerTargetUnit; s.ExchangeRate = ExchangeRate; s.MarkupPercent = MarkupPercent; s.FixedAmount = FixedAmount; s.MinimumPrice = MinimumPrice;
        s.SafetyStock = SafetyStock; s.MinimumStock = MinimumStock; s.MaximumStock = MaximumStock; s.BrandFilter = BrandFilter; s.CategoryFilter = CategoryFilter;
        s.UpdateName = UpdateName; s.UpdateDescription = UpdateDescription; s.UpdateImages = UpdateImages; s.MissingSourceGraceMinutes = MissingSourceGraceMinutes; s.Priority = Priority; s.SlaRefreshMinutes = SlaRefreshMinutes; s.SlaGraceMinutes = SlaGraceMinutes;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Canonical);
    public static SourceConfig? FromJson(string json) { try { return JsonSerializer.Deserialize<SourceConfig>(json, Canonical); } catch (JsonException) { return null; } }

    /// <summary>A stable fingerprint of the configuration: the canonical JSON (sorted mapping keys, no whitespace) hashed, so the order in which fields were entered never makes a revision.</summary>
    public string Fingerprint() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ToJson())))[..32];
}

/// <summary>One immutable revision of a source's configuration.</summary>
public sealed record SourceRevisionSnapshot(string SourceId, int Revision, string Fingerprint, DateTime CreatedUtc, string Reason, string Json)
{
    public SourceConfig? Config => SourceConfig.FromJson(Json);
}

/// <summary>A save that would overwrite a change made elsewhere since the record was loaded.</summary>
public sealed class SourceRevisionConflictException : InvalidOperationException
{
    public int LoadedRevision { get; } public int CurrentRevision { get; }
    public SourceRevisionConflictException(int loaded, int current) : base($"Kaynak başka bir yerde değiştirildi (yüklenen rev. {loaded.ToString(CultureInfo.CurrentCulture)}, güncel rev. {current.ToString(CultureInfo.CurrentCulture)}); yeniden yükleyip değişikliği tekrar yapın. Hiçbir şey üzerine yazılmadı.") { LoadedRevision = loaded; CurrentRevision = current; }
}

/// <summary>
/// The source revision ledger (#893): every change to a source's configuration — its address, mapping, rules,
/// profile — is one immutable, numbered snapshot in the catalogue database, written in the same transaction as
/// the source itself. A save that changes only state appends nothing; a save that changes configuration appends
/// the next revision and stamps the source with it; a save whose loaded revision is no longer the current one is
/// a concurrent edit and is refused before anything is written. A rollback never rewrites history: restoring
/// revision N puts N's configuration on the source, and the save that follows appends revision N+k with the
/// reason naming N. Runs record the revision they ran under, so an import can always be read against the
/// configuration that produced it.
/// </summary>
public static class SourceRevisionStore
{
    public const string SaveReason = "kaydet";
    public const int JsonLimit = 64 * 1024;

    public static void EnsureTable(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS SourceRevisions(SourceId TEXT NOT NULL, Revision INTEGER NOT NULL, Fingerprint TEXT NOT NULL, CreatedUtc TEXT NOT NULL, Reason TEXT NOT NULL, Json TEXT NOT NULL, PRIMARY KEY(SourceId, Revision))";
        command.ExecuteNonQuery();
    }

    /// <summary>The latest revision number for a source, 0 when none.</summary>
    public static int Latest(SqliteConnection connection, string sourceId, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(Revision),0) FROM SourceRevisions WHERE SourceId=$id"; command.Parameters.AddWithValue("$id", sourceId);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Appends the next revision for the configuration when it differs from the latest one; returns the revision the source is at afterwards (the latest, when nothing changed).</summary>
    public static int Append(SqliteConnection connection, SqliteTransaction? transaction, XmlSource source, string reason, DateTime? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(connection); ArgumentNullException.ThrowIfNull(source);
        var config = SourceConfig.Of(source); var fingerprint = config.Fingerprint(); var json = config.ToJson();
        if (json.Length > JsonLimit) throw new InvalidOperationException("Kaynak yapılandırması sürüm kaydı için fazla büyük.");
        var latest = Latest(connection, source.Id, transaction);
        if (latest > 0)
        {
            using var same = connection.CreateCommand(); same.Transaction = transaction;
            same.CommandText = "SELECT Fingerprint FROM SourceRevisions WHERE SourceId=$id AND Revision=$rev"; same.Parameters.AddWithValue("$id", source.Id); same.Parameters.AddWithValue("$rev", latest);
            if (same.ExecuteScalar() is string current && string.Equals(current, fingerprint, StringComparison.Ordinal)) return latest;
        }
        var next = latest + 1;
        using var insert = connection.CreateCommand(); insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO SourceRevisions(SourceId,Revision,Fingerprint,CreatedUtc,Reason,Json) VALUES($id,$rev,$fp,$at,$reason,$json)";
        insert.Parameters.AddWithValue("$id", source.Id); insert.Parameters.AddWithValue("$rev", next); insert.Parameters.AddWithValue("$fp", fingerprint);
        insert.Parameters.AddWithValue("$at", (nowUtc ?? DateTime.UtcNow).ToString("O", CultureInfo.InvariantCulture)); insert.Parameters.AddWithValue("$reason", SafeReason(reason)); insert.Parameters.AddWithValue("$json", json);
        insert.ExecuteNonQuery();
        return next;
    }

    public static IReadOnlyList<SourceRevisionSnapshot> List(SqliteConnection connection, string sourceId, int limit = 200)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT SourceId,Revision,Fingerprint,CreatedUtc,Reason,Json FROM SourceRevisions WHERE SourceId=$id ORDER BY Revision DESC LIMIT $limit"; command.Parameters.AddWithValue("$id", sourceId); command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        using var reader = command.ExecuteReader(); var result = new List<SourceRevisionSnapshot>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), reader.GetString(4), reader.GetString(5)));
        return result;
    }

    public static SourceRevisionSnapshot? Get(SqliteConnection connection, string sourceId, int revision) => List(connection, sourceId, 1000).FirstOrDefault(r => r.Revision == revision);

    /// <summary>The reason as a short, sanitized word or phrase — a label the ledger shows, never a payload.</summary>
    public static string SafeReason(string? reason)
    {
        var clean = AuditStore.Redact(reason ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (clean.Length == 0) return SaveReason;
        return clean.Length <= 80 ? clean : clean[..80];
    }
}
