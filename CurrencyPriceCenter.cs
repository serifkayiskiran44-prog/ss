using System.Globalization;

namespace TrMarketplaceHubDesktop;

public sealed record CurrencyRate(string Base, string Quote, decimal Value, string Source, DateTimeOffset CapturedUtc, bool ManualOverride = false);
public sealed record CurrencyConversion(decimal SourceAmount, decimal ConvertedAmount, string Base, string Quote, bool Stale, string Status);
public sealed record CurrencyOverrideAudit(string Base, string Quote, decimal OldValue, decimal NewValue, string Actor, DateTimeOffset AtUtc);

public sealed class CurrencyPriceCenter
{
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(new[] { "TRY", "USD", "EUR" }, StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Base, string Quote), CurrencyRate> rates = new();
    private readonly List<CurrencyOverrideAudit> audits = new();
    public IReadOnlyList<CurrencyOverrideAudit> Audits => audits.ToArray();
    public void SetRate(CurrencyRate rate) { ValidateCurrency(rate.Base); ValidateCurrency(rate.Quote); if (rate.Value <= 0) throw new ArgumentException("Kur pozitif olmalıdır."); rates[(rate.Base.ToUpperInvariant(), rate.Quote.ToUpperInvariant())] = rate with { Base = rate.Base.ToUpperInvariant(), Quote = rate.Quote.ToUpperInvariant() }; }
    public CurrencyOverrideAudit Override(string @base, string quote, decimal value, string actor) { ValidateCurrency(@base); ValidateCurrency(quote); var key = (@base.ToUpperInvariant(), quote.ToUpperInvariant()); var old = rates.TryGetValue(key, out var r) ? r.Value : 0; var item = new CurrencyOverrideAudit(key.Item1, key.Item2, old, value, actor, DateTimeOffset.UtcNow); SetRate(new(key.Item1, key.Item2, value, "manual", item.AtUtc, true)); audits.Add(item); return item; }
    public CurrencyConversion Convert(decimal amount, string @base, string quote, DateTimeOffset now, TimeSpan maxAge)
    { if (amount < 0) throw new ArgumentException("Tutar negatif olamaz."); ValidateCurrency(@base); ValidateCurrency(quote); if (string.Equals(@base, quote, StringComparison.OrdinalIgnoreCase)) return new(amount, amount, @base.ToUpperInvariant(), quote.ToUpperInvariant(), false, "READY"); if (!rates.TryGetValue((@base.ToUpperInvariant(), quote.ToUpperInvariant()), out var rate)) return new(amount, 0, @base.ToUpperInvariant(), quote.ToUpperInvariant(), true, "NOT_CONFIGURED"); var stale = now - rate.CapturedUtc > maxAge; return new(amount, decimal.Round(amount * rate.Value, 2, MidpointRounding.AwayFromZero), rate.Base, rate.Quote, stale, stale ? "STALE_LIVE_WRITE_BLOCKED" : "READY"); }
    public static decimal Parse(string text, CultureInfo culture) => decimal.Parse(text, NumberStyles.Number, culture);
    public static decimal Round(decimal amount, int decimals = 2) => decimal.Round(amount, decimals, MidpointRounding.AwayFromZero);
    private static void ValidateCurrency(string value) { if (!Supported.Contains(value)) throw new ArgumentException("Desteklenmeyen para birimi."); }
}
