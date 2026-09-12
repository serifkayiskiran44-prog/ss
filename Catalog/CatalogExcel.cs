using ClosedXML.Excel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TrMarketplaceHubDesktop.Catalog;

public sealed record ExcelPreview(IReadOnlyList<CatalogProduct> Rows, IReadOnlyList<string> Errors,
    string FileHash = "", string ProfileFingerprint = "", string ProductRevisionFingerprint = "", string SourcePath = "");
public sealed record ExcelPreviewDecision(int RowNumber, string Action, CatalogProduct? Product, string Error = "");
public sealed record ExcelDecisionPreview(IReadOnlyList<ExcelPreviewDecision> Rows, IReadOnlyList<string> Errors, ExcelPreview? Preview = null);
public static class ExcelPreviewExtensions { public static int IndexOf<T>(this IReadOnlyList<T> source, T value) => Enumerable.Range(0, source.Count).FirstOrDefault(i => EqualityComparer<T>.Default.Equals(source[i], value), -1); }

public sealed record ExcelColumnMapping(IReadOnlyDictionary<string, int> Columns)
{
    public static ExcelColumnMapping FromHeaders(IXLRow header, IReadOnlyDictionary<string, string>? aliases = null)
    {
        var known = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Sku"] = ["sku", "stockcode", "stokkodu", "code"], ["Barcode"] = ["barkod", "barcode", "ean"], ["Name"] = ["ürün", "urun", "ürün adı", "urun adi", "name", "title"], ["Brand"] = ["marka", "brand"], ["Category"] = ["kategori", "category"], ["Description"] = ["açıklama", "aciklama", "description"], ["Cost"] = ["alış", "alis", "maliyet", "cost"], ["Price"] = ["satış", "satis", "fiyat", "price"], ["Currency"] = ["döviz", "doviz", "currency"], ["VatRate"] = ["kdv", "vat", "vat rate", "kdv oranı"], ["Stock"] = ["stok", "stock", "qty", "quantity"], ["Active"] = ["aktif", "active", "enabled"], ["Gtin"] = ["gtin", "gtin13", "gtin14"], ["ImageUrls"] = ["görseller", "gorseller", "images", "imageurls"]
        };
        if (aliases is not null) foreach (var pair in aliases) if (known.ContainsKey(pair.Value)) known[pair.Value] = known[pair.Value].Concat([pair.Key]).ToArray();
        var found = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in header.CellsUsed()) { var value = Normalize(cell.GetString()); foreach (var pair in known) if (pair.Value.Any(a => Normalize(a) == value)) { found[pair.Key] = cell.Address.ColumnNumber; break; } }
        return new(found);
    }
    public static ExcelColumnMapping FromProfile(IXLRow header, ExcelImportProfile profile)
    {
        var auto = FromHeaders(header, profile.HeaderAliases); var found = new Dictionary<string, int>(auto.Columns, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in profile.ColumnMappings) { var wanted = Normalize(pair.Value); var cell = header.CellsUsed().FirstOrDefault(x => Normalize(x.GetString()) == wanted); if (cell is not null) found[pair.Key] = cell.Address.ColumnNumber; }
        return new(found);
    }
    internal static string Normalize(string value) => value.Trim().ToLowerInvariant().Replace("ı", "i").Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).Aggregate(new StringBuilder(), (b, c) => b.Append(char.IsWhiteSpace(c) ? ' ' : c)).ToString();
}

public static class CatalogExcel
{
    public static IReadOnlyList<string> Headers(string path) { EnsureFile(path); using var book = new XLWorkbook(path); var sheet = book.Worksheets.FirstOrDefault() ?? throw new InvalidDataException("Çalışma sayfası bulunamadı."); var row = sheet.FirstRowUsed() ?? throw new InvalidDataException("Başlık satırı bulunamadı."); return row.CellsUsed().Select(c => c.GetString().Trim()).ToList(); }
    public static void ExportErrors(string path, ExcelPreview preview) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N"); try { using var book = new XLWorkbook(); var sheet = book.AddWorksheet("Hatalar"); sheet.Cell(1, 1).Value = "Hata"; for (var i = 0; i < preview.Errors.Count; i++) sheet.Cell(i + 2, 1).Value = preview.Errors[i]; sheet.Columns().AdjustToContents(); book.SaveAs(temporary); File.Move(temporary, path, true); } finally { if (File.Exists(temporary)) File.Delete(temporary); } }
    public static void Export(string path, IReadOnlyList<CatalogProduct> products) => Export(path, products, null);
    public static void Export(string path, IReadOnlyList<CatalogProduct> products, IReadOnlyCollection<string>? visibleFields)
    {
        var all = new (string Key, string Header, Func<CatalogProduct, object?> Value)[] { ("Sku", "SKU", p => p.Sku), ("Barcode", "Barkod", p => p.Barcode), ("Name", "Ürün", p => p.Name), ("Brand", "Marka", p => p.Brand), ("Category", "Kategori", p => p.Category), ("Description", "Açıklama", p => p.Description), ("Cost", "Alış", p => p.Cost), ("Price", "Satış", p => p.Price), ("Currency", "Döviz", p => p.Currency), ("VatRate", "KDV %", p => p.VatRate), ("Stock", "Stok", p => p.Stock), ("Active", "Aktif", p => p.Active), ("Gtin", "GTIN", p => p.Gtin), ("ImageUrls", "Görseller", p => p.ImageUrls), ("SourceId", "XML Kaynağı", p => p.SourceId), ("SourceKind", "Veri kaynağı", p => p.SourceKind), ("PriceSource", "Fiyat kaynağı", p => p.PriceSource), ("StockSource", "Stok kaynağı", p => p.StockSource), ("MediaSource", "Medya kaynağı", p => p.MediaSource) };
        var columns = visibleFields is null || visibleFields.Count == 0 ? all : all.Where(x => visibleFields.Contains(x.Key, StringComparer.OrdinalIgnoreCase)).ToArray(); if (columns.Length == 0) throw new InvalidOperationException("Dışa aktarım için en az bir görünür alan seçin.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N"); try { using var book = new XLWorkbook(); var sheet = book.AddWorksheet("Ürünler"); for (var i = 0; i < columns.Length; i++) sheet.Cell(1, i + 1).Value = columns[i].Header; var row = 2; foreach (var product in products) { for (var i = 0; i < columns.Length; i++) sheet.Cell(row, i + 1).Value = columns[i].Value(product)?.ToString() ?? ""; row++; } sheet.Columns().AdjustToContents(); book.SaveAs(temporary); File.Move(temporary, path, true); } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static ExcelPreview Preview(string path, ExcelColumnMapping? mapping = null) => Preview(path, mapping, DeterministicNumberParser.Culture("en-US"), null);
    public static ExcelPreview Preview(string path, CultureInfo culture) => Preview(path, null, culture, null);
    public static ExcelPreview Preview(string path, ExcelImportProfile profile) => Preview(path, null, ExcelProfileStore.Culture(profile.CultureName), profile);
    public static ExcelDecisionPreview PreviewDecisions(CatalogStore store, string path, ExcelImportProfile profile)
    {
        var preview = WithMetadata(Preview(path, profile), path, profile, store); return BuildDecisions(store, preview);
    }
    public static async Task<ExcelDecisionPreview> PreviewDecisionsAsync(CatalogStore store, string path, ExcelImportProfile profile, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() => { cancellationToken.ThrowIfCancellationRequested(); return PreviewDecisions(store, path, profile); }, cancellationToken).ConfigureAwait(false);
    }
    static ExcelDecisionPreview BuildDecisions(CatalogStore store, ExcelPreview preview)
    {
        var existing = store.Products(); var bySku = existing.Where(x => x.Sku.Length > 0).GroupBy(x => x.Sku, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Single(), StringComparer.OrdinalIgnoreCase); var byBarcode = existing.Where(x => x.Barcode.Length > 0).GroupBy(x => x.Barcode, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Single(), StringComparer.OrdinalIgnoreCase); var decisions = new List<ExcelPreviewDecision>(); var row = 2; foreach (var product in preview.Rows) { var match = bySku.GetValueOrDefault(product.Sku) ?? (product.Barcode.Length == 0 ? null : byBarcode.GetValueOrDefault(product.Barcode)); var action = match is null ? "CREATE" : Equivalent(match, product) ? "SKIP" : "UPDATE"; decisions.Add(new(row++, action, product)); } foreach (var error in preview.Errors) decisions.Add(new(row++, "ERROR", null, error)); return new(decisions, preview.Errors, preview);
    }
    static bool Equivalent(CatalogProduct left, CatalogProduct right) => left.Sku.Equals(right.Sku, StringComparison.OrdinalIgnoreCase) && left.Barcode.Equals(right.Barcode, StringComparison.OrdinalIgnoreCase) && left.Name == right.Name && left.Brand == right.Brand && left.Category == right.Category && left.Description == right.Description && left.Cost == right.Cost && left.Price == right.Price && left.Currency.Equals(right.Currency, StringComparison.OrdinalIgnoreCase) && left.VatRate == right.VatRate && left.Stock == right.Stock && left.Active == right.Active && left.Gtin == right.Gtin;
    static ExcelPreview Preview(string path, ExcelColumnMapping? mapping, CultureInfo culture, ExcelImportProfile? profile)
    {
        EnsureFile(path); using var book = new XLWorkbook(path); var sheet = book.Worksheets.FirstOrDefault() ?? throw new InvalidDataException("Çalışma sayfası bulunamadı."); var header = sheet.FirstRowUsed() ?? throw new InvalidDataException("Başlık satırı bulunamadı."); var map = mapping ?? (profile is null ? ExcelColumnMapping.FromHeaders(header) : ExcelColumnMapping.FromProfile(header, profile)); var rows = new List<CatalogProduct>(); var errors = new List<string>(); var seenSku = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var seenBarcode = new HashSet<string>(StringComparer.OrdinalIgnoreCase); int Col(string key) => map.Columns.TryGetValue(key, out var c) ? c : 0; string Text(IXLRow row, string key) => Col(key) == 0 ? profile?.Defaults.GetValueOrDefault(key, "") ?? "" : row.Cell(Col(key)).GetString().Trim(); var cultureName = string.IsNullOrWhiteSpace(culture.Name) ? "en-US" : culture.Name; foreach (var key in new[] { "Sku", "Name", "Cost", "Price", "Stock" }) if (Col(key) == 0 && !(profile?.Defaults.ContainsKey(key) ?? false)) errors.Add($"Kolon eşleme eksik: {key}."); if (errors.Count > 0) return new(rows, errors); foreach (var excelRow in sheet.RowsUsed().Skip(1)) { var sku = Text(excelRow, "Sku"); if (sku.Length == 0) { errors.Add($"Satır {excelRow.RowNumber()}: SKU zorunlu."); continue; } var barcode = Text(excelRow, "Barcode"); if (!seenSku.Add(sku)) { errors.Add($"Satır {excelRow.RowNumber()}: SKU dosyada yineleniyor."); continue; } if (barcode.Length > 0 && !seenBarcode.Add(barcode)) { errors.Add($"Satır {excelRow.RowNumber()}: barkod dosyada yineleniyor."); continue; } var costResult = DeterministicNumberParser.Decimal(Text(excelRow, "Cost"), cultureName, "Maliyet"); var priceResult = DeterministicNumberParser.Decimal(Text(excelRow, "Price"), cultureName, "Fiyat"); var stockResult = DeterministicNumberParser.Integer(Text(excelRow, "Stock"), cultureName, "Stok"); if (!costResult.Success || !priceResult.Success || !stockResult.Success || stockResult.Value > int.MaxValue) { var failure = !costResult.Success ? costResult : !priceResult.Success ? priceResult : !stockResult.Success ? stockResult : new NumberParseResult(false, 0, "OVERFLOW", "Stok tam sayı sınırını aşıyor."); errors.Add($"Satır {excelRow.RowNumber()}: {failure.Message} ({failure.Code})."); continue; } var cost = costResult.Value; var price = priceResult.Value; var stock = (int)stockResult.Value; var currency = Text(excelRow, "Currency"); var vatText = Text(excelRow, "VatRate"); var vatResult = vatText.Length == 0 ? new NumberParseResult(true, 20m, "OK", "") : DeterministicNumberParser.Decimal(vatText, cultureName, "KDV"); if (!vatResult.Success || vatResult.Value is < 0 or > 100) { errors.Add($"Satır {excelRow.RowNumber()}: KDV oranı 0-100 arası olmalı ({vatResult.Code})."); continue; } var vat = vatResult.Value; rows.Add(new CatalogProduct { Sku = sku, Barcode = barcode, Name = Text(excelRow, "Name"), Brand = Text(excelRow, "Brand"), Category = Text(excelRow, "Category"), Description = Text(excelRow, "Description"), Cost = cost, Price = price, Currency = currency.Length == 3 ? currency : "TRY", VatRate = vat, Stock = stock, Active = !string.Equals(Text(excelRow, "Active"), "false", StringComparison.OrdinalIgnoreCase), Gtin = Text(excelRow, "Gtin"), ImageUrls = Text(excelRow, "ImageUrls") }); } return new(rows, errors);
    }
    public static ImportSummary Apply(CatalogStore store, XmlSource source, ExcelPreview preview) { ValidatePreview(preview); source.UpdateName = true; source.UpdateDescription = true; source.UpdateImages = true; source.Fields["Gtin"] = "excel"; var rows = preview.Rows.Select(p => { p.SourceId = source.Id; p.SourceKind = "excel"; p.PriceSource = "excel"; p.StockSource = "excel"; p.MediaSource = "excel"; p.SourceUpdatedUtc = DateTime.UtcNow; return p; }).ToList(); return store.Import(source, rows); }
    public static ImportSummary Apply(CatalogStore store, XmlSource source, ExcelPreview preview, IReadOnlyCollection<int> selectedRows) { if (selectedRows.Count == 0) throw new InvalidOperationException("Uygulamak için en az bir satır seçin."); if (preview.Errors.Count > 0) throw new InvalidOperationException("Hatalı Excel satırları düzeltilmeden katalog güncellenemez."); var rows = preview.Rows.Where((_, index) => selectedRows.Contains(index)).ToList(); if (rows.Count != selectedRows.Count) throw new InvalidOperationException("Seçili satır numarası önizleme dışında."); return Apply(store, source, new ExcelPreview(rows, Array.Empty<string>())); }
    public static CatalogUndoReceipt ApplyWithUndo(CatalogStore store, XmlSource source, ExcelPreview preview, IReadOnlyCollection<int> selectedRows) => ApplyWithUndoAsync(store, source, preview, selectedRows, preview.SourcePath, null, CancellationToken.None).GetAwaiter().GetResult();
    public static async Task<CatalogUndoReceipt> ApplyWithUndoAsync(CatalogStore store, XmlSource source, ExcelPreview preview, IReadOnlyCollection<int> selectedRows, string? sourcePath, ExcelImportProfile? profile, CancellationToken cancellationToken = default)
    {
        if (selectedRows.Count == 0) throw new InvalidOperationException("Uygulamak için en az bir satır seçin.");
        ValidatePreview(preview);
        if (!string.IsNullOrWhiteSpace(sourcePath) && profile is not null) ValidatePreviewFresh(store, sourcePath, profile, preview);
        cancellationToken.ThrowIfCancellationRequested();
        var rows = preview.Rows.Where((_, index) => selectedRows.Contains(index)).ToList();
        if (rows.Count != selectedRows.Count) throw new InvalidOperationException("Seçili satır numarası önizleme dışında.");
        source.UpdateName = true; source.UpdateDescription = true; source.UpdateImages = true; source.Fields["Gtin"] = "excel";
        rows.ForEach(p => { p.SourceId = source.Id; p.SourceKind = "excel"; p.PriceSource = "excel"; p.StockSource = "excel"; p.MediaSource = "excel"; p.SourceUpdatedUtc = DateTime.UtcNow; });
        return await Task.Run(() => store.ImportWithUndo(source, rows, cancellationToken), cancellationToken).ConfigureAwait(false);
    }
    public static void ValidatePreviewFresh(CatalogStore store, string sourcePath, ExcelImportProfile profile, ExcelPreview preview)
    {
        var actualFileHash = HashFile(sourcePath); if (!string.IsNullOrWhiteSpace(preview.FileHash) && !actualFileHash.Equals(preview.FileHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Excel dosyası önizlemeden sonra değişti; yeniden önizleme oluşturun.");
        var actualProfile = HashProfile(profile); if (!string.IsNullOrWhiteSpace(preview.ProfileFingerprint) && !actualProfile.Equals(preview.ProfileFingerprint, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Excel profili önizlemeden sonra değişti; yeniden önizleme oluşturun.");
        var actualProducts = HashProducts(store.Products()); if (!string.IsNullOrWhiteSpace(preview.ProductRevisionFingerprint) && !actualProducts.Equals(preview.ProductRevisionFingerprint, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Katalog önizlemeden sonra değişti; yeniden önizleme oluşturun.");
    }
    static void ValidatePreview(ExcelPreview preview) { if (preview.Errors.Count > 0) throw new InvalidOperationException("Hatalı Excel satırları düzeltilmeden katalog güncellenemez."); foreach (var row in preview.Rows) if (row.Currency.Trim().ToUpperInvariant() is not ("TRY" or "USD" or "EUR" or "GBP")) throw new InvalidOperationException($"Excel döviz değeri desteklenmiyor: {row.Currency}."); }
    static string HashFile(string path) { EnsureFile(path); using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    static string HashProfile(ExcelImportProfile profile) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(profile)))).ToLowerInvariant();
    static string HashProducts(IEnumerable<CatalogProduct> products) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(products.OrderBy(x => x.Id))))).ToLowerInvariant();
    internal static ExcelPreview WithMetadata(ExcelPreview preview, string path, ExcelImportProfile profile, CatalogStore store) => preview with { FileHash = HashFile(path), ProfileFingerprint = HashProfile(profile), ProductRevisionFingerprint = HashProducts(store.Products()), SourcePath = Path.GetFullPath(path) };
    static void EnsureFile(string path) { if (!File.Exists(path)) throw new FileNotFoundException("Excel dosyası bulunamadı.", path); if (new FileInfo(path).Length > 100L * 1024 * 1024) throw new InvalidDataException("Excel dosyası 100 MB sınırını aşıyor."); }
}

public sealed class ExcelApplyCoordinator : IDisposable
{
    int applying;
    public bool IsApplying => Volatile.Read(ref applying) == 1;
    public async Task<CatalogUndoReceipt> ApplyWithUndoAsync(CatalogStore store, XmlSource source, ExcelPreview preview, IReadOnlyCollection<int> selectedRows, string? sourcePath, ExcelImportProfile? profile, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref applying, 1) != 0) throw new InvalidOperationException("Excel uygulaması zaten çalışıyor.");
        try
        {
            try { await Task.Delay(25, cancellationToken).ConfigureAwait(false); }
            catch (TaskCanceledException) { throw new OperationCanceledException(cancellationToken); }
            return await CatalogExcel.ApplyWithUndoAsync(store, source, preview, selectedRows, sourcePath, profile, cancellationToken).ConfigureAwait(false);
        }
        finally { Volatile.Write(ref applying, 0); }
    }
    public void Dispose() { }
}
