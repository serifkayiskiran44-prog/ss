using System.Globalization;

namespace TrMarketplaceHubDesktop;

public sealed record TransformRule(string Id, int Version, string Operation, string? Argument = null, decimal? Number = null);
public sealed record TransformResult(string Value, string Status, string Detail);

public static class TransformRuleEngine
{
    public static TransformResult Apply(string? source, TransformRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var value = source ?? "";
        try
        {
            return rule.Operation.ToLowerInvariant() switch
            {
                "trim" => new(value.Trim(), "READY", ""),
                "prefix" => new((rule.Argument ?? "") + value, "READY", ""),
                "suffix" => new(value + (rule.Argument ?? ""), "READY", ""),
                "replace" => new(value.Replace(rule.Argument ?? "", rule.Number?.ToString(CultureInfo.InvariantCulture) ?? "", StringComparison.Ordinal), "READY", ""),
                "multiply" => Numeric(value, x => x * Required(rule)),
                "add" => Numeric(value, x => x + Required(rule)),
                "percent" => Numeric(value, x => x * (1 + Required(rule) / 100)),
                "round" => Numeric(value, x => decimal.Round(x, (int)Required(rule), MidpointRounding.AwayFromZero)),
                "min" => Numeric(value, x => Math.Max(x, Required(rule))),
                "max" => Numeric(value, x => Math.Min(x, Required(rule))),
                "default" => new(string.IsNullOrWhiteSpace(value) ? rule.Argument ?? "" : value, "READY", ""),
                _ => new(value, "ERROR", "Bilinmeyen veya desteklenmeyen kural.")
            };
        }
        catch (Exception error) when (error is FormatException or OverflowException or DivideByZeroException or ArgumentException)
        { return new(source ?? "", "ERROR", AuditStore.Sanitize(error.Message)); }
    }

    static TransformResult Numeric(string value, Func<decimal, decimal> operation) => new(operation(decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture)).ToString("0.################", CultureInfo.InvariantCulture), "READY", "");
    static decimal Required(TransformRule rule) => rule.Number ?? throw new ArgumentException("Sayısal kural parametresi eksik.");
}
