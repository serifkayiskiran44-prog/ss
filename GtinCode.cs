namespace TrMarketplaceHubDesktop;

public enum BarcodeKind { Empty, Gtin8, Gtin12, Gtin13, Gtin14, Custom, InvalidGtin }

/// <summary>A barcode as inspected: its kind, the text as given (trimmed at the ends only), whether it is a GTIN with a correct check digit, and the words.</summary>
public sealed record BarcodeVerdict(BarcodeKind Kind, string Raw, bool IsGtin, bool ChecksumValid, string Words)
{
    /// <summary>The 14-digit form for comparison only (left-padded with zeros); empty unless a valid GTIN. Never stored — the value stays exactly as given.</summary>
    public string Canonical14 => IsGtin && ChecksumValid ? Raw.PadLeft(14, '0') : "";
}

/// <summary>
/// GTIN format and check-digit validation (#906), part of the product identity contract (#902). A GTIN is 8, 12,
/// 13 or 14 digits whose last digit is the mod-10 check digit (weights 3 and 1 from the right); anything else in
/// a barcode field is a custom code — allowed as a barcode, refused in the GTIN field. Nothing is normalised
/// silently: the ends are trimmed, but leading zeros stay, inner spaces or dashes make the value a custom code
/// rather than being stripped, and an invalid check digit is reported with the expected digit, never corrected.
/// </summary>
public static class GtinCode
{
    public static BarcodeVerdict Inspect(string? raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0) return new(BarcodeKind.Empty, "", false, false, "boş");
        if (!text.All(char.IsAsciiDigit)) return new(BarcodeKind.Custom, text, false, false, $"özel barkod ({text.Length} karakter; GTIN değil)");
        var kind = text.Length switch { 8 => BarcodeKind.Gtin8, 12 => BarcodeKind.Gtin12, 13 => BarcodeKind.Gtin13, 14 => BarcodeKind.Gtin14, _ => BarcodeKind.Custom };
        if (kind == BarcodeKind.Custom) return new(BarcodeKind.Custom, text, false, false, $"özel barkod ({text.Length} hane; GTIN uzunluğu değil)");
        var expected = CheckDigit(text[..^1]); var actual = text[^1] - '0';
        if (expected != actual) return new(BarcodeKind.InvalidGtin, text, true, false, $"{Label(kind)} sağlama hanesi tutmuyor (beklenen {expected}, verilen {actual}); değer değiştirilmedi");
        return new(kind, text, true, true, $"{Label(kind)} · sağlama doğru");
    }

    /// <summary>The mod-10 check digit for a digit string without its check digit (weights 3, 1, 3, 1 … from the right).</summary>
    public static int CheckDigit(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.Length == 0 || !body.All(char.IsAsciiDigit)) throw new ArgumentException("Sağlama hanesi yalnız rakamlardan hesaplanır.", nameof(body));
        var total = 0;
        for (var i = 0; i < body.Length; i++) total += (body[body.Length - 1 - i] - '0') * (i % 2 == 0 ? 3 : 1);
        return (10 - total % 10) % 10;
    }

    public static bool IsValidGtin(string? raw) => Inspect(raw) is { IsGtin: true, ChecksumValid: true };

    public static string Label(BarcodeKind kind) => kind switch
    {
        BarcodeKind.Gtin8 => "GTIN-8", BarcodeKind.Gtin12 => "GTIN-12 (UPC-A)", BarcodeKind.Gtin13 => "GTIN-13 (EAN-13)", BarcodeKind.Gtin14 => "GTIN-14",
        BarcodeKind.Custom => "özel barkod", BarcodeKind.InvalidGtin => "geçersiz GTIN", _ => "boş",
    };
}
