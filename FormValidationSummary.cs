namespace TrMarketplaceHubDesktop;

/// <summary>One line of the summary: what is wrong, where it lives, and which input to focus to fix it.</summary>
public sealed record FormValidationLink(string Field, string Property, string Section, string Message, SeverityLevel Level)
{
    public bool CanFocus => Property.Length > 0;
}

/// <summary>The form-level view: the roll-up, the ordered links, the first input to focus, and whether the save may proceed.</summary>
public sealed record FormValidationView(SeverityAggregate Aggregate, IReadOnlyList<FormValidationLink> Links, FormValidationLink? FirstBlocking)
{
    public bool CanSave => !Aggregate.HasBlocking;
    public bool IsClear => Links.Count == 0;
}

/// <summary>
/// The form-level validation summary (#820). A save command evaluates the whole record, and this turns the
/// findings into what the top of the form shows and what the save does: blocking findings first in the form's
/// own section order, warnings after, information left out; each finding linked to the input that fixes it
/// through the form's field-to-property table (a finding whose field the form does not own is listed without a
/// focus target rather than dropped); the first blocking link is what the save focuses before refusing. The
/// security line: a message never carries what the operator typed -- the rule text is the message, and if a
/// message does contain an entered value it is masked before it reaches the summary.
/// </summary>
public static class FormValidationSummary
{
    /// <summary>The product form's fields as ProductValidation names them, to the properties its inputs bind.</summary>
    public static IReadOnlyDictionary<string, string> ProductPropertyByField { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Başlık"] = "Name", ["SKU"] = "Sku", ["Barkod"] = "Barcode", ["Alış fiyatı"] = "Cost", ["KDV oranı"] = "VatRate",
        ["Satış fiyatı"] = "Price", ["Satış para birimi"] = "Currency", ["Stok"] = "Stock", ["Açıklama"] = "Description", ["Görseller"] = "ImageUrls",
    };

    public static FormValidationView Compose(ProductValidationResult result, IReadOnlyDictionary<string, string> propertyByField, IReadOnlyDictionary<string, string>? enteredValues = null)
    {
        ArgumentNullException.ThrowIfNull(result); ArgumentNullException.ThrowIfNull(propertyByField);
        var links = new List<FormValidationLink>();
        foreach (var finding in result.Findings.Where(f => f.Severity != ProductValidation.Info))
        {
            var level = SeverityStyle.FromValidation(finding.Severity);
            var property = propertyByField.TryGetValue(finding.Field, out var p) ? p : "";
            links.Add(new(finding.Field, property, finding.Section, Mask(finding.Message, enteredValues), level));
        }
        // Blocking first, each group in the form's own order (Findings are already in section order).
        var ordered = links.Where(l => l.Level == SeverityLevel.Blocking).Concat(links.Where(l => l.Level != SeverityLevel.Blocking)).ToList();
        var aggregate = SeverityStyle.Aggregate(ordered.Select(l => (l.Level, l.Message)).ToList());
        var first = ordered.FirstOrDefault(l => l.Level == SeverityLevel.Blocking && l.CanFocus) ?? ordered.FirstOrDefault(l => l.Level == SeverityLevel.Blocking);
        return new(aggregate, ordered, first);
    }

    /// <summary>A rule message is about the rule; if it quotes what was typed, the quote is masked.</summary>
    public static string Mask(string message, IReadOnlyDictionary<string, string>? enteredValues)
    {
        var safe = message ?? "";
        if (enteredValues is null) return safe;
        foreach (var value in enteredValues.Values.Where(v => !string.IsNullOrWhiteSpace(v) && v.Trim().Length >= 4).Select(v => v.Trim()).OrderByDescending(v => v.Length))
            safe = safe.Replace(value, "…", StringComparison.OrdinalIgnoreCase);
        return safe;
    }
}
