using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;

namespace TrMarketplaceHubDesktop.Catalog;

/// <summary>One anomaly decision explained: what triggered it, against which baseline and thresholds, how old the source's last successful read was, under which correlation id. Counts, percentages, hours and ids only — never a product, never an address.</summary>
public sealed record AnomalyDiagnostic(long Id, string SourceId, string CorrelationId, string Decision, IReadOnlyList<string> Reasons, int CurrentCount, int CurrentZeroStock, decimal CurrentAveragePrice, int? BaselineCount, int? BaselineZeroStock, decimal? BaselineAveragePrice, decimal CountDeltaPercent, decimal ZeroStockPercent, decimal PriceDeltaPercent, decimal MaxCountDeltaPercent, decimal MaxZeroStockPercent, decimal MaxPriceDeltaPercent, TimeSpan? SourceAge, DateTime DecidedUtc, string Words)
{
    public const string Blocked = "BLOCKED", Warn = "WARN";
}

/// <summary>
/// Anomaly explanation details (#939). The dropship anomaly gate used to throw a reason code and forget everything
/// else; now every block — and every near miss — is explained and kept: the triggering values (product count, zero
/// stock count, average price), the baseline they were judged against (the source's own products before the run),
/// the thresholds in force, how old the source's last successful read was, and the correlation id of the run, so an
/// operator can read back why an import stopped and a support case can be matched to its run. A near miss is a
/// delta at or beyond eight tenths of its threshold with nothing blocked: recorded as WARN, the import proceeds.
/// Safe by construction: the words carry counts, percentages, hours, a source id and a run id — no product, no
/// price of a product, no feed address, no credential — and are redacted once more before they are written.
/// </summary>
public static class AnomalyDiagnostics
{
    public const decimal WarnShare = 0.8m;

    /// <summary>The diagnostic for a gate decision, or null when nothing blocked and nothing came close.</summary>
    public static AnomalyDiagnostic? Explain(FeedAnomalyReport report, AnomalyProfile profile, DateTime? lastSuccessfulReadUtc, string? correlationId, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(report); ArgumentNullException.ThrowIfNull(profile);
        var current = report.Current; var previous = report.Previous;
        var countDelta = previous is null ? 0m : previous.ProductCount == 0 ? 100m : Math.Abs((current.ProductCount - previous.ProductCount) * 100m / previous.ProductCount);
        var zero = previous is null ? 0m : current.ProductCount == 0 ? 100m : current.ZeroStockCount * 100m / current.ProductCount;
        var priceDelta = previous is null || previous.AveragePrice == 0 ? 0m : Math.Abs((current.AveragePrice - previous.AveragePrice) * 100m / previous.AveragePrice);
        var near = previous is not null && (countDelta >= profile.MaxCountDeltaPercent * WarnShare || zero >= profile.MaxZeroStockPercent * WarnShare || priceDelta >= profile.MaxPriceDeltaPercent * WarnShare);
        var decision = report.ApplyBlocked ? AnomalyDiagnostic.Blocked : near ? AnomalyDiagnostic.Warn : null;
        if (decision is null) return null;
        TimeSpan? age = lastSuccessfulReadUtc is { } read ? (nowUtc - read < TimeSpan.Zero ? TimeSpan.Zero : nowUtc - read) : null;
        var cid = AuditStore.Redact((correlationId ?? "").Trim());
        var parts = new List<string>();
        if (report.ApplyBlocked)
            foreach (var reason in report.Reasons.Distinct())
                parts.Add(reason switch
                {
                    "PRODUCT_COUNT_SPIKE" or "MISSING_PRODUCTS" => $"{reason}: ürün sayısı {N(previous?.ProductCount ?? 0)} → {N(current.ProductCount)} (%{P(countDelta)} fark, sınır %{P(profile.MaxCountDeltaPercent)})",
                    "MASS_ZERO_STOCK" => $"{reason}: sıfır stoklu ürün %{P(zero)} ({N(current.ZeroStockCount)}/{N(current.ProductCount)}; sınır %{P(profile.MaxZeroStockPercent)})",
                    "PRICE_JUMP" => $"{reason}: ortalama fiyat {M(previous?.AveragePrice ?? 0)} → {M(current.AveragePrice)} (%{P(priceDelta)} fark, sınır %{P(profile.MaxPriceDeltaPercent)})",
                    "MASS_TAXONOMY_CHANGE" => $"{reason}: {N(current.CategoryChanges + current.BrandChanges)} kategori/marka değişikliği (sınır {N(profile.MaxTaxonomyChanges)})",
                    _ => reason,
                });
        else
        {
            if (countDelta >= profile.MaxCountDeltaPercent * WarnShare) parts.Add($"ürün sayısı {N(previous!.ProductCount)} → {N(current.ProductCount)} (%{P(countDelta)} fark, sınır %{P(profile.MaxCountDeltaPercent)}'e yakın)");
            if (zero >= profile.MaxZeroStockPercent * WarnShare) parts.Add($"sıfır stoklu ürün %{P(zero)} (sınır %{P(profile.MaxZeroStockPercent)}'e yakın)");
            if (priceDelta >= profile.MaxPriceDeltaPercent * WarnShare) parts.Add($"ortalama fiyat {M(previous!.AveragePrice)} → {M(current.AveragePrice)} (%{P(priceDelta)} fark, sınır %{P(profile.MaxPriceDeltaPercent)}'e yakın)");
        }
        var words = (decision == AnomalyDiagnostic.Blocked ? "engellendi: " : "uyarı (içe aktarım sürdü): ") + string.Join("; ", parts)
            + (age is { } a ? $" · kaynak yaşı {Hours(a)}" : " · kaynak hiç okunmamış") + (cid.Length > 0 ? $" · korelasyon {cid}" : "");
        return new(0, report.Supplier, cid, decision, report.ApplyBlocked ? report.Reasons.Distinct().ToList() : Array.Empty<string>(), current.ProductCount, current.ZeroStockCount, current.AveragePrice, previous?.ProductCount, previous?.ZeroStockCount, previous?.AveragePrice, decimal.Round(countDelta, 2), decimal.Round(zero, 2), decimal.Round(priceDelta, 2), profile.MaxCountDeltaPercent, profile.MaxZeroStockPercent, profile.MaxPriceDeltaPercent, age, DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), AuditStore.Redact(words));
    }

    static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
    static string P(decimal value) => decimal.Round(value, 1).ToString("0.#", CultureInfo.InvariantCulture);
    static string M(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    static string Hours(TimeSpan span) => span.TotalHours < 1 ? $"{(int)span.TotalMinutes} dk" : span.TotalDays < 2 ? $"{(int)span.TotalHours} sa" : $"{(int)span.TotalDays} gün";
}

/// <summary>The diagnostics, in catalog.db beside the products; newest first; a source narrows them.</summary>
public sealed class AnomalyDiagnosticStore
{
    readonly string connectionString;

    public AnomalyDiagnosticStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "catalog.db") }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS AnomalyDiagnostics(Id INTEGER PRIMARY KEY AUTOINCREMENT, SourceId TEXT NOT NULL, CorrelationId TEXT NOT NULL DEFAULT '', Decision TEXT NOT NULL, Reasons TEXT NOT NULL DEFAULT '', CurrentCount INTEGER NOT NULL, CurrentZeroStock INTEGER NOT NULL, CurrentAveragePrice TEXT NOT NULL, BaselineCount INTEGER NULL, BaselineZeroStock INTEGER NULL, BaselineAveragePrice TEXT NULL, CountDeltaPercent TEXT NOT NULL, ZeroStockPercent TEXT NOT NULL, PriceDeltaPercent TEXT NOT NULL, MaxCountDeltaPercent TEXT NOT NULL, MaxZeroStockPercent TEXT NOT NULL, MaxPriceDeltaPercent TEXT NOT NULL, SourceAgeSeconds INTEGER NULL, DecidedUtc TEXT NOT NULL, Words TEXT NOT NULL); CREATE INDEX IF NOT EXISTS IX_AnomalyDiagnostics_Source ON AnomalyDiagnostics(SourceId, Id)";
        cmd.ExecuteNonQuery();
    }

    SqliteConnection Open() => SqliteConnectionPolicy.Open(connectionString);

    public AnomalyDiagnostic Record(AnomalyDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO AnomalyDiagnostics(SourceId, CorrelationId, Decision, Reasons, CurrentCount, CurrentZeroStock, CurrentAveragePrice, BaselineCount, BaselineZeroStock, BaselineAveragePrice, CountDeltaPercent, ZeroStockPercent, PriceDeltaPercent, MaxCountDeltaPercent, MaxZeroStockPercent, MaxPriceDeltaPercent, SourceAgeSeconds, DecidedUtc, Words) VALUES($s, $cid, $d, $r, $cc, $cz, $ca, $bc, $bz, $ba, $dc, $dz, $dp, $mc, $mz, $mp, $age, $t, $w); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$s", diagnostic.SourceId); cmd.Parameters.AddWithValue("$cid", diagnostic.CorrelationId); cmd.Parameters.AddWithValue("$d", diagnostic.Decision); cmd.Parameters.AddWithValue("$r", string.Join(",", diagnostic.Reasons));
        cmd.Parameters.AddWithValue("$cc", diagnostic.CurrentCount); cmd.Parameters.AddWithValue("$cz", diagnostic.CurrentZeroStock); cmd.Parameters.AddWithValue("$ca", D(diagnostic.CurrentAveragePrice));
        cmd.Parameters.AddWithValue("$bc", diagnostic.BaselineCount is { } bc ? bc : DBNull.Value); cmd.Parameters.AddWithValue("$bz", diagnostic.BaselineZeroStock is { } bz ? bz : DBNull.Value); cmd.Parameters.AddWithValue("$ba", diagnostic.BaselineAveragePrice is { } ba ? D(ba) : DBNull.Value);
        cmd.Parameters.AddWithValue("$dc", D(diagnostic.CountDeltaPercent)); cmd.Parameters.AddWithValue("$dz", D(diagnostic.ZeroStockPercent)); cmd.Parameters.AddWithValue("$dp", D(diagnostic.PriceDeltaPercent));
        cmd.Parameters.AddWithValue("$mc", D(diagnostic.MaxCountDeltaPercent)); cmd.Parameters.AddWithValue("$mz", D(diagnostic.MaxZeroStockPercent)); cmd.Parameters.AddWithValue("$mp", D(diagnostic.MaxPriceDeltaPercent));
        cmd.Parameters.AddWithValue("$age", diagnostic.SourceAge is { } age ? (long)age.TotalSeconds : DBNull.Value); cmd.Parameters.AddWithValue("$t", diagnostic.DecidedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)); cmd.Parameters.AddWithValue("$w", AuditStore.Redact(diagnostic.Words));
        var id = (long)cmd.ExecuteScalar()!;
        return diagnostic with { Id = id };
    }

    public IReadOnlyList<AnomalyDiagnostic> List(string? sourceId = null, int limit = 100)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT Id, SourceId, CorrelationId, Decision, Reasons, CurrentCount, CurrentZeroStock, CurrentAveragePrice, BaselineCount, BaselineZeroStock, BaselineAveragePrice, CountDeltaPercent, ZeroStockPercent, PriceDeltaPercent, MaxCountDeltaPercent, MaxZeroStockPercent, MaxPriceDeltaPercent, SourceAgeSeconds, DecidedUtc, Words FROM AnomalyDiagnostics WHERE ($s IS NULL OR SourceId=$s) ORDER BY Id DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$s", (object?)sourceId ?? DBNull.Value); cmd.Parameters.AddWithValue("$n", Math.Max(1, limit));
        using var r = cmd.ExecuteReader(); var result = new List<AnomalyDiagnostic>();
        while (r.Read())
            result.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4).Split(',', StringSplitOptions.RemoveEmptyEntries), r.GetInt32(5), r.GetInt32(6), Dec(r.GetString(7)), r.IsDBNull(8) ? null : r.GetInt32(8), r.IsDBNull(9) ? null : r.GetInt32(9), r.IsDBNull(10) ? null : Dec(r.GetString(10)), Dec(r.GetString(11)), Dec(r.GetString(12)), Dec(r.GetString(13)), Dec(r.GetString(14)), Dec(r.GetString(15)), Dec(r.GetString(16)), r.IsDBNull(17) ? null : TimeSpan.FromSeconds(r.GetInt64(17)), DateTime.Parse(r.GetString(18), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime(), r.GetString(19)));
        return result;
    }

    public AnomalyDiagnostic? Latest(string sourceId) => List(sourceId, 1).FirstOrDefault();

    static string D(decimal value) => value.ToString(CultureInfo.InvariantCulture);
    static decimal Dec(string text) => decimal.Parse(text, CultureInfo.InvariantCulture);
}

public partial class CatalogStore
{
    /// <summary>#939: the anomaly diagnostics kept for a source (or every source), newest first.</summary>
    public IReadOnlyList<AnomalyDiagnostic> AnomalyDiagnosticsFor(string? sourceId = null, int limit = 100) => new AnomalyDiagnosticStore(dataDirectory).List(sourceId, limit);
}
