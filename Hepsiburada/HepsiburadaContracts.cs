namespace TrMarketplaceHubDesktop.Hepsiburada;

public enum HepsiburadaEnvironment
{
    Production,
    Sit
}

public sealed record HepsiburadaCredentials(
    string MerchantId,
    string ServiceKey,
    HepsiburadaEnvironment Environment,
    string UserAgent);
