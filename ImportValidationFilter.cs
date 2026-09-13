using System.Text.RegularExpressions;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public enum ImportRowChange { New, Changed, Unchanged }

/// <summary>One preview row's validation, reduced to what a filter needs: its highest level, the fields and codes that fired, and whether it changes anything.</summary>
public sealed record ImportValidationRow(int Index, SeverityLevel Highest, IReadOnlySet<string> Fields, IReadOnlySet<string> ReasonCodes, ImportRowChange Change, string StatusLabel);

public sealed record ImportValidationFilterState(SeverityLevel? Severity = null, string? Field = null, string? ReasonCode = null, bool ChangedOnly = false)
{
    public bool IsEmpty => Severity is null && Field is null && ReasonCode is null && !ChangedOnly;
}

public sealed record ImportValidationCounts(int Total, int Matching, int Blocking, int Warning, int Info, int Clean, int Changed)
{
    public string Label => $"{Matching:N0} / {Total:N0} satır · {Blocking:N0} engel · {Warning:N0} uyarı · {Changed:N0} değişen";
}

public sealed record ImportValidationFilterResult(IReadOnlyList<int> MatchingIndices, ImportValidationCounts Counts, IReadOnlyList<string> FieldOptions, IReadOnlyList<string> ReasonCodeOptions)
{
    public bool IsEmptyResult => MatchingIndices.Count == 0 && Counts.Total > 0;
}

/// <summary>
/// Filters over the import preview's validation results (#825). Each preview row is evaluated once with the
/// catalogue's own rules and reduced to flags -- highest severity, the fields that fired, stable reason codes,
/// and whether the row would add or change anything in the pool -- so that filtering a hundred thousand rows
/// is a pass over flags, not a re-validation. Filters combine (severity AND field AND reason code AND
/// changed-only) and the counts always describe the filtered set, so the numbers next to the controls never
/// disagree with the grid. Filter options are field names and reason codes derived from the rules, never a
/// value from a row: a supplier's product name or price is not a label anyone else should read.
/// </summary>
public static class ImportValidationFilter
{
    /// <summary>A stable code for a finding: section and field, ASCII-folded, so the same rule always files under the same code.</summary>
    public static string ReasonCode(ProductValidationFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return $"{Fold(finding.Section)}:{Fold(finding.Field)}";
    }

    static string Fold(string value)
    {
        var lower = (value ?? "").Trim().ToLower(System.Globalization.CultureInfo.GetCultureInfo("tr-TR"))
            .Replace('ı', 'i').Replace('ş', 's').Replace('ğ', 'g').Replace('ü', 'u').Replace('ö', 'o').Replace('ç', 'c');
        return Regex.Replace(lower, @"[^a-z0-9]+", "-").Trim('-');
    }

    public static ImportRowChange Classify(CatalogProduct row, CatalogProduct? existing)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (existing is null) return ImportRowChange.New;
        var same = string.Equals(existing.Name, row.Name, StringComparison.Ordinal) && existing.Price == row.Price && existing.Cost == row.Cost && existing.Stock == row.Stock
            && string.Equals(existing.Currency, row.Currency, StringComparison.Ordinal) && string.Equals(existing.Description, row.Description, StringComparison.Ordinal)
            && string.Equals(existing.Brand, row.Brand, StringComparison.Ordinal) && string.Equals(existing.Category, row.Category, StringComparison.Ordinal) && string.Equals(existing.ImageUrls, row.ImageUrls, StringComparison.Ordinal);
        return same ? ImportRowChange.Unchanged : ImportRowChange.Changed;
    }

    /// <summary>Evaluates every preview row once. <paramref name="existingFor"/> looks the row up in the pool (by SKU, then barcode) and may return null.</summary>
    public static IReadOnlyList<ImportValidationRow> Evaluate(IReadOnlyList<CatalogProduct> rows, Func<CatalogProduct, CatalogProduct?> existingFor)
    {
        ArgumentNullException.ThrowIfNull(rows); ArgumentNullException.ThrowIfNull(existingFor);
        var result = new List<ImportValidationRow>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var findings = ProductValidation.Evaluate(rows[i]).Findings;
            var highest = SeverityStyle.Highest(findings.Select(f => SeverityStyle.FromValidation(f.Severity)));
            var fields = findings.Select(f => f.Field).ToHashSet(StringComparer.Ordinal);
            var codes = findings.Select(ReasonCode).ToHashSet(StringComparer.Ordinal);
            var change = Classify(rows[i], existingFor(rows[i]));
            var level = findings.Count == 0 ? SeverityLevel.Info : highest;
            var label = findings.Count == 0 ? "✔ temiz" : SeverityStyle.For(level, false).Badge;
            result.Add(new(i, findings.Count == 0 ? SeverityLevel.Info : highest, fields, codes, change, $"{label} · {ChangeLabel(change)}"));
        }
        return result;
    }

    public static string ChangeLabel(ImportRowChange change) => change switch { ImportRowChange.New => "yeni", ImportRowChange.Changed => "değişen", _ => "aynı" };

    public static ImportValidationFilterResult Apply(IReadOnlyList<ImportValidationRow> rows, ImportValidationFilterState state)
    {
        ArgumentNullException.ThrowIfNull(rows); ArgumentNullException.ThrowIfNull(state);
        var matching = new List<int>();
        int blocking = 0, warning = 0, info = 0, clean = 0, changed = 0;
        foreach (var row in rows)
        {
            if (state.Severity is { } severity && !(severity == SeverityLevel.Info ? row.Highest == SeverityLevel.Info && row.Fields.Count > 0 : row.Highest == severity)) continue;
            if (state.Field is { Length: > 0 } field && !row.Fields.Contains(field)) continue;
            if (state.ReasonCode is { Length: > 0 } code && !row.ReasonCodes.Contains(code)) continue;
            if (state.ChangedOnly && row.Change == ImportRowChange.Unchanged) continue;
            matching.Add(row.Index);
            if (row.Fields.Count == 0) clean++;
            else if (row.Highest == SeverityLevel.Blocking) blocking++;
            else if (row.Highest == SeverityLevel.Warning) warning++;
            else info++;
            if (row.Change != ImportRowChange.Unchanged) changed++;
        }
        // Options come from the rules that fired across all rows -- never from a row's values.
        var fields = rows.SelectMany(r => r.Fields).Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase).ToList();
        var codes = rows.SelectMany(r => r.ReasonCodes).Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();
        return new(matching, new(rows.Count, matching.Count, blocking, warning, info, clean, changed), fields, codes);
    }
}
