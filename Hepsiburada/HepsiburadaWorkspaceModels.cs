using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop.Hepsiburada;

public sealed record HepsiburadaProductMatch(
    string ProductId,
    string RemoteId,
    string RemoteSku,
    string RemoteBarcode,
    ProductChannelMatchOutcome Outcome,
    string Method,
    string Detail);

public sealed record HepsiburadaManualMatch(
    string ConnectionId,
    string ProductId,
    string RemoteId,
    long SnapshotRevision,
    DateTime UpdatedUtc);

public sealed record HepsiburadaWorkspaceState(
    string ConnectionId,
    string ShopId,
    long Revision,
    DateTime RefreshedUtc,
    IReadOnlyList<HepsiburadaMerchantProduct> Products,
    IReadOnlyList<HepsiburadaManualMatch> ManualMatches);
