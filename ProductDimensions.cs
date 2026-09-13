using System.Globalization;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

/// <summary>The desi a shipping calculation may take from a product: the value, where it came from (manual or derived), the readiness status and the words.</summary>
public sealed record DesiInput(decimal? Desi, string Origin, string Status, string Words)
{
    public const string Ready = "READY", NeedsDimensions = "NEEDS_DIMENSIONS", Inconsistent = "INCONSISTENT";
    public bool IsReady => Status == Ready;
}

/// <summary>
/// Dimension consistency and the desi input (#908). A product's box is three canonical lengths (#907) that stand
/// or fall together: a side missing, zero or negative is refused, never guessed. The desi (L × W × H / 3000) is
/// derived from the box when the operator has entered none and follows the box when a feed or the operator
/// changes it; a desi the operator typed is theirs — kept through box changes, stamped manual, and compared with
/// the box (a warning when the two disagree). This is provenance and readiness only: no carrier, no rate, no
/// connector — the input a rate table (#319) needs, with the reason when there is none.
/// </summary>
public static class ProductDimensions
{
    public const decimal DesiDivisor = 3000m;
    public const string DesiField = "Desi";
    /// <summary>A typed desi that differs from the box's by more than this share of the larger one, and by more than half a desi, disagrees with it; less is rounding.</summary>
    public const decimal MismatchShare = 0.2m, MismatchFloor = 0.5m;

    static readonly (string Key, string Word)[] Sides = { ("Length", "uzunluk"), ("Width", "genişlik"), ("Height", "yükseklik") };

    /// <summary>The box's desi when the three sides are known and positive; null otherwise. Exact decimal arithmetic, two decimals — the parse limit (#907) keeps the product inside <see cref="decimal"/>.</summary>
    public static decimal? DesiOf(CatalogProduct product)
    {
        ArgumentNullException.ThrowIfNull(product);
        return product.LengthCm is { } l && product.WidthCm is { } w && product.HeightCm is { } h && l > 0 && w > 0 && h > 0 ? Math.Round(l * w * h / DesiDivisor, 2) : null;
    }

    public static bool IsDerived(CatalogProduct product) => FieldProvenance.Of(product, DesiField) is { Kind: FieldProvenance.DerivedKind };

    /// <summary>Consistency findings for a record: a partial box (blocking, the missing sides named), a side or a desi at zero or below (blocking), a desi that disagrees with the box (warning).</summary>
    public static IReadOnlyList<UnitFinding> Findings(CatalogProduct product)
    {
        ArgumentNullException.ThrowIfNull(product);
        var findings = new List<UnitFinding>();
        var sides = new[] { product.LengthCm, product.WidthCm, product.HeightCm };
        var known = sides.Count(s => s is not null);
        if (known > 0 && known < 3)
            findings.Add(new(true, "Boyut", "Boyut eksik: " + string.Join(", ", Sides.Where((_, i) => sides[i] is null).Select(s => s.Word)) + " girilmemiş; üç kenar da gerekli."));
        var nonPositive = Sides.Where((_, i) => sides[i] is { } v && v <= 0).Select(s => s.Word).ToArray();
        if (nonPositive.Length > 0) findings.Add(new(true, "Boyut", "Boyut pozitif olmalı: " + string.Join(", ", nonPositive) + " sıfır veya negatif."));
        if (product.Desi is { } desi && desi <= 0) findings.Add(new(true, "Desi", "Desi pozitif olmalı; sıfır veya negatif olamaz."));
        if (product.Desi is { } typed && typed > 0 && DesiOf(product) is { } derived && Disagree(typed, derived))
            findings.Add(new(false, "Desi", IsDerived(product)
                ? $"Desi ({N(typed)}) boyutla güncel değil (boyuttan {N(derived)}); bir sonraki kayıtta yeniden hesaplanır."
                : $"Elle girilen desi ({N(typed)}) boyuttan hesaplanana ({N(derived)}) uymuyor; kargo hesabı elle girileni kullanır."));
        return findings;
    }

    /// <summary>The desi a shipping calculation may take: the operator's own when typed, the box's when derived, or none with the reason.</summary>
    public static DesiInput Resolve(CatalogProduct product)
    {
        ArgumentNullException.ThrowIfNull(product);
        if (Findings(product).Any(f => f.Blocking)) return new(null, "", DesiInput.Inconsistent, "boyut veya desi tutarsız; kargo hesabına girdi olamaz");
        var derived = DesiOf(product);
        if (product.Desi is { } desi && desi > 0)
        {
            if (IsDerived(product)) return new(desi, FieldProvenance.DerivedKind, DesiInput.Ready, $"{N(desi)} desi · boyuttan hesaplandı ({Box(product)})");
            var words = derived is { } d
                ? (Disagree(desi, d) ? $"{N(desi)} desi · elle girildi · boyuttan hesaplanan {N(d)} ile uyuşmuyor" : $"{N(desi)} desi · elle girildi · boyuttan {N(d)}")
                : $"{N(desi)} desi · elle girildi";
            return new(desi, FieldProvenance.ManualKind, DesiInput.Ready, words);
        }
        if (derived is { } fromBox) return new(fromBox, FieldProvenance.DerivedKind, DesiInput.Ready, $"{N(fromBox)} desi · boyuttan hesaplandı ({Box(product)})");
        return new(null, "", DesiInput.NeedsDimensions, "desi yok: boyut (U x G x Y) veya desi girilmemiş");
    }

    /// <summary>Feed path (no operator in the loop): a derived or absent desi follows the box — set from it, cleared with it; a manual or legacy desi is the operator's and is kept. Returns true when the desi changed.</summary>
    public static bool Refresh(CatalogProduct product, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(product);
        if (product.Desi is not null && !IsDerived(product)) return false;
        var derived = DesiOf(product);
        var changed = product.Desi != derived;
        product.Desi = derived;
        if (derived is null) product.FieldOrigins?.Remove(DesiField);
        else if (changed || !IsDerived(product)) Stamp(product, nowUtc);
        return changed;
    }

    /// <summary>Operator path, after <see cref="FieldProvenance.StampManual"/>: a desi the operator typed is theirs (already stamped manual); one they cleared forgets the manual claim and, like a derived one, follows the box.</summary>
    public static void Apply(CatalogProduct before, CatalogProduct after, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(before); ArgumentNullException.ThrowIfNull(after);
        if (after.Desi != before.Desi && after.Desi is not null) return;
        if (after.Desi != before.Desi) after.FieldOrigins?.Remove(DesiField);
        Refresh(after, nowUtc);
    }

    /// <summary>One line for the drawer: the desi, where it came from and the box it came from — or why there is none.</summary>
    public static string Describe(CatalogProduct product) => Resolve(product).Words;

    static bool Disagree(decimal typed, decimal derived)
    {
        var gap = Math.Abs(typed - derived);
        return gap > MismatchFloor && gap > Math.Max(typed, derived) * MismatchShare;
    }

    static void Stamp(CatalogProduct product, DateTime nowUtc)
    {
        product.FieldOrigins ??= new Dictionary<string, FieldOrigin>(StringComparer.Ordinal);
        product.FieldOrigins[DesiField] = new FieldOrigin { Kind = FieldProvenance.DerivedKind, ObservedUtc = nowUtc };
    }

    static string Box(CatalogProduct p) => $"{N(p.LengthCm ?? 0)} × {N(p.WidthCm ?? 0)} × {N(p.HeightCm ?? 0)} cm";
    static string N(decimal value) => value.ToString("0.##", CultureInfo.CurrentCulture);
}
