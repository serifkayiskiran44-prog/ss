namespace TrMarketplaceHubDesktop.Catalog;

public enum ProductFieldGroup { Content, Price, OnlineStock }
public enum ProductSourceKind { Manual, Xml }

public sealed record ProductSourceBinding(
    string ProductId,
    ProductFieldGroup Group,
    ProductSourceKind Kind,
    string SourceId,
    bool Enabled,
    long Version,
    DateTime UpdatedUtc);
