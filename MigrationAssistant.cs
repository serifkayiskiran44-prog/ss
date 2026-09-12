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
    public IReadOnlyList<CatalogProduct> ReadyRows => Lines.Where(x => x.Product is not null && (x.Action is "CREATE" or "UPDATE")).Select(x => x.Product!).ToList();
    public IReadOnlyList<string> Errors => Lines.Where(x => x.Action == "ERROR").Select(x => x.Error).ToList();
}
public sealed record MigrationApplyResult(string JournalId, int Created, int Updated, int Skipped, int TaxonomyAdded, int StoresAdded, string BackupPath);
public sealed record MigrationJournalRecord(string Id, string SourceFileName, DateTime CreatedUtc, string Status, string ReceiptJson, string BackupPath);

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

    public MigrationApplyResult Apply(MigrationPreview preview, bool approved, string backupPath = "")
    {
        if (!approved) throw new InvalidOperationException("Veri geçişi için önizleme onayı gerekli.");
        if (preview.Errors.Count > 0) throw new InvalidOperationException("Hatalı satırlar düzeltilmeden veri geçişi uygulanamaz.");
        var catalog = new CatalogStore(directory); var current = catalog.Products().ToDictionary(x => x.Id);
        foreach (var line in preview.Lines.Where(x => x.Action == "UPDATE")) if (!current.TryGetValue(line.ExistingId, out var now) || now.UpdatedUtc != line.ExistingUpdatedUtc) throw new InvalidOperationException("Önizleme sonrası mevcut veri değişti; yeniden önizleme oluşturun.");
        var rows = preview.ReadyRows; var receipt = catalog.ApplyMigration(rows, "migration:" + Guid.NewGuid().ToString("N")); var journal = new MigrationJournalStore(directory); var journalId = journal.Save(Path.GetFileName(preview.SourcePath), receipt, backupPath);
        var taxonomy = new TaxonomyStore(directory); var taxonomyAdded = 0; foreach (var name in rows.Select(x => x.Brand).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)) { if (!taxonomy.List(TaxonomyKind.Brand).Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) { taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = name, Value = name }); taxonomyAdded++; } }
        foreach (var name in rows.Select(x => x.Category).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)) { if (!taxonomy.List(TaxonomyKind.Category).Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) { taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = name, Value = name }); taxonomyAdded++; } }
        var stores = new MarketplaceConnectionStore(directory); var storesAdded = 0; foreach (var metadata in preview.Stores) { stores.Save(metadata.Channel, metadata.ShopId, metadata.DisplayName, metadata.Enabled); storesAdded++; }
        return new(journalId, preview.Lines.Count(x => x.Action == "CREATE"), preview.Lines.Count(x => x.Action == "UPDATE"), preview.Lines.Count(x => x.Action == "SKIP"), taxonomyAdded, storesAdded, backupPath);
    }

    public void Undo(string journalId) => new MigrationJournalStore(directory).Undo(journalId, new CatalogStore(directory));

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

public sealed class MigrationJournalStore
{
    readonly string connectionString;
    public MigrationJournalStore(string? directory = null) { directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop"); Directory.CreateDirectory(directory); connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "migration.db") }.ToString(); using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "CREATE TABLE IF NOT EXISTS MigrationJournals(Id TEXT PRIMARY KEY,SourceFileName TEXT NOT NULL,CreatedUtc TEXT NOT NULL,Status TEXT NOT NULL,ReceiptJson TEXT NOT NULL,BackupPath TEXT NOT NULL)"; command.ExecuteNonQuery(); }
    SqliteConnection Open() { var c = SqliteConnectionPolicy.Open(connectionString); return c; }
    public string Save(string sourceFileName, CatalogUndoReceipt receipt, string backupPath) { var id = Guid.NewGuid().ToString("N"); using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "INSERT INTO MigrationJournals VALUES($id,$source,$created,'Applied',$receipt,$backup)"; command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$source", AuditStore.Sanitize(Path.GetFileName(sourceFileName))); command.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$receipt", JsonSerializer.Serialize(receipt)); command.Parameters.AddWithValue("$backup", AuditStore.Sanitize(Path.GetFileName(backupPath ?? ""))); command.ExecuteNonQuery(); return id; }
    public IReadOnlyList<MigrationJournalRecord> List() { using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "SELECT Id,SourceFileName,CreatedUtc,Status,ReceiptJson,BackupPath FROM MigrationJournals ORDER BY CreatedUtc DESC"; using var r = command.ExecuteReader(); var rows = new List<MigrationJournalRecord>(); while (r.Read()) rows.Add(new(r.GetString(0), r.GetString(1), DateTime.Parse(r.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), r.GetString(3), r.GetString(4), r.GetString(5))); return rows; }
    public void Undo(string id, CatalogStore catalog) { var row = List().SingleOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("Geçiş günlüğü bulunamadı."); if (row.Status != "Applied") throw new InvalidOperationException("Bu geçiş zaten geri alındı."); var receipt = JsonSerializer.Deserialize<CatalogUndoReceipt>(row.ReceiptJson) ?? throw new InvalidDataException("Geçiş geri alma kaydı bozuk."); catalog.Undo(receipt); using var c = Open(); using var command = c.CreateCommand(); command.CommandText = "UPDATE MigrationJournals SET Status='Undone' WHERE Id=$id"; command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery(); }
}
