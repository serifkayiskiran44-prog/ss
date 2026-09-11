using System.Globalization;
using System.Text;

namespace TrMarketplaceHubDesktop;

public sealed record ReportTemplate(string Module, IReadOnlyList<string> Columns, string Separator = ";");

public static class ReportTemplateRenderer
{
    static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Id", "Sku", "Name", "Status", "ShopId", "OrderId", "Price", "Currency", "UpdatedUtc" };
    public static string Render(ReportTemplate template, IEnumerable<IReadOnlyDictionary<string, object?>> rows)
    {
        ArgumentNullException.ThrowIfNull(template); ArgumentNullException.ThrowIfNull(rows);
        if (template.Columns.Count is < 1 or > 40 || template.Columns.Any(x => !Allowed.Contains(x))) throw new InvalidOperationException("Rapor şablonunda desteklenmeyen veya gizli alan var.");
        var separator = template.Separator is ";" or "," or "\t" ? template.Separator : throw new ArgumentException("Rapor ayıracı geçersiz.");
        var sb = new StringBuilder(); sb.AppendLine(string.Join(separator, template.Columns.Select(Escape)));
        foreach (var row in rows) sb.AppendLine(string.Join(separator, template.Columns.Select(column => Format(row.TryGetValue(column, out var value) ? value : null))));
        return sb.ToString();
    }
    static string Format(object? value) => Escape(value switch { null => "", DateTime date => date.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), decimal money => money.ToString("0.################", CultureInfo.InvariantCulture), _ => AuditStore.Sanitize(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "") });
    static string Escape(string value) => value.Contains(';') || value.Contains(',') || value.Contains('"') || value.Contains('\n') ? '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"' : value;
}
