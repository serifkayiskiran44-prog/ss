using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace TrMarketplaceHubDesktop;

public enum ReportScope { Global, Store }
public enum ReportRunState { Never, Succeeded, Failed, Cancelled }

/// <param name="Sources">The local data the report reads, as words a person recognises ("Ürün havuzu, siparişler").</param>
/// <param name="Outputs">What the report produces: "Ekranda", "Excel", "CSV", "XML", "Zip".</param>
/// <param name="Route">The workspace that owns the report; the card opens it.</param>
public sealed record ReportDefinition(string Key, string Title, string Purpose, string Sources, ReportScope Scope, IReadOnlyList<string> Outputs, string Route);

public sealed record ReportRun(string ReportKey, string StoreKey, DateTime StartedUtc, DateTime FinishedUtc, ReportRunState State, int RowCount, string Note);

public sealed record ReportCard(string Key, string Title, string DisplayTitle, bool TitleTrimmed, string Purpose, string ScopeText, string LastRunText, ReportRunState LastRunState, string SavedFiltersText, string OutputsText, string Route, bool Visible)
{
    public string AccessibleName => $"{Title}: {LastRunText}; {ScopeText}; {OutputsText}";
}

public sealed record ReportCatalogView(IReadOnlyList<ReportCard> Cards, int Hidden, int Total)
{
    public bool IsEmpty => Cards.Count == 0;
    public string EmptyText => Total == 0 ? "Gösterilecek rapor yok." : Hidden == Total ? "Bu oturumda sunulan mağaza olmadığından mağaza kapsamlı raporlar gizli." : "Aramaya uyan rapor yok; arama metnini kısaltın.";
}

/// <summary>
/// The report catalog (#846): the reports this build really produces -- each a fixed definition with its purpose,
/// the local data it reads, whether it is store-scoped, and what it outputs -- described as cards carrying the
/// last real run (from <see cref="ReportRunStore"/>), the saved filter count and the output kinds. Store-scoped
/// reports are hidden when the shell offers no store; a run for a key outside the catalog is ignored; long titles
/// are trimmed for the card and kept whole for the tooltip and the accessible name.
/// </summary>
public static class ReportCatalog
{
    public const int TitleLimit = 60;
    public const string FilterModulePrefix = "report:";

    public static IReadOnlyList<ReportDefinition> Definitions { get; } = new ReportDefinition[]
    {
        new("products-xlsx", "Ürün havuzu Excel dışa aktarımı", "Filtreye uyan ürünleri seçili alanlarla Excel dosyasına yazar.", "Ürün havuzu", ReportScope.Global, new[] { "Excel" }, "excel"),
        new("products-xml", "Ürün havuzu XML dışa aktarımı", "Arama ve filtreye uyan ürünleri standart XML şablonuyla yazar.", "Ürün havuzu", ReportScope.Global, new[] { "XML" }, "products"),
        new("import-rejections", "Reddedilen içe aktarım satırları", "Excel önizlemesinin reddettiği satırları satır, neden kodu ve alan ile dışa aktarır.", "Excel önizlemesi", ReportScope.Global, new[] { "Excel", "CSV" }, "excel"),
        new("product-orders", "Ürün sipariş özeti", "Seçili ürünün sipariş sayısını, adedini ve son siparişini gösterir.", "Siparişler", ReportScope.Store, new[] { "Ekranda" }, "products"),
        new("orders", "Sipariş ve kargo listesi", "Sipariş kayıtlarını durum, aciliyet ve kargo bilgisiyle listeler.", "Siparişler, kargo", ReportScope.Store, new[] { "Ekranda" }, "orders"),
        new("orders-csv", "Sipariş listesi (CSV)", "Seçili mağazanın siparişlerini tarih aralığı ve teslimat durumuna göre CSV dosyasına yazar; müşteri alanı içermez.", "Siparişler", ReportScope.Store, new[] { "CSV" }, "reports"),
        new("listing-matrix", "Kanal yayın matrisi", "Ürün × mağaza yayın, mapping, sync ve bağlantı durumunu karşılaştırır.", "Ürün havuzu, kanal planları, sync, bağlantılar", ReportScope.Store, new[] { "Ekranda" }, "listing-matrix"),
        new("data-quality", "Veri kalite raporu", "Duplicate, zorunlu alan, fiyat/stok/döviz, URL ve kaynak bulgularını listeler.", "Ürün havuzu, kanal planları", ReportScope.Global, new[] { "Ekranda" }, "data-quality"),
        new("api-health", "API bağlantı sağlığı", "Auth, erişilebilirlik, rate-limit, kota ve son hata durumunu gösterir.", "Bağlantılar", ReportScope.Store, new[] { "Ekranda" }, "api-health"),
        new("support-package", "Güvenli destek paketi", "Sanitize edilmiş sağlık özeti ve audit kayıtlarını zip olarak yazar; secret içermez.", "Tanılama, audit", ReportScope.Global, new[] { "Zip" }, "diagnostics"),
    };

    public static ReportDefinition? Find(string? key) => Definitions.FirstOrDefault(d => string.Equals(d.Key, (key ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

    public static ReportCard Describe(ReportDefinition definition, ReportRun? latestRun, int savedFilters, IReadOnlyCollection<string>? allowedStoreKeys, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var title = definition.Title.Trim();
        var trimmed = title.Length > TitleLimit;
        var display = trimmed ? title[..(TitleLimit - 1)].TrimEnd() + "…" : title;
        var visible = definition.Scope == ReportScope.Global || allowedStoreKeys is null || allowedStoreKeys.Count > 0;
        var scope = definition.Scope == ReportScope.Global ? "Kapsam: tüm veri · " + definition.Sources
            : allowedStoreKeys is null ? "Kapsam: tüm mağazalar · " + definition.Sources
            : $"Kapsam: {allowedStoreKeys.Count:N0} sunulan mağaza · {definition.Sources}";
        var state = latestRun?.State ?? ReportRunState.Never;
        var lastRun = latestRun is null ? "Hiç çalıştırılmadı"
            : $"Son çalıştırma: {StatusTooltip.Relative(latestRun.FinishedUtc, nowUtc)} · {Word(latestRun.State)}" + (latestRun.State == ReportRunState.Succeeded ? $" · {latestRun.RowCount:N0} satır" : "");
        var filters = savedFilters <= 0 ? "Kayıtlı filtre yok" : $"{savedFilters:N0} kayıtlı filtre";
        var outputs = "Çıktı: " + string.Join(", ", definition.Outputs);
        return new(definition.Key, title, display, trimmed, definition.Purpose, scope, lastRun, state, filters, outputs, definition.Route, visible);
    }

    public static string Word(ReportRunState state) => state switch { ReportRunState.Succeeded => "başarılı", ReportRunState.Failed => "başarısız", ReportRunState.Cancelled => "iptal edildi", _ => "hiç çalıştırılmadı" };

    /// <summary>Cards for <paramref name="definitions"/>: runs keyed by report (unknown keys ignored), saved-filter counts, the shell's store keys, a search over title/purpose/sources.</summary>
    public static ReportCatalogView Build(IEnumerable<ReportDefinition> definitions, IReadOnlyDictionary<string, ReportRun>? latestRuns, IReadOnlyDictionary<string, int>? savedFilters, IReadOnlyCollection<string>? allowedStoreKeys, string? query, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var q = (query ?? "").Trim(); var cards = new List<ReportCard>(); var hidden = 0; var total = 0;
        foreach (var definition in definitions)
        {
            total++;
            ReportRun? latest = null; if (latestRuns is not null && latestRuns.TryGetValue(definition.Key, out var found)) latest = found;
            var count = savedFilters is not null && savedFilters.TryGetValue(definition.Key, out var n) ? n : 0;
            var card = Describe(definition, latest, count, allowedStoreKeys, nowUtc);
            if (!card.Visible) { hidden++; continue; }
            if (q.Length > 0 && !$"{card.Title} {card.Purpose} {definition.Sources}".ContainsFolded(q)) continue;
            cards.Add(card);
        }
        return new(cards, hidden, total);
    }

    /// <summary>The live catalog for a data directory: real runs and real saved filters.</summary>
    public static ReportCatalogView Load(string? directory, IReadOnlyCollection<string>? allowedStoreKeys, string? query, DateTime nowUtc)
    {
        var runs = new ReportRunStore(directory).Latest();
        var preferences = new UiPreferenceStore(directory);
        var filters = Definitions.ToDictionary(d => d.Key, d => preferences.ListViews(FilterModulePrefix + d.Key).Count, StringComparer.OrdinalIgnoreCase);
        return Build(Definitions, runs, filters, allowedStoreKeys, query, nowUtc);
    }
}

/// <summary>Where every report run lands (#846): key, store, start/finish, state, row count and a sanitized note -- never a path, never a payload.</summary>
public sealed class ReportRunStore
{
    readonly string connectionString;
    public ReportRunStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop"); Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "report_runs.db") }.ToString();
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "CREATE TABLE IF NOT EXISTS ReportRuns(Id INTEGER PRIMARY KEY AUTOINCREMENT,ReportKey TEXT NOT NULL,StoreKey TEXT NOT NULL,StartedUtc TEXT NOT NULL,FinishedUtc TEXT NOT NULL,State TEXT NOT NULL,RowCount INTEGER NOT NULL,Note TEXT NOT NULL);CREATE INDEX IF NOT EXISTS IX_ReportRuns_Key ON ReportRuns(ReportKey,FinishedUtc)"; cmd.ExecuteNonQuery();
    }
    SqliteConnection Open() => SqliteConnectionPolicy.Open(connectionString);

    public ReportRun Record(string reportKey, DateTime startedUtc, ReportRunState state, int rowCount, string? note = null, string? storeKey = null)
    {
        var key = (reportKey ?? "").Trim().ToLowerInvariant();
        if (key.Length is 0 or > 60 || key.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch == '-'))) throw new ArgumentException("Rapor anahtarı geçersiz.", nameof(reportKey));
        if (state == ReportRunState.Never) throw new ArgumentException("Bir çalıştırma 'hiç' olamaz.", nameof(state));
        var run = new ReportRun(key, (storeKey ?? "").Trim(), startedUtc, DateTime.UtcNow, state, Math.Max(0, rowCount), SafeNote(note));
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO ReportRuns(ReportKey,StoreKey,StartedUtc,FinishedUtc,State,RowCount,Note) VALUES($key,$store,$started,$finished,$state,$rows,$note)";
        cmd.Parameters.AddWithValue("$key", run.ReportKey); cmd.Parameters.AddWithValue("$store", run.StoreKey); cmd.Parameters.AddWithValue("$started", run.StartedUtc.ToString("O", CultureInfo.InvariantCulture)); cmd.Parameters.AddWithValue("$finished", run.FinishedUtc.ToString("O", CultureInfo.InvariantCulture)); cmd.Parameters.AddWithValue("$state", run.State.ToString()); cmd.Parameters.AddWithValue("$rows", run.RowCount); cmd.Parameters.AddWithValue("$note", run.Note);
        cmd.ExecuteNonQuery();
        return run;
    }

    /// <summary>The latest run per report key.</summary>
    public IReadOnlyDictionary<string, ReportRun> Latest()
    {
        var result = new Dictionary<string, ReportRun>(StringComparer.OrdinalIgnoreCase);
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT ReportKey,StoreKey,StartedUtc,FinishedUtc,State,RowCount,Note FROM ReportRuns ORDER BY FinishedUtc DESC, Id DESC";
        using var r = cmd.ExecuteReader();
        while (r.Read()) { var run = Read(r); if (!result.ContainsKey(run.ReportKey)) result[run.ReportKey] = run; }
        return result;
    }

    public IReadOnlyList<ReportRun> Recent(string reportKey, int limit = 20)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT ReportKey,StoreKey,StartedUtc,FinishedUtc,State,RowCount,Note FROM ReportRuns WHERE ReportKey=$key ORDER BY FinishedUtc DESC, Id DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$key", (reportKey ?? "").Trim().ToLowerInvariant()); cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        using var r = cmd.ExecuteReader(); var rows = new List<ReportRun>(); while (r.Read()) rows.Add(Read(r)); return rows;
    }

    static ReportRun Read(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), DateTime.Parse(r.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), DateTime.Parse(r.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), Enum.TryParse<ReportRunState>(r.GetString(4), out var state) ? state : ReportRunState.Failed, r.GetInt32(5), r.GetString(6));

    /// <summary>A note keeps words, never a location: text that is a path (a drive, a UNC root, a rooted or relative path) is reduced to its file name before the usual redaction; a slash inside a date or a sentence is left alone (an en-US short date is "8/14/2026").</summary>
    public static string SafeNote(string? note)
    {
        var text = (note ?? "").Trim();
        if (text.Length == 0) return "";
        if (LooksLikePath(text)) text = Path.GetFileName(text.TrimEnd('\\', '/'));
        return AuditStore.Sanitize(text.Length > 200 ? text[..200] : text);
    }
    static bool LooksLikePath(string text) => System.Text.RegularExpressions.Regex.IsMatch(text, @"^(?:[A-Za-z]:[\\/]|\\\\|/|~[\\/]|\.{1,2}[\\/])") || (text.Contains('\\') && !text.Contains(' '));
}
