namespace TrMarketplaceHubDesktop.Catalog;

public sealed record ProductChannelBinding(
    string ProductId, string ConnectionId, string RemoteId, string RemoteSku, string RemoteBarcode,
    bool ManageContent, bool ManagePrice, bool ManageStock,
    string CategoryId, string TemplateId, string State, long Version, DateTime UpdatedUtc);

public enum ProductChannelMatchOutcome { Matched, NewListingCandidate, Conflict, Skipped, Error }

public sealed record ProductChannelRemoteRow(
    string ConnectionId, string ShopId, string RemoteId, string RemoteSku, string RemoteBarcode,
    string CategoryId = "", string TemplateId = "", string State = "Active");

public sealed record ProductChannelRemoteSnapshot(
    string ConnectionId, string ShopId, IReadOnlyList<ProductChannelRemoteRow> Rows);

public interface IProductChannelRemoteSnapshotProvider
{
    ProductChannelRemoteSnapshot Read(MarketplaceConnection connection);
}

public interface IProductChannelCreationPreviewHandoff
{
    string Preview(MarketplaceConnection connection, IReadOnlyList<string> productIds);
}

public sealed record ProductChannelMatchReview(string ProductId, string RemoteId);

public sealed record ProductChannelMatchRow(
    string ProductId, string ConnectionId, string LocalSku, string LocalBarcode,
    string RemoteId, string RemoteSku, string RemoteBarcode,
    ProductChannelMatchOutcome Outcome, bool Reviewed, string Detail);

public sealed record ProductChannelBindingPreview(
    string Id, string ParentPreviewId, string ConnectionId, string Channel, string ShopId,
    DateTime CreatedUtc, long ConnectionRevision, string CatalogHash, string BindingVersionHash,
    string RemoteSnapshotHash, IReadOnlyList<ProductChannelRemoteRow> RemoteRows,
    IReadOnlyList<ProductChannelMatchRow> Rows);

public sealed record ProductChannelBindingReceipt(
    string PreviewId, string ConnectionId, DateTime AppliedUtc,
    IReadOnlyList<ProductChannelBinding> AppliedBindings,
    IReadOnlyList<string> NewListingCandidates, string CreationPreviewId);
