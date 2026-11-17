using System.Security.Cryptography;
using System.Text;

namespace TrMarketplaceHubDesktop;

public enum BizimHesapReadbackStatus { Missing, Matched, Ambiguous, Conflict }

public sealed record BizimHesapReadbackResult(BizimHesapReadbackStatus Status, BizimHesapProduct? Product);

public static class BizimHesapCreateSafety
{
    public static bool IsPriceCurrent(BizimHesapCreateProduct local, BizimHesapProduct remote)
    {
        var expectedGross = Math.Round(local.NetPrice * (1m + local.VatRate / 100m), 4, MidpointRounding.AwayFromZero);
        return Math.Abs(expectedGross - remote.Price) < 0.0001m;
    }

    public static BizimHesapReadbackResult MatchRemoteId(string remoteId, IEnumerable<BizimHesapProduct> remoteProducts)
    {
        var key = Normalize(remoteId);
        if (key.Length == 0) return new(BizimHesapReadbackStatus.Missing, null);
        var matches = remoteProducts.Where(row => Normalize(row.Id) == key).ToArray();
        return matches.Length switch
        {
            0 => new(BizimHesapReadbackStatus.Missing, null),
            1 => new(BizimHesapReadbackStatus.Matched, matches[0]),
            _ => new(BizimHesapReadbackStatus.Ambiguous, null)
        };
    }

    public static string AccountKey(BizimHesapSettings settings)
    {
        BizimHesapConnection.Validate(settings);
        return Hash(Normalize(settings.FirmId));
    }

    public static string PayloadHash(BizimHesapCreateProduct product)
    {
        var canonical = string.Join('\u001f', new[]
        {
            Normalize(product.Sku),
            Normalize(product.Barcode),
            product.Title.Trim(),
            product.NetPrice.ToString("G29", System.Globalization.CultureInfo.InvariantCulture),
            product.VatRate.ToString("G29", System.Globalization.CultureInfo.InvariantCulture),
            product.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Normalize(product.Currency)
        });
        return Hash(canonical);
    }

    public static BizimHesapReadbackResult Match(BizimHesapCreateProduct product, IEnumerable<BizimHesapProduct> remoteProducts)
    {
        var barcodeKey = Normalize(product.Barcode);
        var skuKey = Normalize(product.Sku);
        var barcode = barcodeKey.Length == 0
            ? Array.Empty<BizimHesapProduct>()
            : remoteProducts.Where(row => Normalize(row.Barcode) == barcodeKey).ToArray();
        var sku = skuKey.Length == 0
            ? Array.Empty<BizimHesapProduct>()
            : remoteProducts.Where(row => Normalize(row.Code) == skuKey).ToArray();

        if (barcode.Length > 1 || sku.Length > 1)
            return new(BizimHesapReadbackStatus.Ambiguous, null);
        if (barcode.Length == 1 && sku.Length == 1 && !ReferenceEquals(barcode[0], sku[0]) && barcode[0].Id != sku[0].Id)
            return new(BizimHesapReadbackStatus.Conflict, null);
        var match = barcode.SingleOrDefault() ?? sku.SingleOrDefault();
        return match is null
            ? new(BizimHesapReadbackStatus.Missing, null)
            : new(BizimHesapReadbackStatus.Matched, match);
    }

    static string Normalize(string? value) => (value ?? "").Trim().ToUpperInvariant();
    static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
