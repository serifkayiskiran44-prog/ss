using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TrMarketplaceHubDesktop;

/// <summary>One rejected import row, in the shape a person or a script fixes it from.</summary>
public sealed record RejectedRow(int RowNumber, string ReasonCode, string Field, string SafeValue, string Message);

public sealed record RejectedRowsExportResult(bool Success, int Written, bool Cancelled, string Error)
{
    public static RejectedRowsExportResult Ok(int written) => new(true, written, false, "");
    public static readonly RejectedRowsExportResult WasCancelled = new(false, 0, true, "Dışa aktarım iptal edildi; dosya yazılmadı.");
    public static RejectedRowsExportResult Failed(string error) => new(false, 0, false, error);
}

/// <summary>
/// The rejected-rows export (#826): only the rows the import refused, in a schema that stays the same from
/// release to release -- row number, reason code, field, safe value, message -- so a fix-and-retry loop can be
/// scripted against it. The import records its refusals as sentences ("Satır 3: Stock negatif olamaz.
/// (NEGATIVE)"); <see cref="Parse"/> lifts row, field and code out of that sentence and never invents a code it
/// did not find. Values are supplier data: redacted, never a raw body, one short line. The writer is atomic and
/// cancellable -- it writes beside the target and moves at the end, a cancellation or a disk error leaves no
/// partial file behind and is reported as a result rather than thrown into the UI.
/// </summary>
public static class RejectedRowsExport
{
    public const int SchemaVersion = 1;
    public static readonly IReadOnlyList<string> Schema = new[] { "satir", "neden_kodu", "alan", "guvenli_deger", "mesaj" };
    public const string UnknownCode = "UNKNOWN";
    public const int ValueLength = 80;

    static readonly Regex Sentence = new(@"^\s*Satır\s+(?<row>\d+)\s*:\s*(?<body>.*?)\s*(?:\((?<code>[A-Z][A-Z0-9_]*)\))?\s*$", RegexOptions.Compiled);
    static readonly Regex LeadingField = new(@"^(?<field>[A-Za-zÇĞİÖŞÜçğıöşü][\w]*)\s+(?=\S)", RegexOptions.Compiled);

    /// <summary>Lifts row number, field and reason code out of the import's own sentence; what is not there stays empty or UNKNOWN.</summary>
    public static RejectedRow Parse(string error)
    {
        var text = (error ?? "").Trim();
        var m = Sentence.Match(text);
        if (!m.Success) return new(0, UnknownCode, "", "", Clean(text));
        var body = m.Groups["body"].Value.Trim();
        var code = m.Groups["code"].Success ? m.Groups["code"].Value : UnknownCode;
        var field = "";
        var lead = LeadingField.Match(body);
        // "Stock negatif olamaz." names the field first; "Ad ve kimlik boş olamaz." does not (no known field).
        if (lead.Success && KnownFields.Contains(lead.Groups["field"].Value)) field = lead.Groups["field"].Value;
        return new(int.Parse(m.Groups["row"].Value, CultureInfo.InvariantCulture), code, field, "", Clean(body));
    }

    static readonly HashSet<string> KnownFields = new(StringComparer.Ordinal) { "Sku", "Barcode", "Gtin", "Name", "Description", "Cost", "Price", "Stock", "Brand", "Category", "ImageUrls", "Currency", "CostCurrency", "VatRate", "Desi" };

    /// <summary>For callers that know the row, field, code and the raw value: the value is made safe here.</summary>
    public static RejectedRow From(int rowNumber, string reasonCode, string field, string? rawValue, string message) =>
        new(rowNumber, string.IsNullOrWhiteSpace(reasonCode) ? UnknownCode : reasonCode.Trim(), field ?? "", SafeValue(rawValue), Clean(message));

    public static string SafeValue(string? raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0) return "";
        if (StatusTooltip.LooksLikeRawPayload(text)) return StatusTooltip.RawPayloadHidden;
        text = Regex.Replace(AuditStore.Redact(text), @"\s+", " ").Trim();
        return text.Length <= ValueLength ? text : text[..(ValueLength - 1)] + "…";
    }

    static string Clean(string message) => Regex.Replace(AuditStore.Redact(message ?? ""), @"\s+", " ").Trim();

    /// <summary>Only the refused rows, in row order (unparsed sentences last, in the order they were recorded).</summary>
    public static IReadOnlyList<RejectedRow> Build(IReadOnlyList<string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return errors.Select((e, i) => (Row: Parse(e), Order: i)).OrderBy(x => x.Row.RowNumber == 0 ? int.MaxValue : x.Row.RowNumber).ThenBy(x => x.Order).Select(x => x.Row).ToList();
    }

    /// <summary>UTF-8 (with BOM, so Excel opens it) CSV with the stable header; atomic; cancellable per row.</summary>
    public static RejectedRowsExportResult WriteCsv(string path, IReadOnlyList<RejectedRow> rows, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (string.IsNullOrWhiteSpace(path)) return RejectedRowsExportResult.Failed("Dosya yolu boş.");
        string temporary;
        try { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); temporary = path + ".tmp-" + Guid.NewGuid().ToString("N"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return RejectedRowsExportResult.Failed("Dosya yazılamadı: " + AuditStore.Redact(error.Message)); }
        try
        {
            using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(true)))
            {
                writer.WriteLine(string.Join(",", Schema));
                foreach (var row in rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.WriteLine(string.Join(",", new[] { row.RowNumber.ToString(CultureInfo.InvariantCulture), row.ReasonCode, row.Field, row.SafeValue, row.Message }.Select(Csv)));
                }
            }
            File.Move(temporary, path, true);
            return RejectedRowsExportResult.Ok(rows.Count);
        }
        catch (OperationCanceledException) { return RejectedRowsExportResult.WasCancelled; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return RejectedRowsExportResult.Failed("Dosya yazılamadı: " + AuditStore.Redact(error.Message)); }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    static string Csv(string value)
    {
        var v = value ?? "";
        return v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }
}
