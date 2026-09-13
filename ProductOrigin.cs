using System.Globalization;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

/// <summary>The country of origin as the record resolves: MISSING (nothing given), KNOWN (a canonical ISO 3166-1 alpha-2 code with its display name) or UNKNOWN (a value given that resolves to no country; kept as given, no code).</summary>
public sealed record OriginResolution(string State, string Code, string Display, string Words)
{
    public const string Missing = "MISSING", Known = "KNOWN", Unknown = "UNKNOWN";
    public bool IsKnown => State == Known;
}

/// <summary>One change of a product's country of origin, by codes (never the raw text): "" for none, "?" for a value that resolves to no country.</summary>
public sealed record OriginChange(string FromCode, string ToCode);

/// <summary>
/// Country of origin (#910). The value is kept as given (a code or a country name, from a feed or the operator)
/// and, beside it, the canonical ISO 3166-1 alpha-2 code when the value resolves: an alpha-2 code, an alpha-3
/// code, or a country name in Turkish, English or the country's own language — exact matches only, nothing
/// guessed. The display name follows the UI language through a built-in Turkish table (the runtime only knows a
/// country's English and native names), falling back to the English name. Missing and unknown are distinct
/// states; every change of the resolved code is one audit row that names codes, never the text.
/// </summary>
public static class ProductOrigin
{
    public const string Field = "CountryOfOrigin";
    public const string ChangeAction = "origin-change";
    const int TextLimit = 40;

    static readonly Lazy<IReadOnlyDictionary<string, RegionInfo>> Regions = new(() =>
        CultureInfo.GetCultures(CultureTypes.SpecificCultures)
            .Select(c => { try { return new RegionInfo(c.Name); } catch (ArgumentException) { return null; } })
            .Where(r => r is not null && r.TwoLetterISORegionName.Length == 2 && r.TwoLetterISORegionName.All(char.IsAsciiLetterUpper))
            .GroupBy(r => r!.TwoLetterISORegionName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First()!, StringComparer.Ordinal));

    static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    /// <summary>Turkish display names for the countries a Turkish marketplace operator meets; the English name serves the rest.</summary>
    static readonly IReadOnlyDictionary<string, string> TurkishNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["TR"] = "Türkiye", ["DE"] = "Almanya", ["CN"] = "Çin", ["US"] = "Amerika Birleşik Devletleri", ["GB"] = "Birleşik Krallık", ["FR"] = "Fransa", ["IT"] = "İtalya", ["ES"] = "İspanya",
        ["NL"] = "Hollanda", ["BE"] = "Belçika", ["AT"] = "Avusturya", ["CH"] = "İsviçre", ["PL"] = "Polonya", ["CZ"] = "Çekya", ["GR"] = "Yunanistan", ["BG"] = "Bulgaristan", ["RO"] = "Romanya",
        ["HU"] = "Macaristan", ["SE"] = "İsveç", ["NO"] = "Norveç", ["DK"] = "Danimarka", ["FI"] = "Finlandiya", ["IE"] = "İrlanda", ["PT"] = "Portekiz", ["RU"] = "Rusya", ["UA"] = "Ukrayna",
        ["JP"] = "Japonya", ["KR"] = "Güney Kore", ["IN"] = "Hindistan", ["PK"] = "Pakistan", ["BD"] = "Bangladeş", ["VN"] = "Vietnam", ["TH"] = "Tayland", ["ID"] = "Endonezya", ["MY"] = "Malezya",
        ["SG"] = "Singapur", ["TW"] = "Tayvan", ["HK"] = "Hong Kong", ["AE"] = "Birleşik Arap Emirlikleri", ["SA"] = "Suudi Arabistan", ["EG"] = "Mısır", ["MA"] = "Fas", ["ZA"] = "Güney Afrika",
        ["BR"] = "Brezilya", ["MX"] = "Meksika", ["CA"] = "Kanada", ["AU"] = "Avustralya", ["NZ"] = "Yeni Zelanda", ["IL"] = "İsrail", ["IR"] = "İran", ["IQ"] = "Irak", ["AZ"] = "Azerbaycan",
        ["GE"] = "Gürcistan", ["KZ"] = "Kazakistan", ["UZ"] = "Özbekistan", ["KG"] = "Kırgızistan", ["TM"] = "Türkmenistan", ["TJ"] = "Tacikistan", ["AM"] = "Ermenistan", ["RS"] = "Sırbistan",
        ["HR"] = "Hırvatistan", ["SI"] = "Slovenya", ["SK"] = "Slovakya", ["LT"] = "Litvanya", ["LV"] = "Letonya", ["EE"] = "Estonya", ["CY"] = "Kıbrıs", ["MK"] = "Kuzey Makedonya", ["BA"] = "Bosna-Hersek",
        ["AL"] = "Arnavutluk", ["ME"] = "Karadağ", ["MD"] = "Moldova", ["BY"] = "Belarus", ["LU"] = "Lüksemburg", ["MT"] = "Malta", ["IS"] = "İzlanda", ["AR"] = "Arjantin", ["CL"] = "Şili",
        ["CO"] = "Kolombiya", ["PE"] = "Peru", ["NG"] = "Nijerya", ["KE"] = "Kenya", ["ET"] = "Etiyopya", ["TN"] = "Tunus", ["DZ"] = "Cezayir", ["LY"] = "Libya", ["SY"] = "Suriye", ["LB"] = "Lübnan",
        ["JO"] = "Ürdün", ["KW"] = "Kuveyt", ["QA"] = "Katar", ["OM"] = "Umman", ["BH"] = "Bahreyn", ["PH"] = "Filipinler", ["LK"] = "Sri Lanka", ["NP"] = "Nepal", ["MN"] = "Moğolistan",
    };

    public static string Canonical(string? code) => (code ?? "").Trim().ToUpperInvariant();
    /// <summary>True for an ISO 3166-1 alpha-2 country code the runtime knows — letters only, so a UN numeric area ("150") or a language code ("EN") is not a country.</summary>
    public static bool IsValidCode(string? code) => Canonical(code) is { Length: 2 } c && Regions.Value.ContainsKey(c);

    /// <summary>The canonical alpha-2 code for a value as given — an alpha-2 code, an alpha-3 code, or a country name in Turkish, English or the country's own language; "" when nothing matches exactly.</summary>
    public static string ResolveCode(string? value)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0) return "";
        var upper = text.ToUpperInvariant();
        if (upper.Length == 2) return Regions.Value.ContainsKey(upper) ? upper : "";
        if (upper.Length == 3 && upper.All(char.IsAsciiLetterUpper) && Regions.Value.Values.FirstOrDefault(r => r.ThreeLetterISORegionName.Equals(upper, StringComparison.OrdinalIgnoreCase)) is { } three) return three.TwoLetterISORegionName;
        foreach (var (code, name) in TurkishNames) if (string.Compare(name, text, Turkish, CompareOptions.IgnoreCase) == 0) return code;
        foreach (var region in Regions.Value.Values) if (region.EnglishName.Equals(text, StringComparison.OrdinalIgnoreCase) || region.NativeName.Equals(text, StringComparison.OrdinalIgnoreCase)) return region.TwoLetterISORegionName;
        return "";
    }

    /// <summary>The display name for a code in the given UI culture: the Turkish table for a Turkish UI, else the English name; "" for an invalid code.</summary>
    public static string DisplayName(string? code, string? cultureName)
    {
        var c = Canonical(code);
        if (!IsValidCode(c)) return "";
        if ((cultureName ?? "").StartsWith("tr", StringComparison.OrdinalIgnoreCase) && TurkishNames.TryGetValue(c, out var turkish)) return turkish;
        return Regions.Value[c].EnglishName;
    }

    public static string NativeName(string? code) => IsValidCode(code) ? Regions.Value[Canonical(code)].NativeName : "";

    public static OriginResolution Resolve(CatalogProduct product)
    {
        ArgumentNullException.ThrowIfNull(product);
        var text = (product.CountryOfOrigin ?? "").Trim();
        var code = IsValidCode(product.CountryOfOriginCode) ? Canonical(product.CountryOfOriginCode) : ResolveCode(text);
        if (code.Length > 0)
        {
            var display = DisplayName(code, CultureInfo.CurrentUICulture.Name); var native = NativeName(code);
            var words = $"{code} · {display}" + (native.Length > 0 && !native.Equals(display, StringComparison.OrdinalIgnoreCase) ? $" ({native})" : "") + (text.Length > 0 && !text.Equals(code, StringComparison.OrdinalIgnoreCase) && !text.Equals(display, StringComparison.OrdinalIgnoreCase) ? $" · kaynak: {SafeText(text)}" : "");
            return new(OriginResolution.Known, code, display, words);
        }
        if (text.Length == 0) return new(OriginResolution.Missing, "", "", "menşei belirtilmedi");
        return new(OriginResolution.Unknown, "", "", $"menşei tanınmıyor: {SafeText(text)}; ülke kodu (TR, DE) veya ülke adı girin");
    }

    /// <summary>Applies a write: the value trimmed as given, the canonical code beside it when it resolves, "" when it does not. Returns true when the code changed.</summary>
    public static bool Apply(CatalogProduct product)
    {
        ArgumentNullException.ThrowIfNull(product);
        var before = product.CountryOfOriginCode ?? "";
        product.CountryOfOrigin = (product.CountryOfOrigin ?? "").Trim();
        product.CountryOfOriginCode = ResolveCode(product.CountryOfOrigin);
        return !string.Equals(before, product.CountryOfOriginCode, StringComparison.Ordinal);
    }

    /// <summary>The state of a record before a write, for <see cref="Change"/>.</summary>
    public static (string Code, string Text) Snapshot(CatalogProduct product) => (Canonical(product.CountryOfOriginCode), (product.CountryOfOrigin ?? "").Trim());

    /// <summary>The change a write made to the resolved origin — by state code: "" none, "?" unresolved, else the country — or null when the resolution did not move.</summary>
    public static OriginChange? Change((string Code, string Text) before, CatalogProduct after)
    {
        ArgumentNullException.ThrowIfNull(after);
        var from = StateCode(before.Code, before.Text); var to = StateCode(Canonical(after.CountryOfOriginCode), (after.CountryOfOrigin ?? "").Trim());
        return from == to ? null : new(from, to);
    }

    /// <summary>The audit row for a change: module catalog, action origin-change, the codes and who — never the text as given.</summary>
    public static AuditEvent ToAudit(string productId, OriginChange change, string origin)
    {
        ArgumentNullException.ThrowIfNull(change);
        var who = string.Equals(origin, FieldProvenance.FeedKind, StringComparison.OrdinalIgnoreCase) ? "kaynaktan" : string.Equals(origin, FieldProvenance.ManualKind, StringComparison.OrdinalIgnoreCase) ? "elle" : "bilinmeyen köken";
        return new AuditEvent { Module = "catalog", Action = ChangeAction, ProductId = productId ?? "", Outcome = change.ToCode == "?" ? "Warning" : "Info", Detail = $"Menşei {Word(change.FromCode)} → {Word(change.ToCode)} · {who}" };
    }

    /// <summary>Validation view: a value that resolves to no country is a warning; missing is the ordinary state (the compliance profile says when it is required).</summary>
    public static IReadOnlyList<string> Findings(CatalogProduct product)
        => Resolve(product).State == OriginResolution.Unknown ? new[] { "Menşei tanınmıyor; ülke kodu (TR, DE) veya ülke adı girin." } : Array.Empty<string>();

    public static string Describe(CatalogProduct product) => Resolve(product).Words;

    static string StateCode(string code, string text) => code.Length > 0 ? code : text.Length > 0 ? "?" : "";
    static string Word(string stateCode) => stateCode switch { "" => "belirtilmedi", "?" => "tanınmıyor", _ => stateCode };
    static string SafeText(string text) => AuditStore.Redact(text.Length > TextLimit ? text[..TextLimit] + "…" : text);
}
