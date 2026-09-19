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

public sealed record HepsiburadaCategory(long Id, string Name, string Path, bool Leaf, bool Available, bool Active)
{
    public bool Publishable => Leaf && Available && Active;
}

public enum HepsiburadaAttributeKind { Unknown, Text, Number, Boolean, List }

public sealed record HepsiburadaAttributeValue(string Id, string Name);

public sealed record HepsiburadaAttribute(
    string Id,
    string Name,
    bool Mandatory,
    bool MultiValue,
    HepsiburadaAttributeKind Kind,
    IReadOnlyList<HepsiburadaAttributeValue> Values);

public sealed record HepsiburadaBuybox(
    string HepsiburadaSku,
    string MerchantSku,
    int? Rank,
    decimal? WinningPrice,
    decimal? OwnPrice,
    DateTime ReadUtc);

public sealed record HepsiburadaCommission(string HepsiburadaSku, string MerchantSku, decimal Rate, string Currency);

public interface IHepsiburadaDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}
