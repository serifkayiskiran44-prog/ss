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

public sealed record HepsiburadaMerchantProduct(
    string MerchantId,
    string MerchantSku,
    string Barcode,
    string HepsiburadaSku);

public sealed record HepsiburadaListing(
    string MerchantId,
    string MerchantSku,
    string HepsiburadaSku,
    string Barcode,
    int Stock,
    decimal Price,
    int DispatchTime);

public sealed record HepsiburadaConnectionIdentity(
    string MerchantId,
    HepsiburadaEnvironment Environment,
    DateTime CheckedAtUtc);
