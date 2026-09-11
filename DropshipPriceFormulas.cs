using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TrMarketplaceHubDesktop;

public sealed record DropshipPriceFormula(decimal Multiplier = 1m, decimal Percent = 0m, decimal FixedCost = 0m, decimal VatPercent = 0m, bool InputIncludesVat = false, bool OutputIncludesVat = true, int Decimals = 2, bool PsychologicalEnding = false, decimal MinimumMarginPercent = 0m, long Version = 1)
{
    public static DropshipPriceFormula Example => new(Multiplier: 1.2m, FixedCost: 0m, VatPercent: 20m);
}
public sealed record PricePreview(string ShopId, string Channel, string ProductId, decimal SourceCost, decimal ResultPrice, string Currency, decimal MarginPercent, string Status, long FormulaVersion, string Key);

public static class DropshipPriceFormulas
{
    public static PricePreview Preview(string shopId, string channel, string productId, decimal sourceCost, decimal exchangeRate, string currency, DropshipPriceFormula formula, decimal? currentCost = null)
    {
        if (sourceCost < 0 || exchangeRate <= 0 || formula.Decimals < 0 || formula.VatPercent < 0 || formula.MinimumMarginPercent < 0) throw new ArgumentException("Fiyat formülü girdileri geçersiz.");
        try { var cost = checked(sourceCost * exchangeRate); var basePrice = checked(cost * formula.Multiplier + cost * formula.Percent / 100m + formula.FixedCost); var price = ConvertVat(basePrice, formula); price = decimal.Round(price, formula.Decimals, MidpointRounding.AwayFromZero); if (formula.PsychologicalEnding && formula.Decimals >= 2) price = Math.Max(0, decimal.Floor(price) + .99m); var margin = currentCost is null || price == 0 ? 0 : decimal.Round((price - currentCost.Value) / price * 100m, 4); var status = margin < formula.MinimumMarginPercent ? "BLOCKED_LOW_MARGIN" : "READY"; var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{shopId}|{channel}|{productId}|{cost}|{price}|{currency}|{formula.Version}"))).ToLowerInvariant(); return new(shopId, channel, productId, sourceCost, price, currency.ToUpperInvariant(), margin, status, formula.Version, key); } catch (OverflowException) { throw new InvalidOperationException("FORMULA_OVERFLOW: fiyat önizlemesi hesaplanamadı."); }
    }
    public static IReadOnlyList<PricePreview> Bulk(IEnumerable<(string ProductId, decimal Cost)> items, string shopId, string channel, decimal rate, DropshipPriceFormula formula, string currency) => items.Select(x => Preview(shopId, channel, x.ProductId, x.Cost, rate, currency, formula)).ToArray();
    public static string Format(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);
    private static decimal ConvertVat(decimal amount, DropshipPriceFormula f) { if (f.VatPercent == 0 || f.InputIncludesVat == f.OutputIncludesVat) return amount; var factor = 1m + f.VatPercent / 100m; return f.InputIncludesVat ? amount / factor : amount * factor; }
}
