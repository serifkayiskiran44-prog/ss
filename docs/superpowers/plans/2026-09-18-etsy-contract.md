# Shared implementation contract

Namespace for workspace: `TrMarketplaceHubDesktop.Etsy`. Metadata records/client remain in `TrMarketplaceHubDesktop`.

## Workspace data types

`EtsyWorkspaceState`: string ShopId, ShopName, Currency; long Revision; DateTime? LastRefreshUtc; List<EtsyListing> Listings; List<EtsyTaxonomyNode> Categories; List<EtsyShippingProfile> ShippingProfiles; List<EtsyProcessingProfile> ProcessingProfiles; List<EtsyShopSection> Sections; List<EtsyCategoryMapping> CategoryMappings; List<EtsyWorkspaceTemplate> Templates; List<EtsyProductProfile> Profiles. Mutable property classes with safe empty defaults for serialization/UI.

`EtsyCategoryMapping`: string LocalCategory; long TaxonomyId.
`EtsyWorkspaceTemplate`: string Id (GUID default), Name; EtsyListingTemplate Listing (existing type); long? ShopSectionId.
`EtsyProductProfile`: string ProductId, TemplateId, Title, Description, Tags, Materials, PriceCurrency; long? ListingId, TaxonomyId, ShippingProfileId, ReadinessStateId, ShopSectionId; decimal? Price; List<EtsyProductProperty> Properties. Empty strings/null mean inherit. Explicit price overrides carry their currency and must match the shop currency. Local SKU and barcode never change. The mapped ListingId belongs only to this state's ShopId.
`EtsyProductProperty`: long PropertyId; long? ScaleId; long[] ValueIds; string[] Values.

`EtsyOperation`: CreateDraft, Price, Stock, PriceAndStock, Content, Publish, Deactivate.
`EtsyPreviewRow`: string ProductId, Sku, Title, Action, Detail, PayloadJson; long? ListingId; bool CanSend.
`EtsyOperationPlan`: string Id, ShopId; EtsyOperation Operation; DateTime CreatedUtc; IReadOnlyList<EtsyPreviewRow> Rows. Additional persisted immutable snapshot fields are backend-owned.
`EtsyOperationReceipt`: string PlanId, ProductId, Sku, Status, Detail; long? ListingId; DateTime CreatedUtc. Unknown outcome is distinct and blocks replay.
`EtsyMatchRow`: string ProductId, Sku, Title, Status, Detail; long? ListingId; bool CanMatch.

## Service interface consumed by UI

`new EtsyWorkspaceStore(string? directory=null)`; Load(string shopId) returns state; Save(state) saves with revision check; Receipts(string shopId) returns IReadOnlyList<EtsyOperationReceipt>.

`new EtsyWorkspaceService(string? directory, HttpClient http)`.

- `IReadOnlyList<EtsyMatchRow> Match(EtsyWorkspaceState state, IReadOnlyList<CatalogProduct> products)` is a read-only matching preview. Exact case-insensitive trimmed SKU only; duplicates/ambiguity cannot be saved.
- `void ApplyMatches(EtsyWorkspaceState state, IReadOnlyList<EtsyMatchRow> matches)` applies valid mappings locally through store after conflict/current-catalog checks. No remote writes or local catalog identity changes.
- `Task<EtsyOperationPlan> PreviewAsync(EtsyCredentials credentials, IReadOnlyList<string> productIds, EtsyOperation operation, CancellationToken cancellationToken=default)` loads persisted workspace/catalog itself; builds and persists immutable preview with per-row validation. Resolve template and profile; errors stay visible. No live writes.
- `Task<IReadOnlyList<EtsyOperationReceipt>> SendAsync(EtsyCredentials credentials, string planId, bool approved, CancellationToken cancellationToken=default)` validates saved plan, account/shop/revisions/catalog/remote snapshots; atomically claims each row and persists actual outcomes. Only rows CanSend. No automatic retry after uncertain writes. No hidden remote publish.

Root UI uses local catalog `CatalogStore.Products()` read access and saves workspace profiles/mappings/templates; no dependence on root MainWindow selection. UI invokes metadata client for dictionaries and stores complete successful read results through Save; fetch failure preserves cache. Product properties are fetched live in product card, definitions exposed by metadata client.

If actual API constraints require contract changes, send root the smallest proposal before changing the agreed public interface.
