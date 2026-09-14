using ClosedXML.Excel;
using System.Globalization;
using System.IO;
using System.Text;

namespace TrMarketplaceHubDesktop.Catalog;

public sealed record ExcelPreview(IReadOnlyList<CatalogProduct> Rows, IReadOnlyList<string> Errors);
public sealed record ExcelPreviewDecision(int RowNumber, string Action, CatalogProduct? Product, string Error = "");
public sealed record ExcelDecisionPreview(IReadOnlyList<ExcelPreviewDecision> Rows, IReadOnlyList<string> Errors);
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
    public static IReadOnlyList<string> ListWorksheets(string path) { EnsureFile(path); using var book = new XLWorkbook(path); var names = book.Worksheets.Select(w => w.Name).ToList(); if (names.Count == 0) throw new InvalidDataException("Çalışma kitabında sayfa bulunamadı."); return names; }
    static IXLWorksheet ResolveSheet(XLWorkbook book, string? sheetName)
    {
        if (string.IsNullOrWhiteSpace(sheetName)) return book.Worksheets.FirstOrDefault() ?? throw new InvalidDataException("Çalışma sayfası bulunamadı.");
        return book.Worksheets.FirstOrDefault(w => string.Equals(w.Name, sheetName, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidDataException($"Çalışma sayfası bulunamadı: {sheetName}.");
    }
    public static IReadOnlyList<string> Headers(string path, string? sheetName = null, int headerRow = 1)
    {
        if (headerRow < 1) throw new ArgumentOutOfRangeException(nameof(headerRow), "Başlık satırı 1 veya daha büyük olmalı.");
        EnsureFile(path); using var book = new XLWorkbook(path); var sheet = ResolveSheet(book, sheetName);
        var row = sheet.Row(headerRow); if (!row.CellsUsed().Any()) throw new InvalidDataException("Seçilen başlık satırı boş.");
        return row.CellsUsed().Select(c => c.GetString().Trim()).ToList();
    }
    /// True when the workbook's current header set (for the profile's configured
    /// sheet/row) no longer matches the header set captured when the profile was
    /// saved - column mapping by position/name could now be wrong.
    public static bool IsProfileStale(ExcelImportProfile profile, string path)
    {
        if (profile.ExpectedHeaders.Count == 0) return false;
        IReadOnlyList<string> current;
        try { current = Headers(path, profile.SheetName, profile.HeaderRow); } catch (InvalidDataException) { return true; }
        var normalizedExpected = profile.ExpectedHeaders.Select(ExcelColumnMapping.Normalize).ToList();
        var normalizedCurrent = current.Select(ExcelColumnMapping.Normalize).ToList();
        return !normalizedExpected.SequenceEqual(normalizedCurrent);
    }
    public static void ExportErrors(string path, ExcelPreview preview) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); var temporary = TempSiblingPath(path); try { using var book = new XLWorkbook(); var sheet = book.AddWorksheet("Hatalar"); sheet.Cell(1, 1).Value = "Hata"; for (var i = 0; i < preview.Errors.Count; i++) sheet.Cell(i + 2, 1).Value = preview.Errors[i]; sheet.Columns().AdjustToContents(); book.SaveAs(temporary); File.Move(temporary, path, true); } finally { if (File.Exists(temporary)) File.Delete(temporary); } }
    /// Bump only when the export column KEY set or ORDER changes (a label/header text
    /// change alone does not require a bump - the key is the stable identity).
    public const int ExportSchemaVersion = 2;
    enum ExportCellKind { Text, Decimal, Int, Bool }
    /// Single source of truth for export column identity, order and type - the same
    /// list backs both the workbook contents and the schema version's meaning.
    static readonly (string Key, string Header, ExportCellKind Kind, Func<CatalogProduct, object?> Value)[] ExportColumns =
    [
        ("Sku", "SKU", ExportCellKind.Text, p => p.Sku), ("Barcode", "Barkod", ExportCellKind.Text, p => p.Barcode), ("Name", "Ürün", ExportCellKind.Text, p => p.Name),
        ("Brand", "Marka", ExportCellKind.Text, p => p.Brand), ("Category", "Kategori", ExportCellKind.Text, p => p.Category), ("Description", "Açıklama", ExportCellKind.Text, p => p.Description),
        ("Cost", "Alış", ExportCellKind.Decimal, p => p.Cost), ("Price", "Satış", ExportCellKind.Decimal, p => p.Price), ("Currency", "Döviz", ExportCellKind.Text, p => p.Currency),
        ("VatRate", "KDV %", ExportCellKind.Decimal, p => p.VatRate), ("Stock", "Stok", ExportCellKind.Int, p => p.Stock), ("Active", "Aktif", ExportCellKind.Bool, p => p.Active),
        ("Gtin", "GTIN", ExportCellKind.Text, p => p.Gtin), ("ImageUrls", "Görseller", ExportCellKind.Text, p => p.ImageUrls), ("SourceId", "XML Kaynağı", ExportCellKind.Text, p => p.SourceId),
        ("SourceKind", "Veri kaynağı", ExportCellKind.Text, p => p.SourceKind), ("PriceSource", "Fiyat kaynağı", ExportCellKind.Text, p => p.PriceSource),
        ("StockSource", "Stok kaynağı", ExportCellKind.Text, p => p.StockSource), ("MediaSource", "Medya kaynağı", ExportCellKind.Text, p => p.MediaSource),
    ];
    public static IReadOnlyList<string> ExportColumnKeys => ExportColumns.Select(x => x.Key).ToList();
    public static void Export(string path, IReadOnlyList<CatalogProduct> products) => Export(path, products, null);
    public static void Export(string path, IReadOnlyList<CatalogProduct> products, IReadOnlyCollection<string>? visibleFields)
    {
        var columns = visibleFields is null || visibleFields.Count == 0 ? ExportColumns : ExportColumns.Where(x => visibleFields.Contains(x.Key, StringComparer.OrdinalIgnoreCase)).ToArray(); if (columns.Length == 0) throw new InvalidOperationException("Dışa aktarım için en az bir görünür alan seçin.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); var temporary = TempSiblingPath(path);
        try
        {
            using var book = new XLWorkbook();
            book.Properties.Comments = $"MarketplaceHub product export schema v{ExportSchemaVersion}";
            var sheet = book.AddWorksheet("Ürünler");
            for (var i = 0; i < columns.Length; i++) sheet.Cell(1, i + 1).Value = columns[i].Header;
            var row = 2;
            foreach (var product in products)
            {
                for (var i = 0; i < columns.Length; i++)
                {
                    var cell = sheet.Cell(row, i + 1);
                    var value = columns[i].Value(product);
                    switch (columns[i].Kind)
                    {
                        case ExportCellKind.Decimal: cell.Value = Convert.ToDecimal(value ?? 0m, CultureInfo.InvariantCulture); break;
                        case ExportCellKind.Int: cell.Value = Convert.ToInt32(value ?? 0, CultureInfo.InvariantCulture); break;
                        case ExportCellKind.Bool: cell.Value = value is true; break;
                        default:
                            // Text number format ("@") keeps a leading-zero SKU/barcode as
                            // exact text on reopen, and prevents Excel from evaluating a
                            // value starting with =/+/-/@ as a formula (CSV/XLSX injection).
                            cell.Style.NumberFormat.Format = "@";
                            cell.Value = value?.ToString() ?? "";
                            break;
                    }
                }
                row++;
            }
            sheet.Columns().AdjustToContents(); book.SaveAs(temporary); File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static ExcelPreview Preview(string path, ExcelColumnMapping? mapping = null, string? sheetName = null, int headerRow = 1) => Preview(path, mapping, CultureInfo.CurrentCulture, null, sheetName, headerRow);
    public static ExcelPreview Preview(string path, CultureInfo culture, string? sheetName = null, int headerRow = 1) => Preview(path, null, culture, null, sheetName, headerRow);
    public static ExcelPreview Preview(string path, ExcelImportProfile profile) => Preview(path, null, ExcelProfileStore.Culture(profile.CultureName), profile, profile.SheetName, profile.HeaderRow);
    public static ExcelDecisionPreview PreviewDecisions(CatalogStore store, string path, ExcelImportProfile profile)
    {
        var preview = Preview(path, profile); var existing = store.Products(); var bySku = existing.Where(x => x.Sku.Length > 0).GroupBy(x => x.Sku, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Single(), StringComparer.OrdinalIgnoreCase); var byBarcode = existing.Where(x => x.Barcode.Length > 0).GroupBy(x => x.Barcode, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Single(), StringComparer.OrdinalIgnoreCase); var decisions = new List<ExcelPreviewDecision>(); var row = 2; foreach (var product in preview.Rows) { var match = bySku.GetValueOrDefault(product.Sku) ?? (product.Barcode.Length == 0 ? null : byBarcode.GetValueOrDefault(product.Barcode)); var action = match is null ? "CREATE" : Equivalent(match, product) ? "SKIP" : "UPDATE"; decisions.Add(new(row++, action, product)); } foreach (var error in preview.Errors) decisions.Add(new(row++, "ERROR", null, error)); return new(decisions, preview.Errors);
    }
    static bool Equivalent(CatalogProduct left, CatalogProduct right) => left.Sku.Equals(right.Sku, StringComparison.OrdinalIgnoreCase) && left.Barcode.Equals(right.Barcode, StringComparison.OrdinalIgnoreCase) && left.Name == right.Name && left.Brand == right.Brand && left.Category == right.Category && left.Description == right.Description && left.Cost == right.Cost && left.Price == right.Price && left.Currency.Equals(right.Currency, StringComparison.OrdinalIgnoreCase) && left.VatRate == right.VatRate && left.Stock == right.Stock && left.Active == right.Active && left.Gtin == right.Gtin;
    static ExcelPreview Preview(string path, ExcelColumnMapping? mapping, CultureInfo culture, ExcelImportProfile? profile, string? sheetName = null, int headerRow = 1)
    {
        if (headerRow < 1) throw new ArgumentOutOfRangeException(nameof(headerRow), "Başlık satırı 1 veya daha büyük olmalı.");
        EnsureFile(path); using var book = new XLWorkbook(path); var sheet = ResolveSheet(book, sheetName); var header = sheet.Row(headerRow); if (!header.CellsUsed().Any()) throw new InvalidDataException("Seçilen başlık satırı boş."); var map = mapping ?? (profile is null ? ExcelColumnMapping.FromHeaders(header) : ExcelColumnMapping.FromProfile(header, profile)); var rows = new List<CatalogProduct>(); var errors = new List<string>(); var seenSku = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var seenBarcode = new HashSet<string>(StringComparer.OrdinalIgnoreCase); int Col(string key) => map.Columns.TryGetValue(key, out var c) ? c : 0; string Text(IXLRow row, string key) => Col(key) == 0 ? profile?.Defaults.GetValueOrDefault(key, "") ?? "" : row.Cell(Col(key)).GetString().Trim(); foreach (var key in new[] { "Sku", "Name", "Cost", "Price", "Stock" }) if (Col(key) == 0 && !(profile?.Defaults.ContainsKey(key) ?? false)) errors.Add($"Kolon eşleme eksik: {key}."); if (errors.Count > 0) return new(rows, errors); foreach (var excelRow in sheet.RowsUsed().Where(r => r.RowNumber() > headerRow)) { var sku = Text(excelRow, "Sku"); if (sku.Length == 0) { errors.Add($"Satır {excelRow.RowNumber()}: SKU zorunlu."); continue; } var barcode = Text(excelRow, "Barcode"); if (!seenSku.Add(sku)) { errors.Add($"Satır {excelRow.RowNumber()}: SKU dosyada yineleniyor."); continue; } if (barcode.Length > 0 && !seenBarcode.Add(barcode)) { errors.Add($"Satır {excelRow.RowNumber()}: barkod dosyada yineleniyor."); continue; } if (!decimal.TryParse(Text(excelRow, "Cost"), NumberStyles.Any, culture, out var cost) || !decimal.TryParse(Text(excelRow, "Price"), NumberStyles.Any, culture, out var price) || !int.TryParse(Text(excelRow, "Stock"), NumberStyles.Any, culture, out var stock) || cost < 0 || price < 0 || stock < 0) { errors.Add($"Satır {excelRow.RowNumber()}: fiyat/stok geçersiz."); continue; } var currency = Text(excelRow, "Currency"); var vatText = Text(excelRow, "VatRate"); var vat = vatText.Length == 0 ? 20m : decimal.TryParse(vatText, NumberStyles.Any, culture, out var parsedVat) ? parsedVat : -1m; if (vat is < 0 or > 100) { errors.Add($"Satır {excelRow.RowNumber()}: KDV oranı 0-100 arası olmalı."); continue; } rows.Add(new CatalogProduct { Sku = sku, Barcode = barcode, Name = Text(excelRow, "Name"), Brand = Text(excelRow, "Brand"), Category = Text(excelRow, "Category"), Description = Text(excelRow, "Description"), Cost = cost, Price = price, Currency = currency.Length == 3 ? currency : "TRY", VatRate = vat, Stock = stock, Active = !string.Equals(Text(excelRow, "Active"), "false", StringComparison.OrdinalIgnoreCase), Gtin = Text(excelRow, "Gtin"), ImageUrls = Text(excelRow, "ImageUrls") }); } return new(rows, errors);
    }
    public static ImportSummary Apply(CatalogStore store, XmlSource source, ExcelPreview preview) { ValidatePreview(preview); source.UpdateName = true; source.UpdateDescription = true; source.UpdateImages = true; source.Fields["Gtin"] = "excel"; var rows = preview.Rows.Select(p => { p.SourceId = source.Id; p.SourceKind = "excel"; p.PriceSource = "excel"; p.StockSource = "excel"; p.MediaSource = "excel"; p.SourceUpdatedUtc = DateTime.UtcNow; return p; }).ToList(); return store.Import(source, rows); }
    public static ImportSummary Apply(CatalogStore store, XmlSource source, ExcelPreview preview, IReadOnlyCollection<int> selectedRows) { if (selectedRows.Count == 0) throw new InvalidOperationException("Uygulamak için en az bir satır seçin."); if (preview.Errors.Count > 0) throw new InvalidOperationException("Hatalı Excel satırları düzeltilmeden katalog güncellenemez."); var rows = preview.Rows.Where((_, index) => selectedRows.Contains(index)).ToList(); if (rows.Count != selectedRows.Count) throw new InvalidOperationException("Seçili satır numarası önizleme dışında."); return Apply(store, source, new ExcelPreview(rows, Array.Empty<string>())); }
    public static CatalogUndoReceipt ApplyWithUndo(CatalogStore store, XmlSource source, ExcelPreview preview, IReadOnlyCollection<int> selectedRows) { if (selectedRows.Count == 0) throw new InvalidOperationException("Uygulamak için en az bir satır seçin."); ValidatePreview(preview); var rows = preview.Rows.Where((_, index) => selectedRows.Contains(index)).ToList(); if (rows.Count != selectedRows.Count) throw new InvalidOperationException("Seçili satır numarası önizleme dışında."); source.UpdateName = true; source.UpdateDescription = true; source.UpdateImages = true; source.Fields["Gtin"] = "excel"; rows.ForEach(p => { p.SourceId = source.Id; p.SourceKind = "excel"; p.PriceSource = "excel"; p.StockSource = "excel"; p.MediaSource = "excel"; p.SourceUpdatedUtc = DateTime.UtcNow; }); return store.ImportWithUndo(source, rows); }
    static void ValidatePreview(ExcelPreview preview) { if (preview.Errors.Count > 0) throw new InvalidOperationException("Hatalı Excel satırları düzeltilmeden katalog güncellenemez."); foreach (var row in preview.Rows) if (row.Currency.Trim().ToUpperInvariant() is not ("TRY" or "USD" or "EUR" or "GBP")) throw new InvalidOperationException($"Excel döviz değeri desteklenmiyor: {row.Currency}."); }
    /// ClosedXML's SaveAs validates the target file's extension (.xlsx/.xlsm/.xltx/
    /// .xltm); appending ".tmp-<guid>" after ".xlsx" makes GetExtension see ".tmp-…"
    /// and reject it. Keep the real extension on the temp file instead.
    static string TempSiblingPath(string path) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $"{Path.GetFileNameWithoutExtension(path)}.tmp-{Guid.NewGuid():N}{Path.GetExtension(path)}");
    static void EnsureFile(string path) { if (!File.Exists(path)) throw new FileNotFoundException("Excel dosyası bulunamadı.", path); if (new FileInfo(path).Length > 100L * 1024 * 1024) throw new InvalidDataException("Excel dosyası 100 MB sınırını aşıyor."); }
}
