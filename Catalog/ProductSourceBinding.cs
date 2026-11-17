namespace TrMarketplaceHubDesktop.Catalog;

/// <summary>The three groups of product data whose origin is chosen independently.</summary>
public enum ProductFieldGroup { Content, Price, OnlineStock }

public enum ProductSourceKind { Manual, Xml }

/// <summary>
/// Where one field group of a product comes from. It says nothing about where the product is sold:
/// a product fed by any XML can be sold on every connected shop.
/// <c>Version</c> is 0 for a group that was never stored (implicitly Manual) and grows by one per save.
/// </summary>
public sealed record ProductSourceBinding(
    string ProductId, ProductFieldGroup Group, ProductSourceKind Kind,
    string SourceId, bool Enabled, long Version, DateTime UpdatedUtc);
