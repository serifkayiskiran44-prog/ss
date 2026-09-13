using System.Globalization;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

/// <summary>One catalogue entry: the code stored on the product, the VAT percentage the existing money logic takes, and the operator's label.</summary>
public sealed record TaxClassEntry(string Code, decimal RatePercent, string Label, bool Exempt = false);

/// <summary>One change of a product's tax class: the code it changed to, the rate that code carried then, the moment and the origin (feed or operator).</summary>
public sealed class TaxClassChange
{
    public string Code { get; set; } = "";
    public decimal? RatePercent { get; set; }
    public DateTime ChangedUtc { get; set; }
    public string Origin { get; set; } = "";
}

/// <summary>The product's tax class as the pricing logic sees it: MISSING (no class; the store rule's VAT rate applies), KNOWN (a catalogue code with its rate) or UNKNOWN (a code the catalogue does not know; no rate, nothing guessed).</summary>
public sealed record TaxClassResolution(string State, string Code, decimal? RatePercent, string Words)
{
    public const string Missing = "MISSING", Known = "KNOWN", Unknown = "UNKNOWN";
    public bool IsKnown => State == Known;
}

/// <summary>
/// Product tax class (#909). The class is typed metadata on the product — one of the catalogue's codes — and it
/// reaches a price only through the money logic that already exists (#285's gate in the price preview): a known
/// class supplies the VAT percentage the store rule would otherwise supply, a missing class leaves the store rule's
/// rate in charge, an unknown class supplies nothing and the preview refuses, as it does for any missing input.
/// A known class also sets the product's own VAT rate, so there is one source for it. Every change of the class
/// is kept on the record with its moment, its origin and the rate the code carried then, so an old price can be
/// read against the rate of its day after any number of restarts. No tax engine, no jurisdiction table: the
/// Turkish VAT bands the application already models as a percentage.
/// </summary>
public static class ProductTaxClass
{
    public const string Field = "TaxClass";
    public static readonly IReadOnlyList<TaxClassEntry> Catalog = new[]
    {
        new TaxClassEntry("standard", 20m, "Genel oran (%20)"),
        new TaxClassEntry("reduced", 10m, "İndirimli oran (%10)"),
        new TaxClassEntry("super-reduced", 1m, "Süper indirimli oran (%1)"),
        new TaxClassEntry("zero", 0m, "Sıfır oran (%0)"),
        new TaxClassEntry("exempt", 0m, "İstisna (KDV'siz)", Exempt: true),
    };
    /// <summary>The editor's choices: "not specified" first, then the catalogue.</summary>
    public static readonly IReadOnlyList<TaxClassEntry> Choices = new[] { new TaxClassEntry("", 0m, "(belirtilmedi — mağaza kuralının KDV'si)") }.Concat(Catalog).ToArray();

    public static string Canonical(string? code) => (code ?? "").Trim().ToLowerInvariant();
    public static TaxClassEntry? Find(string? code) { var c = Canonical(code); return c.Length == 0 ? null : Catalog.FirstOrDefault(e => e.Code == c); }

    public static TaxClassResolution Resolve(CatalogProduct product)
    {
        ArgumentNullException.ThrowIfNull(product);
        var code = Canonical(product.TaxClass);
        if (code.Length == 0) return new(TaxClassResolution.Missing, "", null, "vergi sınıfı belirtilmedi; mağaza kuralının KDV oranı kullanılır");
        var entry = Find(code);
        if (entry is null) return new(TaxClassResolution.Unknown, code, null, "vergi sınıfı tanınmıyor: " + SafeCode(code) + "; fiyat hesabında kullanılamaz");
        return new(TaxClassResolution.Known, entry.Code, entry.RatePercent, entry.Label);
    }

    /// <summary>The VAT percentage the money gate takes: the product's known class, else the store rule's rate; null for an unknown class so the gate blocks instead of guessing.</summary>
    public static decimal? RateFor(CatalogProduct product, decimal? policyRatePercent)
    {
        var resolution = Resolve(product);
        return resolution.State switch { TaxClassResolution.Known => resolution.RatePercent, TaxClassResolution.Missing => policyRatePercent, _ => null };
    }

    /// <summary>Applies a write: the code is canonicalised, a known class sets the product's VAT rate, and a change of code is appended to the history with the moment, the origin and the rate. Returns true when the class changed.</summary>
    public static bool Apply(CatalogProduct product, string origin, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(product);
        var code = Canonical(product.TaxClass); product.TaxClass = code;
        var entry = Find(code);
        if (entry is not null) product.VatRate = entry.RatePercent;
        product.TaxClassHistory ??= new List<TaxClassChange>();
        var last = product.TaxClassHistory.Count == 0 ? null : product.TaxClassHistory[^1];
        if (last is null ? code.Length == 0 : last.Code == code) return false;
        product.TaxClassHistory.Add(new TaxClassChange { Code = code, RatePercent = entry?.RatePercent, ChangedUtc = nowUtc, Origin = (origin ?? "").Trim() });
        return true;
    }

    /// <summary>Validation view: an unknown class is a warning (the price preview will refuse), a known class whose rate the record does not carry is a warning until the next save; a missing class is the ordinary state and no finding (the drawer says it).</summary>
    public static IReadOnlyList<(string Severity, string Message)> Findings(CatalogProduct product)
    {
        ArgumentNullException.ThrowIfNull(product);
        var resolution = Resolve(product);
        return resolution.State switch
        {
            TaxClassResolution.Unknown => new[] { (ProductValidation.Warning, "Vergi sınıfı tanınmıyor; fiyat hesabı bu ürün için engellenir. Katalogdaki bir sınıfı seçin.") },
            TaxClassResolution.Known when resolution.RatePercent is { } rate && product.VatRate != rate => new[] { (ProductValidation.Warning, $"KDV oranı (%{N(product.VatRate)}) vergi sınıfının oranıyla (%{N(rate)}) uyuşmuyor; bir sonraki kayıtta sınıfın oranı uygulanır.") },
            _ => Array.Empty<(string, string)>(),
        };
    }

    /// <summary>One line for the drawer: the class as resolved and, when the class ever changed, the last change with its origin.</summary>
    public static string Describe(CatalogProduct product, DateTime nowUtc)
    {
        var words = Resolve(product).Words;
        var last = product.TaxClassHistory is { Count: > 0 } history ? history[^1] : null;
        if (last is null) return words;
        var who = last.Origin.Equals(FieldProvenance.ManualKind, StringComparison.OrdinalIgnoreCase) ? "elle" : last.Origin.Equals(FieldProvenance.FeedKind, StringComparison.OrdinalIgnoreCase) ? "kaynaktan" : "bilinmeyen köken";
        return $"{words} · son değişiklik {Ago(nowUtc - last.ChangedUtc)} · {who} · {product.TaxClassHistory!.Count.ToString(CultureInfo.CurrentCulture)} kayıt";
    }

    static string SafeCode(string code) => AuditStore.Redact(code.Length > 40 ? code[..40] + "…" : code);
    static string N(decimal value) => value.ToString("0.##", CultureInfo.CurrentCulture);
    static string Ago(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalMinutes < 1) return "az önce";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} dk önce";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} sa önce";
        return $"{(int)span.TotalDays} gün önce";
    }
}
