namespace TrMarketplaceHubDesktop.Catalog;

public class XmlCategoryRule
{
    public string XmlCategory { get; set; } = "";
    public string TargetCategory { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public Dictionary<string, XmlChannelFormula> Prices { get; set; } = new();
}

public class XmlChannelFormula
{
    public string SaleFormula { get; set; } = "";
    public string ListFormula { get; set; } = "";
}

public record XmlChannelPrice(decimal SalePrice, decimal? ListPrice, string Currency);

/// Local calculated prices. Publishing requires the separate channel approval flow.
public static class XmlCategoryRules
{
    public static readonly string[] Channels = ["Trendyol", "Hepsiburada", "Amazon", "Etsy", "eBay", "Ozon", "Joom", "Wish", "BizimHesap"];

    public static void Validate(XmlSource source)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in source.CategoryRules)
        {
            if (!seen.Add(rule.XmlCategory.Trim())) throw new InvalidOperationException("Aynı XML kategorisi için birden fazla kural var.");
            foreach (var formulas in rule.Prices.Values)
            foreach (var formula in new[] { formulas.SaleFormula, formulas.ListFormula }.Where(f => !string.IsNullOrWhiteSpace(f)))
                try { _ = PriceFormula.Compile(formula); }
                catch (FormatException error) { throw new InvalidOperationException($"{rule.XmlCategory}: {error.Message}"); }
        }
    }

    public static Action<CatalogProduct> Create(XmlSource source)
    {
        Validate(source);
        // Compile once per source run, not once per product.
        var rules = source.CategoryRules.ToDictionary(r => r.XmlCategory.Trim(), r => (
            Rule: r, Prices: r.Prices.Where(p => !string.IsNullOrWhiteSpace(p.Value.SaleFormula) || !string.IsNullOrWhiteSpace(p.Value.ListFormula))
                .ToDictionary(p => p.Key, p => (Sale: Compile(p.Value.SaleFormula), List: Compile(p.Value.ListFormula)))), StringComparer.OrdinalIgnoreCase);
        return product =>
        {
            product.XmlCategory = product.Category;
            if (!rules.TryGetValue(product.XmlCategory.Trim(), out var entry)) return;
            product.Active = entry.Rule.Enabled;
            if (!string.IsNullOrWhiteSpace(entry.Rule.TargetCategory)) product.Category = entry.Rule.TargetCategory.Trim();
            foreach (var (channel, formula) in entry.Prices)
            {
                decimal ConvertPrice(CompiledPriceFormula compiled)
                {
                    var value = compiled.Evaluate(product.Cost);
                    if (value < 0) throw new InvalidOperationException($"{channel} / {product.XmlCategory}: formül negatif fiyat üretti.");
                    // Formulas return amounts in the XML purchase currency; reuse the source's
                    // configured currency conversion without applying the general markup twice.
                    var rate = source.PriceMode == "Formula" ? (source.Currency == "TRY" ? 1 : source.TryPerTargetUnit) : 1 / source.ExchangeRate;
                    return Math.Round(value / rate, 2, MidpointRounding.AwayFromZero);
                }
                try
                {
                    var sale = formula.Sale is null ? product.Price : ConvertPrice(formula.Sale);
                    decimal? list = formula.List is null ? null : ConvertPrice(formula.List);
                    if (list == 0) list = null;
                    if (list < sale) throw new InvalidOperationException($"{channel} / {product.XmlCategory}: üstü çizili fiyat satış fiyatından düşük olamaz.");
                    product.ChannelPrices[channel] = new(sale, list, source.Currency);
                }
                catch (Exception error) when (error is DivideByZeroException or OverflowException)
                { throw new InvalidOperationException($"{channel} / {product.XmlCategory}: fiyat formülü hesaplanamadı."); }
            }
        };
    }

    static CompiledPriceFormula? Compile(string formula) => string.IsNullOrWhiteSpace(formula) ? null : PriceFormula.Compile(formula);
}
