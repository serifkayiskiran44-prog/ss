using ClosedXML.Excel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TrMarketplaceHubDesktop.Catalog;

public sealed record ExcelSheetColumn(int Number, string Letter, string Header, string Sample)
{
    public string Label => $"{Letter}  ·  {(Header.Length == 0 ? "Başlıksız" : Header)}  ·  {Sample}";
}

internal sealed class ExcelWorkbookData : IDisposable
{
    readonly XLWorkbook workbook;
    readonly MemoryStream stream;
    public IXLWorksheet Sheet { get; }
    public ExcelImportProfile Profile { get; }
    public string FileHash { get; }
    public Dictionary<string, int> Columns { get; }
    public static string HashFile(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }
    public static string Signature(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    public ExcelWorkbookData(string path, ExcelImportProfile profile)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Excel dosyası bulunamadı.", path);
        if (new FileInfo(path).Length > 100L * 1024 * 1024) throw new InvalidDataException("Excel dosyası 100 MB sınırını aşıyor.");
        if (profile.HeaderRow is < 1 or > 1048575) throw new InvalidOperationException("Başlık satırı geçersiz.");
        var bytes = File.ReadAllBytes(path);
        FileHash = Convert.ToHexString(SHA256.HashData(bytes));
        stream = new MemoryStream(bytes, false);
        workbook = new XLWorkbook(stream);
        try
        {
            Profile = profile;
            Sheet = string.IsNullOrWhiteSpace(profile.SheetName) ? workbook.Worksheets.First() : workbook.Worksheets.FirstOrDefault(s => s.Name.Equals(profile.SheetName, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("Seçilen Excel sayfası bulunamadı.");
            if (!Sheet.Row(profile.HeaderRow).CellsUsed().Any()) throw new InvalidOperationException("Başlık satırı boş.");
            Columns = profile.UseColumnLetters || profile.ColumnLetters.Count > 0
                ? profile.ColumnLetters.Where(p => !string.IsNullOrWhiteSpace(p.Value)).ToDictionary(p => p.Key, p => ColumnNumber(p.Value), StringComparer.OrdinalIgnoreCase)
                : new(ExcelColumnMapping.FromProfile(Sheet.Row(profile.HeaderRow), profile).Columns, StringComparer.OrdinalIgnoreCase);
            var last = Sheet.LastColumnUsed()?.ColumnNumber() ?? 0;
            if (Columns.Values.Any(c => c > last)) throw new InvalidOperationException("Eşlenen sütun çalışma sayfasının dışında.");
        }
        catch { workbook.Dispose(); stream.Dispose(); throw; }
    }
    public static int ColumnNumber(string letter)
    {
        var text = letter.Trim().ToUpperInvariant();
        if (text.Length is < 1 or > 3 || text.Any(c => c < 'A' || c > 'Z')) throw new InvalidOperationException("Sütun A, B, C … AA biçiminde olmalı.");
        var number = text.Aggregate(0, (n, c) => n * 26 + c - 'A' + 1);
        if (number > 16384) throw new InvalidOperationException("Excel sütunu XFD sınırını aşıyor.");
        return number;
    }
    public static string ColumnLetter(int number) => XLHelper.GetColumnLetterFromNumber(number);
    public IEnumerable<IXLRow> Rows => Sheet.RowsUsed().Where(r => r.RowNumber() > Profile.HeaderRow);
    public bool Mapped(string field) => Columns.ContainsKey(field) || Profile.Defaults.ContainsKey(field);
    public string Text(IXLRow row, string field)
    {
        if (!Columns.TryGetValue(field, out var column)) return Profile.Defaults.GetValueOrDefault(field, "").Trim();
        var cell = row.Cell(column);
        if (cell.HasFormula) throw new InvalidOperationException($"{field}: formül yerine hesaplanmış sabit değer kullanın.");
        return cell.GetFormattedString(CultureInfo.InvariantCulture).Trim();
    }
    public decimal Number(IXLRow row, string field)
    {
        if (Columns.TryGetValue(field, out var col) && row.Cell(col).DataType == XLDataType.Number && !row.Cell(col).HasFormula)
            return row.Cell(col).GetValue<decimal>();
        var text = Text(row, field);
        var culture = ExcelProfileStore.Culture(Profile.CultureName);
        var dec = Regex.Escape(culture.NumberFormat.NumberDecimalSeparator);
        var group = Regex.Escape(culture.NumberFormat.NumberGroupSeparator);
        if (!Regex.IsMatch(text, @"^[+-]?(?:[0-9]+|[0-9]{1,3}(?:" + group + @"[0-9]{3})+)(?:" + dec + @"[0-9]+)?$"))
            throw new InvalidOperationException($"{field}: sayı biçimi geçersiz ({culture.Name}).");
        if (!decimal.TryParse(text, NumberStyles.Number, culture, out var result)) throw new InvalidOperationException($"{field}: sayı sınırı aşıldı.");
        return result;
    }
    public void Dispose() { workbook.Dispose(); stream.Dispose(); }
}
