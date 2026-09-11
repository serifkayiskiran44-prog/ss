namespace TrMarketplaceHubDesktop;

public sealed record EtsyLiveSafetyDecision(string Status, string Detail);

public static class EtsyLiveSafetyGate
{
    public static EtsyLiveSafetyDecision ValidateCurrency(string sourceCurrency, string remoteCurrency)
    {
        if (string.IsNullOrWhiteSpace(sourceCurrency) || string.IsNullOrWhiteSpace(remoteCurrency)) return new("BLOCKED", "CURRENCY_UNKNOWN");
        return string.Equals(sourceCurrency.Trim(), remoteCurrency.Trim(), StringComparison.OrdinalIgnoreCase) ? new("READY", "CURRENCY_MATCH") : new("BLOCKED", "CURRENCY_MISMATCH");
    }
}
