using System.Text.RegularExpressions;

namespace TrMarketplaceHubDesktop;

public enum MappingRowStatus { Mapped, Optional, MissingRequired, DuplicateTarget, UnknownPath }

/// <summary>One target field of the catalogue as the mapping screen knows it.</summary>
public sealed record MappingRowInput(string Key, string Label, string Path);

/// <summary>One row of the readable mapping table: what it needs, what it is, what it looks like, and what is wrong.</summary>
public sealed record MappingRowView(string Key, string Label, string Path, bool Required, string TypeLabel, string Sample, MappingRowStatus Status, string StatusLabel, string Reason)
{
    public bool IsProblem => Status is MappingRowStatus.MissingRequired or MappingRowStatus.DuplicateTarget or MappingRowStatus.UnknownPath;
}

public sealed record MappingTableView(IReadOnlyList<MappingRowView> Rows, int Mapped, int MissingRequired, int Duplicates, int Unknown, string Summary, string? FirstProblemKey)
{
    public bool HasBlocking => MissingRequired > 0 || Duplicates > 0 || Unknown > 0;
}

/// <summary>
/// The import mapping table, made readable (#824): each target field shows whether the catalogue requires it,
/// what type it expects, a sample of what the chosen XML path actually holds, and a status word with a reason --
/// a required field left empty, an XML path mapped onto two target fields (one of them is a mistake), a path
/// the scan did not find. Samples are evidence from the supplier's file and are treated as untrusted: they pass
/// the uncapped redaction, a raw body is replaced, and they are cut to one short line. This adds no XML variant
/// mapping; it describes the mapping that exists.
/// </summary>
public static class ImportMappingTable
{
    public const int SampleLength = 60;

    /// <summary>
    /// The fields the catalogue cannot import without, as groups -- the same rule the mapping snapshot enforces:
    /// a product needs a SKU <b>or</b> a barcode, and a name. A group is satisfied by any one member.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> RequiredGroups { get; } = new[] { new[] { "Sku", "Barcode" }, new[] { "Name" } };

    /// <summary>The label a required row shows: plain for a single-member group, "veya …" for an alternative.</summary>
    public static string RequiredLabel(string key, IReadOnlyList<IReadOnlyList<string>>? groups = null)
    {
        var group = (groups ?? RequiredGroups).FirstOrDefault(g => g.Contains(key));
        if (group is null) return "";
        var others = group.Where(k => k != key).Select(k => k switch { "Sku" => "SKU", "Barcode" => "barkod", "Name" => "ürün adı", _ => k }).ToList();
        return others.Count == 0 ? "✱ zorunlu" : $"✱ zorunlu (veya {string.Join(" / ", others)})";
    }

    public static string TypeLabel(string key) => key switch
    {
        "Price" or "Cost" => "ondalık sayı",
        "Stock" => "tam sayı",
        "ImageUrls" => "URL listesi (| ile)",
        "Barcode" or "Gtin" => "kod",
        "Currency" => "3 harfli kod",
        "Description" => "uzun metin",
        _ => "metin",
    };

    /// <summary>A sample is a specimen of untrusted supplier data: redacted, never a raw body, one short line.</summary>
    public static string SafeSample(string? raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0) return "";
        if (StatusTooltip.LooksLikeRawPayload(text)) return StatusTooltip.RawPayloadHidden;
        text = Regex.Replace(AuditStore.Redact(text), @"\s+", " ").Trim();
        return text.Length <= SampleLength ? text : text[..(SampleLength - 1)] + "…";
    }

    public static MappingTableView Compose(IReadOnlyList<MappingRowInput> rows, IReadOnlyCollection<string> knownPaths, Func<string, string?> sampleFor, IReadOnlyList<IReadOnlyList<string>>? requiredGroups = null)
    {
        ArgumentNullException.ThrowIfNull(rows); ArgumentNullException.ThrowIfNull(knownPaths); ArgumentNullException.ThrowIfNull(sampleFor);
        var groups = requiredGroups ?? RequiredGroups;
        var mappedKeys = rows.Where(r => Paths(r.Path).Any()).Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        // A group is unsatisfied only when none of its members is mapped; then every empty member is flagged.
        var unsatisfied = groups.Where(g => !g.Any(mappedKeys.Contains)).SelectMany(g => g).ToHashSet(StringComparer.Ordinal);
        var required = groups.SelectMany(g => g).ToHashSet(StringComparer.Ordinal);
        var known = new HashSet<string>(knownPaths, StringComparer.Ordinal);
        // A path used by more than one target field is a conflict on every field that uses it (images may list several paths).
        var usage = rows.SelectMany(r => Paths(r.Path).Select(p => (r.Key, Path: p))).GroupBy(x => x.Path, StringComparer.Ordinal).Where(g => g.Select(x => x.Key).Distinct().Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);

        var views = new List<MappingRowView>();
        foreach (var row in rows)
        {
            var isRequired = required.Contains(row.Key);
            var paths = Paths(row.Path).ToList();
            var sample = paths.Count == 0 ? "" : SafeSample(sampleFor(paths[0]));
            MappingRowStatus status; string reason;
            if (paths.Count == 0) { var missingHere = unsatisfied.Contains(row.Key); status = missingHere ? MappingRowStatus.MissingRequired : MappingRowStatus.Optional; reason = missingHere ? "Zorunlu alan eşlenmedi; içe aktarım bu alan olmadan başlamaz." : isRequired ? "Grubun başka bir alanı eşli; bu alan boş kalabilir." : ""; }
            else if (paths.Any(usage.Contains)) { status = MappingRowStatus.DuplicateTarget; reason = $"'{paths.First(usage.Contains)}' yolu birden fazla alana eşlenmiş; biri yanlış olmalı."; }
            else if (known.Count > 0 && paths.Any(p => !known.Contains(p))) { status = MappingRowStatus.UnknownPath; reason = $"'{paths.First(p => !known.Contains(p))}' yolu XML'de bulunamadı."; }
            else { status = MappingRowStatus.Mapped; reason = ""; }
            views.Add(new(row.Key, row.Label, row.Path, isRequired, TypeLabel(row.Key), sample, status, StatusLabel(status), reason));
        }
        var mapped = views.Count(v => v.Status == MappingRowStatus.Mapped);
        var missing = views.Count(v => v.Status == MappingRowStatus.MissingRequired);
        var duplicates = views.Count(v => v.Status == MappingRowStatus.DuplicateTarget);
        var unknown = views.Count(v => v.Status == MappingRowStatus.UnknownPath);
        var parts = new List<string> { $"{mapped} eşli" };
        if (missing > 0) parts.Add($"{missing} zorunlu eksik");
        if (duplicates > 0) parts.Add($"{duplicates} çakışma");
        if (unknown > 0) parts.Add($"{unknown} bulunamayan yol");
        var optional = views.Count(v => v.Status == MappingRowStatus.Optional);
        if (optional > 0) parts.Add($"{optional} isteğe bağlı boş");
        var summary = string.Join(" · ", parts) + (missing + duplicates + unknown == 0 ? " — eşleme hazır." : " — önce sorunları düzeltin.");
        return new(views, mapped, missing, duplicates, unknown, summary, views.FirstOrDefault(v => v.IsProblem)?.Key);
    }

    public static string StatusLabel(MappingRowStatus status) => status switch
    {
        MappingRowStatus.Mapped => "✔ eşli",
        MappingRowStatus.Optional => "○ boş (isteğe bağlı)",
        MappingRowStatus.MissingRequired => "✖ zorunlu eksik",
        MappingRowStatus.DuplicateTarget => "⚠ çakışma",
        _ => "⚠ yol bulunamadı",
    };

    static IEnumerable<string> Paths(string? path) => (path ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
