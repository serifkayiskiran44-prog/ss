using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using System.Xml.XPath;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public enum MigrationSourceFormat { Unknown, Excel, Csv, Json, Xml }
public sealed record MigrationStoreMetadata(string Channel, string ShopId, string DisplayName, bool Enabled = true);
public sealed class MigrationPreviewLine
{
    public int RowNumber { get; init; }
    public string Action { get; init; } = "ERROR";
    public string ExistingId { get; init; } = "";
    public DateTime ExistingUpdatedUtc { get; init; }
    public CatalogProduct? Product { get; init; }
    public string Error { get; init; } = "";
}
public sealed class MigrationPreview
{
    public string SourcePath { get; init; } = "";
    public MigrationSourceFormat Format { get; init; }
    public IReadOnlyList<MigrationPreviewLine> Lines { get; init; } = [];
    public IReadOnlyList<MigrationStoreMetadata> Stores { get; init; } = [];
    public IReadOnlyList<string> IgnoredSecretFields { get; init; } = [];
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    /// Immutable identity for this specific approved operation - generated once
    /// here and reused across every Apply(...) retry against the same preview,
    /// so Apply can recognize "this exact operation" across a crash/restart or a
    /// concurrent second caller instead of re-running its side effects. See #2653.
    public string OperationId { get; init; } = Guid.NewGuid().ToString("N");
    public IReadOnlyList<CatalogProduct> ReadyRows => Lines.Where(x => x.Product is not null && (x.Action is "CREATE" or "UPDATE")).Select(x => x.Product!).ToList();
    public IReadOnlyList<string> Errors => Lines.Where(x => x.Action == "ERROR").Select(x => x.Error).ToList();
}
public sealed record MigrationApplyResult(string JournalId, int Created, int Updated, int Skipped, int TaxonomyAdded, int StoresAdded, string BackupPath);
public sealed record MigrationJournalRecord(string Id, string SourceFileName, DateTime CreatedUtc, string Status, string ReceiptJson, string BackupPath, string OperationId, string Phase, string TaxonomyAddedJson, string StoreAddedJson, string ResultJson);
/// Bounded record of one taxonomy entry this migration operation itself created
/// - never a pre-existing one - so Undo can safely target only what this
/// operation owns.
public sealed record TaxonomyAddedItem(TaxonomyKind Kind, string Id, string Name);
/// Bounded record of one marketplace-store metadata row this migration
/// operation itself created (not an upsert of an already-existing row).
public sealed record StoreAddedItem(string Id, string Channel, string ShopId);

public sealed class MigrationAssistantService
{
    readonly string? directory;
    public MigrationAssistantService(string? directory = null) => this.directory = directory;

    public MigrationPreview Preview(string path, string sourceId = "migration", CultureInfo? culture = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("İçe aktarılacak dosya bulunamadı.", path);
        path = Path.GetFullPath(path); sourceId = string.IsNullOrWhiteSpace(sourceId) ? "migration" : sourceId.Trim(); culture ??= CultureInfo.CurrentCulture;
        var parsed = Read(path, sourceId, culture);
        var existing = new CatalogStore(directory).Products();
        var bySku = existing.Where(x => x.Sku.Length > 0).GroupBy(x => x.Sku, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Single(), StringComparer.OrdinalIgnoreCase);
        var byBarcode = existing.Where(x => x.Barcode.Length > 0).GroupBy(x => x.Barcode, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Single(), StringComparer.OrdinalIgnoreCase);
        var lines = new List<MigrationPreviewLine>(); var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var row = 1;
        foreach (var item in parsed.Rows)
        {
            row++; var validation = ValidateRow(item); var identity = item.Sku.Length > 0 ? "sku:" + item.Sku : item.Barcode.Length > 0 ? "barcode:" + item.Barcode : "row:" + row;
            if (!used.Add(identity)) validation ??= "Dosyada aynı SKU veya barkod birden fazla satırda var.";
            if (validation is not null) { lines.Add(new() { RowNumber = row, Error = validation }); continue; }
            var match = bySku.GetValueOrDefault(item.Sku); if (match is null && item.Barcode.Length > 0) match = byBarcode.GetValueOrDefault(item.Barcode);
            if (match is null) lines.Add(new() { RowNumber = row, Action = "CREATE", Product = item });
            else lines.Add(new() { RowNumber = row, Action = Equivalent(match, item) ? "SKIP" : "UPDATE", ExistingId = match.Id, ExistingUpdatedUtc = match.UpdatedUtc, Product = item });
        }
        lines.AddRange(parsed.Errors.Select(error => new MigrationPreviewLine { RowNumber = ++row, Error = error }));
        return new() { SourcePath = path, Format = parsed.Format, Lines = lines, Stores = parsed.Stores, IgnoredSecretFields = parsed.SecretFields, CreatedUtc = DateTime.UtcNow };
    }

    /// Crash-safe, idempotent, resumable unit-of-work: catalog mutation -> durable
    /// journal checkpoint -> taxonomy phase -> store-metadata phase -> Completed.
    /// Each phase is safe to re-run (catalog mutation matches by Sku/Barcode so a
    /// re-apply merges rather than duplicates; taxonomy/store phases skip anything
    /// already present) and the operation is keyed by preview.OperationId so a
    /// retry against the SAME preview after a crash - or a second concurrent
    /// caller - resumes/short-circuits instead of re-running side effects. See #2653.
    public MigrationApplyResult Apply(MigrationPreview preview, bool approved, string backupPath = "")
    {
        if (!approved) throw new InvalidOperationException("Veri geçişi için önizleme onayı gerekli.");
        if (preview.Errors.Count > 0) throw new InvalidOperationException("Hatalı satırlar düzeltilmeden veri geçişi uygulanamaz.");
        var journal = new MigrationJournalStore(directory);
        var existing = journal.FindByOperation(preview.OperationId);
        if (existing is not null)
        {
            if (existing.Status == "Undone") throw new InvalidOperationException("Bu geçiş zaten geri alınmış; aynı önizleme tekrar uygulanamaz.");
            if (existing.Phase == MigrationJournalStore.PhaseCompleted) return JsonSerializer.Deserialize<MigrationApplyResult>(existing.ResultJson) ?? throw new InvalidDataException("Geçiş sonucu bozuk.");
        }

        var catalog = new CatalogStore(directory); CatalogUndoReceipt receipt; string journalId;
        if (existing is null)
        {
            var current = catalog.Products().ToDictionary(x => x.Id);
            foreach (var line in preview.Lines.Where(x => x.Action == "UPDATE")) if (!current.TryGetValue(line.ExistingId, out var now) || now.UpdatedUtc != line.ExistingUpdatedUtc) throw new InvalidOperationException("Önizleme sonrası mevcut veri değişti; yeniden önizleme oluşturun.");
            receipt = catalog.ApplyMigration(preview.ReadyRows, "migration:" + preview.OperationId);
            journalId = journal.InsertCatalogPhase(preview.OperationId, Path.GetFileName(preview.SourcePath), receipt, backupPath);
            // Re-read the canonical row: on a same-operation race the INSERT above
            // may have lost to a concurrent caller's row (UNIQUE OperationId), in
            // which case the receipt that is actually durable is theirs, not ours.
            existing = journal.FindByOperation(preview.OperationId)!;
            receipt = JsonSerializer.Deserialize<CatalogUndoReceipt>(existing.ReceiptJson) ?? throw new InvalidDataException("Geçiş günlüğü bozuk.");
        }
        else { journalId = existing.Id; receipt = JsonSerializer.Deserialize<CatalogUndoReceipt>(existing.ReceiptJson) ?? throw new InvalidDataException("Geçiş günlüğü bozuk."); }
        _ = receipt;

        var rows = preview.ReadyRows;
        var taxonomyAdded = existing.Phase is MigrationJournalStore.PhaseTaxonomyApplied or MigrationJournalStore.PhaseCompleted
            ? JsonSerializer.Deserialize<List<TaxonomyAddedItem>>(existing.TaxonomyAddedJson) ?? []
            : ApplyTaxonomy(rows, new TaxonomyStore(directory), journal, journalId);
        var storesAdded = ApplyStores(preview.Stores, new MarketplaceConnectionStore(directory), journal, journalId);
        var result = new MigrationApplyResult(journalId, preview.Lines.Count(x => x.Action == "CREATE"), preview.Lines.Count(x => x.Action == "UPDATE"), preview.Lines.Count(x => x.Action == "SKIP"), taxonomyAdded.Count, storesAdded.Count, backupPath);
        journal.Complete(journalId, result);
        return result;
    }

    /// Runs to completion or not at all as far as Phase is concerned: whatever it
    /// manages to add before a failure is still durably recorded (finally) for
    /// accurate Undo scoping, but Phase only advances past CatalogApplied once the
    /// whole loop succeeds - a partial run is safely re-driven from scratch by the
    /// next Apply() call (each check is itself idempotent).
    static List<TaxonomyAddedItem> ApplyTaxonomy(IReadOnlyList<CatalogProduct> rows, TaxonomyStore taxonomy, MigrationJournalStore journal, string journalId)
    {
        var added = new List<TaxonomyAddedItem>(); var merged = new List<TaxonomyAddedItem>();
        try
        {
            foreach (var name in rows.Select(x => x.Brand).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                if (!taxonomy.List(TaxonomyKind.Brand).Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                { var entry = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = name, Value = name }); added.Add(new(TaxonomyKind.Brand, entry.Id, entry.Name)); }
            foreach (var name in rows.Select(x => x.Category).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                if (!taxonomy.List(TaxonomyKind.Category).Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                { var entry = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = name, Value = name }); added.Add(new(TaxonomyKind.Category, entry.Id, entry.Name)); }
        }
        finally { merged = journal.AppendTaxonomyAdded(journalId, added); }
        journal.AdvanceToTaxonomyPhase(journalId);
        return merged;
    }

    /// Counts only rows this call itself creates (checked against a pre-loop
    /// snapshot) - re-saving an already-existing channel+shop is an idempotent
    /// upsert, never counted as a new addition on retry. See #2653 (storesAdded
    /// must not double-count on retry/upsert).
    static List<StoreAddedItem> ApplyStores(IReadOnlyList<MigrationStoreMetadata> stores, MarketplaceConnectionStore store, MigrationJournalStore journal, string journalId)
    {
        var existingKeys = store.List(false).Select(x => (x.Channel, ShopId: x.ShopId)).ToHashSet();
        var added = new List<StoreAddedItem>();
        try
        {
            foreach (var metadata in stores)
            {
                var normalizedChannel = MarketplaceConnectionCatalog.Get(metadata.Channel).Id;
                var wasNew = !existingKeys.Contains((normalizedChannel, metadata.ShopId.Trim()));
                var saved = store.Save(metadata.Channel, metadata.ShopId, metadata.DisplayName, metadata.Enabled);
                if (wasNew) added.Add(new(saved.Id, saved.Channel, saved.ShopId));
            }
        }
        finally { added = journal.AppendStoreAdded(journalId, added); }
        return added;
    }

    public void Undo(string journalId) => new MigrationJournalStore(directory).Undo(journalId, new CatalogStore(directory), new TaxonomyStore(directory), new MarketplaceConnectionStore(directory));

    sealed record Parsed(MigrationSourceFormat Format, IReadOnlyList<CatalogProduct> Rows, IReadOnlyList<string> Errors, IReadOnlyList<MigrationStoreMetadata> Stores, IReadOnlyList<string> SecretFields);
    Parsed Read(string path, string sourceId, CultureInfo culture)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch { ".xlsx" or ".xlsm" => ReadExcel(path, sourceId, culture), ".csv" or ".tsv" => ReadCsv(path, sourceId, culture), ".json" => ReadJson(path, sourceId, culture), ".xml" => ReadXml(path, sourceId), _ => throw new InvalidDataException("Desteklenen veri geçişi biçimleri: XLSX, CSV, TSV, JSON ve XML.") };
    }
    static Parsed ReadExcel(string path, string sourceId, CultureInfo culture)
    {
        var preview = CatalogExcel.Preview(path, culture); return new(MigrationSourceFormat.Excel, preview.Rows.Select(x => { x.SourceId = sourceId; return x; }).ToList(), preview.Errors, [], []);
    }
    static Parsed ReadCsv(string path, string sourceId, CultureInfo culture)
    {
        var lines = File.ReadLines(path).Take(100_002).ToList(); if (lines.Count == 0) return new(MigrationSourceFormat.Csv, [], ["CSV boş."], [], []); var delimiter = Path.GetExtension(path).Equals(".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : DetectDelimiter(lines[0]); var headers = ParseCsvLine(lines[0], delimiter); var rows = new List<CatalogProduct>(); var errors = new List<string>(); var secrets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var map = headers.Select((value, index) => (Key: KnownField(value), Index: index)).Where(x => x.Key.Length > 0).ToDictionary(x => x.Key, x => x.Index, StringComparer.OrdinalIgnoreCase); foreach (var header in headers.Where(IsSecretField)) secrets.Add(header);
        for (var i = 1; i < lines.Count; i++) { var values = ParseCsvLine(lines[i], delimiter); var row = ParseProduct(map, values, sourceId, culture, out var error); if (row is null) errors.Add($"Satır {i + 1}: {error}"); else rows.Add(row); }
        return new(MigrationSourceFormat.Csv, rows, errors, [], secrets.ToList());
    }
    static Parsed ReadJson(string path, string sourceId, CultureInfo culture)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path)); var root = document.RootElement; var values = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : root.TryGetProperty("products", out var products) && products.ValueKind == JsonValueKind.Array ? products.EnumerateArray().ToList() : throw new InvalidDataException("JSON bir ürün dizisi veya products dizisi içermeli."); var rows = new List<CatalogProduct>(); var errors = new List<string>(); var secrets = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var stores = new List<MigrationStoreMetadata>(); var row = 0;
        foreach (var value in values) { row++; if (value.ValueKind != JsonValueKind.Object) { errors.Add($"Satır {row}: JSON nesne olmalı."); continue; } foreach (var property in value.EnumerateObject().Where(x => IsSecretField(x.Name))) secrets.Add(property.Name); if (value.TryGetProperty("marketplace", out var market) && value.TryGetProperty("shopId", out var shop) && value.TryGetProperty("displayName", out var display)) stores.Add(new(market.GetString() ?? "", shop.GetString() ?? "", display.GetString() ?? "", !value.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.False)); var map = value.EnumerateObject().Select(x => (Key: KnownField(x.Name), Value: x.Value)).Where(x => x.Key.Length > 0).ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase); var product = ParseJsonProduct(map, sourceId, culture, out var error); if (product is null) errors.Add($"Satır {row}: {error}"); else rows.Add(product); }
        return new(MigrationSourceFormat.Json, rows, errors, stores, secrets.ToList());
    }
    static Parsed ReadXml(string path, string sourceId)
    {
        var text = File.ReadAllText(path); var scan = XmlCatalog.Inspect(text); var source = new XmlSource { Id = sourceId, Name = Path.GetFileName(path), ItemPath = scan.ItemPath, Fields = scan.SuggestedFields, Currency = "TRY", CostCurrency = "TRY", ExchangeRate = 1, MarkupPercent = 0, SafetyStock = 0, MinimumStock = 0, MaximumStock = int.MaxValue, DecimalSeparator = "." }; try { var rows = XmlCatalog.Preview(text, source); rows.ForEach(x => x.SourceId = sourceId); return new(MigrationSourceFormat.Xml, rows, [], [], []); } catch (Exception error) { return new(MigrationSourceFormat.Xml, [], [AuditStore.Sanitize(error.Message)], [], []); }
    }
    static CatalogProduct? ParseProduct(IReadOnlyDictionary<string, int> map, IReadOnlyList<string> values, string sourceId, CultureInfo culture, out string error)
    {
        string Text(string key) => map.TryGetValue(key, out var index) && index < values.Count ? values[index].Trim() : ""; var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); foreach (var key in map.Keys) row[key] = Text(key); return ParseProduct(row, sourceId, culture, out error);
    }
    static CatalogProduct? ParseProduct(IReadOnlyDictionary<string, string> row, string sourceId, CultureInfo culture, out string error)
    {
        error = ""; var sku = row.GetValueOrDefault("Sku", ""); var barcode = row.GetValueOrDefault("Barcode", ""); var name = row.GetValueOrDefault("Name", ""); if (sku.Length == 0 && barcode.Length == 0) { error = "SKU veya barkod zorunlu."; return null; } if (name.Length == 0) { error = "Ürün adı zorunlu."; return null; }
        if (!Decimal(row.GetValueOrDefault("Cost", ""), culture, out var cost) || !Decimal(row.GetValueOrDefault("Price", ""), culture, out var price) || !Int(row.GetValueOrDefault("Stock", ""), culture, out var stock)) { error = "Maliyet, fiyat veya stok sayı değil."; return null; } var currency = row.GetValueOrDefault("Currency", "TRY").Trim().ToUpperInvariant(); if (currency.Length == 0) currency = "TRY"; if (currency is not ("TRY" or "USD" or "EUR" or "GBP")) { error = "Desteklenmeyen döviz."; return null; } var vatText = row.GetValueOrDefault("VatRate", "20"); var vat = vatText.Length == 0 ? 20 : Decimal(vatText, culture, out var parsedVat) ? parsedVat : -1; if (cost < 0 || price < 0 || stock < 0 || vat is < 0 or > 100) { error = "Negatif değer veya geçersiz KDV."; return null; }
        return new() { SourceId = sourceId, Sku = sku, Barcode = barcode, Name = name, Brand = row.GetValueOrDefault("Brand", ""), Category = row.GetValueOrDefault("Category", ""), Description = row.GetValueOrDefault("Description", ""), Cost = cost, Price = price, Currency = currency, VatRate = vat, Stock = stock, Gtin = row.GetValueOrDefault("Gtin", ""), ImageUrls = row.GetValueOrDefault("ImageUrls", ""), Active = !row.GetValueOrDefault("Active", "true").Equals("false", StringComparison.OrdinalIgnoreCase) };
    }
    static CatalogProduct? ParseJsonProduct(IReadOnlyDictionary<string, JsonElement> row, string sourceId, CultureInfo culture, out string error) => ParseProduct(row.ToDictionary(x => x.Key, x => x.Value.ValueKind == JsonValueKind.String ? x.Value.GetString() ?? "" : x.Value.ToString(), StringComparer.OrdinalIgnoreCase), sourceId, culture, out error);
    static bool Decimal(string value, CultureInfo culture, out decimal result) => decimal.TryParse(value, NumberStyles.Number, culture, out result) || decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out result);
    static bool Int(string value, CultureInfo culture, out int result) => int.TryParse(value, NumberStyles.Integer, culture, out result) || int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    static string KnownField(string header) => ExcelColumnMapping.Normalize(header) switch { "sku" or "stockcode" or "stokkodu" or "code" => "Sku", "barkod" or "barcode" or "ean" => "Barcode", "ürün" or "urun" or "ürün adı" or "urun adi" or "name" or "title" => "Name", "marka" or "brand" => "Brand", "kategori" or "category" => "Category", "açıklama" or "aciklama" or "description" => "Description", "alış" or "alis" or "maliyet" or "cost" => "Cost", "satış" or "satis" or "fiyat" or "price" => "Price", "döviz" or "doviz" or "currency" => "Currency", "kdv" or "vat" or "vat rate" or "kdv oranı" => "VatRate", "stok" or "stock" or "qty" or "quantity" => "Stock", "aktif" or "active" or "enabled" => "Active", "gtin" or "gtin13" or "gtin14" => "Gtin", "görseller" or "gorseller" or "images" or "imageurls" => "ImageUrls", _ => "" };
    static bool IsSecretField(string value) { var key = value.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant(); return key.Contains("password") || key.Contains("passwd") || key.Contains("token") || key.Contains("secret") || key.Contains("apikey") || key.Contains("clientsecret") || key.Contains("accesskey") || key.Contains("refresh"); }
    static char DetectDelimiter(string line) => line.Count(x => x == ';') > line.Count(x => x == ',') ? ';' : ',';
    static List<string> ParseCsvLine(string line, char delimiter) { var result = new List<string>(); var builder = new StringBuilder(); var quoted = false; for (var i = 0; i < line.Length; i++) { var ch = line[i]; if (ch == '"') { if (quoted && i + 1 < line.Length && line[i + 1] == '"') { builder.Append('"'); i++; } else quoted = !quoted; } else if (ch == delimiter && !quoted) { result.Add(builder.ToString()); builder.Clear(); } else builder.Append(ch); } result.Add(builder.ToString()); return result; }
    static string? ValidateRow(CatalogProduct p) => string.IsNullOrWhiteSpace(p.Name) ? "Ürün adı zorunlu." : string.IsNullOrWhiteSpace(p.Sku) && string.IsNullOrWhiteSpace(p.Barcode) ? "SKU veya barkod zorunlu." : p.Currency is not ("TRY" or "USD" or "EUR" or "GBP") ? "Desteklenmeyen döviz." : p.Cost < 0 || p.Price < 0 || p.Stock < 0 || p.VatRate is < 0 or > 100 ? "Negatif değer veya geçersiz KDV." : null;
    static bool Equivalent(CatalogProduct left, CatalogProduct right) => left.Sku.Equals(right.Sku, StringComparison.OrdinalIgnoreCase) && left.Barcode.Equals(right.Barcode, StringComparison.OrdinalIgnoreCase) && left.Name == right.Name && left.Brand == right.Brand && left.Category == right.Category && left.Description == right.Description && left.Cost == right.Cost && left.Price == right.Price && left.Currency.Equals(right.Currency, StringComparison.OrdinalIgnoreCase) && left.VatRate == right.VatRate && left.Stock == right.Stock && left.Active == right.Active && left.Gtin == right.Gtin && left.ImageUrls == right.ImageUrls;
}

/// Durable checkpoints for the Migration Assistant apply unit-of-work: catalog
/// mutation, taxonomy additions, and store-metadata additions are separate
/// SQLite databases and cannot share one transaction, so this journal is the
/// prepare/apply/finalize record that lets Apply(...) recognize - across a
/// crash/restart or a concurrent second caller - exactly how far a given
/// operation id got, instead of guessing from partial application state. See #2653.
public sealed class MigrationJournalStore
{
    public const string PhaseCatalogApplied = "CatalogApplied";
    public const string PhaseTaxonomyApplied = "TaxonomyApplied";
    public const string PhaseCompleted = "Completed";
    const string Columns = "Id,SourceFileName,CreatedUtc,Status,ReceiptJson,BackupPath,OperationId,Phase,TaxonomyAddedJson,StoreAddedJson,ResultJson";

    readonly string connectionString;
    public MigrationJournalStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop"); Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "migration.db") }.ToString();
        using var c = Open(); using var command = c.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS MigrationJournals(Id TEXT PRIMARY KEY,SourceFileName TEXT NOT NULL,CreatedUtc TEXT NOT NULL,Status TEXT NOT NULL,ReceiptJson TEXT NOT NULL,BackupPath TEXT NOT NULL)"; command.ExecuteNonQuery();
        EnsureColumn(c, "OperationId", "TEXT NOT NULL DEFAULT ''"); EnsureColumn(c, "Phase", $"TEXT NOT NULL DEFAULT '{PhaseCompleted}'");
        EnsureColumn(c, "TaxonomyAddedJson", "TEXT NOT NULL DEFAULT '[]'"); EnsureColumn(c, "StoreAddedJson", "TEXT NOT NULL DEFAULT '[]'"); EnsureColumn(c, "ResultJson", "TEXT NOT NULL DEFAULT ''");
        using var index = c.CreateCommand(); index.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS UX_MigrationJournals_OperationId ON MigrationJournals(OperationId) WHERE OperationId<>''"; index.ExecuteNonQuery();
    }
    static void EnsureColumn(SqliteConnection c, string name, string definition)
    {
        using var check = c.CreateCommand(); check.CommandText = "SELECT 1 FROM pragma_table_info('MigrationJournals') WHERE name=$name"; check.Parameters.AddWithValue("$name", name);
        if (check.ExecuteScalar() is not null) return;
        using var add = c.CreateCommand(); add.CommandText = $"ALTER TABLE MigrationJournals ADD COLUMN {name} {definition}"; add.ExecuteNonQuery();
    }
    SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); return c; }
    static MigrationJournalRecord Read(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), DateTime.Parse(r.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8), r.GetString(9), r.GetString(10));

    public IReadOnlyList<MigrationJournalRecord> List() { using var c = Open(); using var command = c.CreateCommand(); command.CommandText = $"SELECT {Columns} FROM MigrationJournals ORDER BY CreatedUtc DESC"; using var r = command.ExecuteReader(); var rows = new List<MigrationJournalRecord>(); while (r.Read()) rows.Add(Read(r)); return rows; }

    /// Locates the durable record for a given operation id so Apply can detect
    /// "this exact approved operation is already prepared/partially/fully
    /// applied" instead of blindly re-running side effects.
    public MigrationJournalRecord? FindByOperation(string operationId)
    {
        if (string.IsNullOrEmpty(operationId)) return null;
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = $"SELECT {Columns} FROM MigrationJournals WHERE OperationId=$op"; command.Parameters.AddWithValue("$op", operationId);
        using var r = command.ExecuteReader(); return r.Read() ? Read(r) : null;
    }

    /// Durable checkpoint written immediately after the catalog mutation commits
    /// - the only step that could otherwise lose the undo receipt if the process
    /// dies right after. The UNIQUE index on OperationId means a same-operation
    /// race never creates two rows: the losing insert falls back to the winner's.
    public string InsertCatalogPhase(string operationId, string sourceFileName, CatalogUndoReceipt receipt, string backupPath)
    {
        var id = Guid.NewGuid().ToString("N");
        using var c = Open(); using var command = c.CreateCommand();
        command.CommandText = $"INSERT INTO MigrationJournals({Columns}) VALUES($id,$source,$created,'Applied',$receipt,$backup,$op,'{PhaseCatalogApplied}','[]','[]','')";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$source", AuditStore.Sanitize(Path.GetFileName(sourceFileName)));
        command.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$receipt", JsonSerializer.Serialize(receipt)); command.Parameters.AddWithValue("$backup", AuditStore.Sanitize(Path.GetFileName(backupPath ?? "")));
        command.Parameters.AddWithValue("$op", operationId);
        try { command.ExecuteNonQuery(); return id; }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { return FindByOperation(operationId)!.Id; }
    }

    /// Merges (union by Id, never duplicated) into whatever this operation has
    /// already durably recorded, so a partial then resumed taxonomy phase still
    /// ends up with a complete, accurate list for Undo scoping.
    public List<TaxonomyAddedItem> AppendTaxonomyAdded(string id, IReadOnlyList<TaxonomyAddedItem> items)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        using var find = c.CreateCommand(); find.Transaction = tx; find.CommandText = "SELECT TaxonomyAddedJson FROM MigrationJournals WHERE Id=$id"; find.Parameters.AddWithValue("$id", id);
        var existing = JsonSerializer.Deserialize<List<TaxonomyAddedItem>>((string)find.ExecuteScalar()!) ?? [];
        var merged = existing.Concat(items).GroupBy(x => x.Id).Select(x => x.First()).ToList();
        using var update = c.CreateCommand(); update.Transaction = tx; update.CommandText = "UPDATE MigrationJournals SET TaxonomyAddedJson=$json WHERE Id=$id"; update.Parameters.AddWithValue("$json", JsonSerializer.Serialize(merged)); update.Parameters.AddWithValue("$id", id); update.ExecuteNonQuery();
        tx.Commit(); return merged;
    }
    /// Only ever moves forward from CatalogApplied - a stale/duplicate call after
    /// the phase already advanced (or completed) is a harmless no-op.
    public void AdvanceToTaxonomyPhase(string id) { using var c = Open(); using var command = c.CreateCommand(); command.CommandText = $"UPDATE MigrationJournals SET Phase='{PhaseTaxonomyApplied}' WHERE Id=$id AND Phase='{PhaseCatalogApplied}'"; command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery(); }

    public List<StoreAddedItem> AppendStoreAdded(string id, IReadOnlyList<StoreAddedItem> items)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        using var find = c.CreateCommand(); find.Transaction = tx; find.CommandText = "SELECT StoreAddedJson FROM MigrationJournals WHERE Id=$id"; find.Parameters.AddWithValue("$id", id);
        var existing = JsonSerializer.Deserialize<List<StoreAddedItem>>((string)find.ExecuteScalar()!) ?? [];
        var merged = existing.Concat(items).GroupBy(x => x.Id).Select(x => x.First()).ToList();
        using var update = c.CreateCommand(); update.Transaction = tx; update.CommandText = "UPDATE MigrationJournals SET StoreAddedJson=$json WHERE Id=$id"; update.Parameters.AddWithValue("$json", JsonSerializer.Serialize(merged)); update.Parameters.AddWithValue("$id", id); update.ExecuteNonQuery();
        tx.Commit(); return merged;
    }

    /// Only Complete() ever writes Phase='Completed' together with ResultJson, so
    /// a caller can never observe a typed success result for an operation whose
    /// taxonomy/store phase is still outstanding.
    public void Complete(string id, MigrationApplyResult result)
    {
        using var c = Open(); using var command = c.CreateCommand();
        command.CommandText = $"UPDATE MigrationJournals SET Phase='{PhaseCompleted}', ResultJson=$result WHERE Id=$id";
        command.Parameters.AddWithValue("$result", JsonSerializer.Serialize(result)); command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery();
    }

    /// Undo only reverts what THIS operation actually created: the catalog delta
    /// (CatalogStore.Undo already refuses if the catalog changed again since),
    /// plus the taxonomy/store rows this operation added - and only if nothing
    /// else has touched them since (TaxonomyStore.Delete refuses while in use by
    /// any product/mapping; the store compare-and-delete refuses if its revision
    /// moved). A partial (not yet Completed) operation cannot be undone - Apply
    /// must be resumed to a clean, fully-known state first. See #2653.
    public void Undo(string id, CatalogStore catalog, TaxonomyStore taxonomy, MarketplaceConnectionStore stores)
    {
        var row = List().SingleOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("Geçiş günlüğü bulunamadı.");
        if (row.Status != "Applied") throw new InvalidOperationException("Bu geçiş zaten geri alındı.");
        if (row.Phase != PhaseCompleted) throw new InvalidOperationException("RECOVERY_REQUIRED: geçiş tamamlanmadan geri alınamaz; önce aynı önizlemeyle Apply'ı tekrar çalıştırıp tamamlayın.");
        var receipt = JsonSerializer.Deserialize<CatalogUndoReceipt>(row.ReceiptJson) ?? throw new InvalidDataException("Geçiş geri alma kaydı bozuk.");
        catalog.Undo(receipt);
        foreach (var item in JsonSerializer.Deserialize<List<TaxonomyAddedItem>>(row.TaxonomyAddedJson) ?? [])
            try { taxonomy.Delete(item.Kind, item.Id); } catch (InvalidOperationException) { /* shared/pre-existing usage, or already removed - never force-delete data another operation depends on */ }
        foreach (var item in JsonSerializer.Deserialize<List<StoreAddedItem>>(row.StoreAddedJson) ?? [])
            stores.DeleteIfUntouchedSinceCreate(item.Id, 1);
        using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "UPDATE MigrationJournals SET Status='Undone' WHERE Id=$id"; command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery();
    }
}
