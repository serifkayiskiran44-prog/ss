using System.Globalization;
using System.Text.RegularExpressions;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

public enum UnitKind { Weight, Length }

/// <summary>One parsed quantity: the canonical value (kg or cm) when the text could be read, the text as given, the unit found, and the diagnostic when it could not be read — blocking for a negative or out-of-range value.</summary>
public sealed record UnitQuantity(UnitKind Kind, decimal? Canonical, string Original, string Unit, string Diagnostic, bool Blocking)
{
    public bool IsKnown => Canonical is not null;
}

/// <summary>A box: three lengths in cm when readable, the text as given, and the diagnostic otherwise.</summary>
public sealed record DimensionSet(decimal? LengthCm, decimal? WidthCm, decimal? HeightCm, string Original, string Diagnostic, bool Blocking)
{
    public bool IsKnown => LengthCm is not null && WidthCm is not null && HeightCm is not null;
    /// <summary>Volumetric weight (desi) of the box: L × W × H / 3000, two decimals.</summary>
    public decimal? Desi => IsKnown ? Math.Round(LengthCm!.Value * WidthCm!.Value * HeightCm!.Value / 3000m, 2) : null;
}

public sealed record UnitFinding(bool Blocking, string Field, string Message);

/// <summary>
/// Weight and dimension units (#907). A feed or an operator writes "1,5 kg", "1500 g", "150 mm", "20 x 30 x 40 cm";
/// the product keeps the text as given (the provenance) and, beside it, the canonical value — kilograms for weight,
/// centimetres for lengths — when the text could be read with the source's number culture. A value without a unit
/// is ambiguous and stays text only ("kg mı g mı belirsiz"); an unsupported unit stays text only; a negative or
/// out-of-range value is refused. Nothing is guessed and nothing is rounded away silently.
/// </summary>
public static class ProductUnits
{
    public const decimal MaxWeightKg = 100000m, MaxLengthCm = 100000m;
    static readonly Dictionary<string, decimal> WeightToKg = new(StringComparer.OrdinalIgnoreCase) { ["kg"] = 1m, ["kilogram"] = 1m, ["g"] = 0.001m, ["gr"] = 0.001m, ["gram"] = 0.001m, ["mg"] = 0.000001m, ["t"] = 1000m, ["ton"] = 1000m };
    static readonly Dictionary<string, decimal> LengthToCm = new(StringComparer.OrdinalIgnoreCase) { ["cm"] = 1m, ["mm"] = 0.1m, ["m"] = 100m, ["metre"] = 100m, ["dm"] = 10m };
    static readonly Regex Quantity = new(@"^\s*([+-]?[0-9][0-9.,\s]*?)\s*([A-Za-zçğıöşüÇĞİÖŞÜ]+)?\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex Separator = new(@"\s*[xX×*]\s*", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static UnitQuantity Parse(string? raw, UnitKind kind, string? cultureName)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0) return new(kind, null, "", "", "", false);
        var match = Quantity.Match(text);
        if (!match.Success) return new(kind, null, text, "", "okunamadı: sayı ve birim beklenir (örn. 1,5 kg)", false);
        var unit = match.Groups[2].Value;
        var table = kind == UnitKind.Weight ? WeightToKg : LengthToCm;
        if (unit.Length == 0) return new(kind, null, text, "", kind == UnitKind.Weight ? "birim yok: kg mı g mı belirsiz; değer olduğu gibi saklandı" : "birim yok: cm mi mm mi belirsiz; değer olduğu gibi saklandı", false);
        if (!table.TryGetValue(unit, out var factor)) return new(kind, null, text, unit, $"desteklenmeyen birim: {unit}; değer olduğu gibi saklandı", false);
        var number = ReadNumber(match.Groups[1].Value, cultureName);
        if (number.Overflow) return new(kind, null, text, unit, "değer aralık dışı", true);
        if (number.Value is not { } value) return new(kind, null, text, unit, "sayı okunamadı; değer olduğu gibi saklandı", false);
        if (value < 0) return new(kind, null, text, unit, "negatif olamaz", true);
        decimal canonical;
        try { canonical = decimal.Round(value * factor, 6); } catch (OverflowException) { return new(kind, null, text, unit, "değer aralık dışı", true); }
        var max = kind == UnitKind.Weight ? MaxWeightKg : MaxLengthCm;
        if (canonical > max) return new(kind, null, text, unit, $"değer aralık dışı (en fazla {max.ToString("N0", CultureInfo.CurrentCulture)} {(kind == UnitKind.Weight ? "kg" : "cm")})", true);
        return new(kind, canonical, text, unit, "", false);
    }

    /// <summary>"20 x 30 x 40 cm", "200x300x400 mm", "20 cm x 30 cm x 40 cm": three lengths; a unit written once at the end applies to all three.</summary>
    public static DimensionSet ParseDimensions(string? raw, string? cultureName)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0) return new(null, null, null, "", "", false);
        var parts = Separator.Split(text);
        if (parts.Length != 3) return new(null, null, null, text, "üç boyut beklenir (U x G x Y, örn. 20 x 30 x 40 cm)", false);
        var last = Quantity.Match(parts[2]); var sharedUnit = last.Success ? last.Groups[2].Value : "";
        var values = new decimal?[3];
        for (var i = 0; i < 3; i++)
        {
            var piece = parts[i]; var pm = Quantity.Match(piece);
            if (pm.Success && pm.Groups[2].Value.Length == 0 && sharedUnit.Length > 0) piece = pm.Groups[1].Value + " " + sharedUnit;
            var quantity = Parse(piece, UnitKind.Length, cultureName);
            if (!quantity.IsKnown) return new(null, null, null, text, quantity.Diagnostic, quantity.Blocking);
            values[i] = quantity.Canonical;
        }
        return new(values[0], values[1], values[2], text, "", false);
    }

    /// <summary>Applies the pipeline to a product's original texts: canonical values when readable, null otherwise; the originals stay. Returns the findings (blocking ones refuse the write).</summary>
    public static IReadOnlyList<UnitFinding> Apply(CatalogProduct product, string? cultureName)
    {
        ArgumentNullException.ThrowIfNull(product);
        var findings = new List<UnitFinding>();
        var weight = Parse(product.WeightText, UnitKind.Weight, cultureName); product.WeightKg = weight.Canonical;
        if (weight.Diagnostic.Length > 0) findings.Add(new(weight.Blocking, "Ağırlık", "Ağırlık: " + weight.Diagnostic));
        var box = ParseDimensions(product.DimensionsText, cultureName); product.LengthCm = box.LengthCm; product.WidthCm = box.WidthCm; product.HeightCm = box.HeightCm;
        if (box.Diagnostic.Length > 0) findings.Add(new(box.Blocking, "Boyut", "Boyut: " + box.Diagnostic));
        return findings;
    }

    /// <summary>The validation view of a stored record, without re-reading the numbers: a text that has no canonical value beside it was ambiguous or unreadable when it was written.</summary>
    public static IReadOnlyList<UnitFinding> Findings(CatalogProduct product)
    {
        ArgumentNullException.ThrowIfNull(product);
        var findings = new List<UnitFinding>();
        if (product.WeightText.Trim().Length > 0 && product.WeightKg is null) findings.Add(new(false, "Ağırlık", "Ağırlık birimi eksik veya okunamadı; değer olduğu gibi saklandı, kg'a çevrilmedi."));
        if (product.DimensionsText.Trim().Length > 0 && (product.LengthCm is null || product.WidthCm is null || product.HeightCm is null)) findings.Add(new(false, "Boyut", "Boyut birimi eksik veya okunamadı; değer olduğu gibi saklandı, cm'ye çevrilmedi."));
        return findings;
    }

    public static string DescribeWeight(CatalogProduct product) => product.WeightKg is { } kg ? $"{kg.ToString("0.###", CultureInfo.CurrentCulture)} kg (kaynak: {product.WeightText})" : product.WeightText.Length > 0 ? $"{product.WeightText} · kg'a çevrilmedi" : "—";
    public static string DescribeDimensions(CatalogProduct product) => product.LengthCm is { } l && product.WidthCm is { } w && product.HeightCm is { } h
        ? $"{l.ToString("0.###", CultureInfo.CurrentCulture)} × {w.ToString("0.###", CultureInfo.CurrentCulture)} × {h.ToString("0.###", CultureInfo.CurrentCulture)} cm (kaynak: {product.DimensionsText})"
        : product.DimensionsText.Length > 0 ? $"{product.DimensionsText} · cm'ye çevrilmedi" : "—";

    static (decimal? Value, bool Overflow) ReadNumber(string text, string? cultureName)
    {
        var culture = CultureInfo.InvariantCulture;
        try { if (!string.IsNullOrWhiteSpace(cultureName)) culture = CultureInfo.GetCultureInfo(cultureName); } catch (CultureNotFoundException) { }
        var cleaned = text.Replace(" ", "");
        if (decimal.TryParse(cleaned, NumberStyles.Number, culture, out var value)) return (value, false);
        if (double.TryParse(cleaned, NumberStyles.Number, culture, out var big) && Math.Abs(big) > (double)decimal.MaxValue / 10) return (null, true);
        return (null, false);
    }
}
