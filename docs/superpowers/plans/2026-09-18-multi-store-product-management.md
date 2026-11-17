# Multi-Store Product Management Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Manage multiple marketplace accounts, product sources, shared online inventory, physical-store stock and unified orders from one MonoBridge process.

**Architecture:** Keep `CatalogProduct` as the master identity and introduce separate source, inventory-location and product-to-connection binding stores. Reuse `MarketplaceConnectionStore` as the account registry, move secrets into a connection-scoped DPAPI vault, and host channel-specific Trendyol/Etsy behavior behind a common account workspace and adapter boundary.

**Tech Stack:** .NET 8, WPF, Microsoft.Data.Sqlite, System.Text.Json, Windows DPAPI, MSTest.

**Spec:** `docs/superpowers/specs/2026-09-18-multi-store-product-management-design.md`

## Global Constraints

- Preserve all existing catalog rows, XML definitions, credentials, mappings, order history and receipts.
- A source channel never implies a sales destination.
- Exact barcode is the only automatic remote-product match; SKU matching requires manual review.
- Online accounts share one online inventory balance; physical-store balances are separate.
- No remote write without immutable preview, explicit approval, stale/revision checks, account guard, idempotency and receipt.
- No live marketplace writes in tests.
- Secrets stay out of SQLite, logs, fixtures, diagnostics and Git.
- Variant, bundle, critical-price, XML-variant, fulfillment-core and settlement work remains excluded.

---

### Task 1: Source bindings independent from sales channels

**Files:**
- Create: `Catalog/ProductSourceBinding.cs`
- Create: `Catalog/ProductSourceBindingStore.cs`
- Modify: `Catalog/CatalogStore.cs`
- Modify: `Catalog/XmlCatalog.Definition.cs`
- Modify: `MainWindow.xaml.cs`
- Test: `tests/MarketplaceHub.Tests/ProductSourceBindingTests.cs`

**Interfaces:**
- Produces: `ProductFieldGroup`, `ProductSourceKind`, `ProductSourceBinding`, `ProductSourceBindingStore.Get(productId)`, `Save(binding, expectedVersion)`, `MigrateFromCatalog()`.
- Consumes: `CatalogProduct.SourceId`, `SourceKind`, `PriceSource`, `StockSource`, `MediaSource` and existing `XmlSource` IDs.

- [ ] Write failing temporary-database tests proving one product can use XML A for content, XML B for price and manual online stock; changing a source does not create a marketplace binding.
- [ ] Run `dotnet test tests\MarketplaceHub.Tests\MarketplaceHub.Tests.csproj -c Release --filter ProductSourceBindingTests` and verify the missing store/types fail.
- [ ] Add these contracts:

```csharp
public enum ProductFieldGroup { Content, Price, OnlineStock }
public enum ProductSourceKind { Manual, Xml }
public sealed record ProductSourceBinding(
    string ProductId, ProductFieldGroup Group, ProductSourceKind Kind,
    string SourceId, bool Enabled, long Version, DateTime UpdatedUtc);
```

- [ ] Store rows under primary key `(ProductId, FieldGroup)`, validate XML IDs against `CatalogStore.Sources()`, use compare-and-swap `Version`, and reject deleted/disabled sources in both manual and scheduled production refresh paths.
- [ ] Migrate existing products deterministically: valid recorded XML source becomes the corresponding binding; otherwise use Manual. Do not rewrite `CatalogProduct` values during migration.
- [ ] Run the focused tests and existing XML import tests; commit `feat: separate product sources from sales channels`.

### Task 2: Inventory locations, ledger and transfers

**Files:**
- Create: `Catalog/InventoryLocationStore.cs`
- Create: `Catalog/InventoryLedger.cs`
- Modify: `Catalog/CatalogStore.cs`
- Modify: `Catalog/CatalogOrderStock.cs`
- Modify: `Catalog/OrderStockRestore.cs`
- Test: `tests/MarketplaceHub.Tests/InventoryLocationTests.cs`

**Interfaces:**
- Produces: `InventoryLocation`, `InventoryBalance`, `InventoryMovement`, `InventoryTransferPreview`, `InventoryLocationStore.PreviewTransfer(...)`, `ApplyTransfer(...)`, `ApplyOrder(...)`, `ApplyManualSale(...)`.
- Consumes: existing catalog product IDs/SKUs and order identity `(Marketplace, ShopId, OrderId)`.

- [ ] Write failing tests for initial online migration, separate physical balance, atomic transfer, insufficient stock, stale preview, duplicate online-order receipt and duplicate manual-sale receipt.
- [ ] Run the focused tests and confirm failure before schema implementation.
- [ ] Add contracts:

```csharp
public enum InventoryLocationKind { Online, PhysicalStore }
public sealed record InventoryLocation(string Id, string Name, InventoryLocationKind Kind, bool Enabled, long Version);
public sealed record InventoryBalance(string ProductId, string LocationId, int Quantity, long Version);
public sealed record InventoryTransferPreview(string Id, string ProductId, string FromLocationId,
    string ToLocationId, int Quantity, long FromVersion, long ToVersion, DateTime CreatedUtc);
```

- [ ] Create `InventoryLocations`, `InventoryBalances`, `InventoryMovements`, and immutable transfer/receipt tables. Seed one `online` location and migrate each product's current `Stock` once, using a durable migration marker.
- [ ] Make order application update the online balance and legacy `CatalogProduct.Stock` in one transaction during the compatibility period. Use identity receipts to prevent double deduction.
- [ ] Implement physical-store manual sales and transfers without marketplace HTTP calls.
- [ ] Run focused plus order-stock/restock tests; commit `feat: add shared online and physical inventory locations`.

### Task 3: Connection-scoped encrypted credentials and multiple accounts

**Files:**
- Create: `MarketplaceCredentialVault.cs`
- Create: `MarketplaceConnectionMigration.cs`
- Modify: `MarketplaceConnectionStore.cs`
- Modify: `TrendyolConnection.cs`
- Modify: `CredentialStore.cs`
- Modify: `MarketplaceConnectionsPanel.cs`
- Test: `tests/MarketplaceHub.Tests/MarketplaceCredentialVaultTests.cs`
- Test: `tests/MarketplaceHub.Tests/MultiAccountConnectionTests.cs`

**Interfaces:**
- Produces: `MarketplaceCredentialVault.Save(connectionId, channel, shopId, payload)`, `Load<T>(...)`, `Remove(...)`, `MarketplaceConnectionMigration.ImportLegacy()`.
- Consumes: stable `MarketplaceConnection.Id`; existing Trendyol/Etsy encrypted files.

- [ ] Write failing tests proving two Trendyol accounts and two Etsy accounts load different credentials, wrong channel/shop payloads are rejected, path traversal cannot affect the vault path, and diagnostics reveal no secret values.
- [ ] Implement per-connection DPAPI blobs named by a SHA-256 hash of `ConnectionId`, with channel/shop identity inside the encrypted payload and strict size limits.
- [ ] Add safe migration: load legacy credential, verify its returned account identity, write/read the new vault entry, then mark migration complete; never delete the legacy file automatically.
- [ ] Change account probing to resolve credentials by `ConnectionId` instead of global singleton stores.
- [ ] Extend connection settings with account tabs, Active state and supported capability sections. Unsupported capabilities remain disabled with an explanation.
- [ ] Run focused credential, redirect-safety and connection-fence tests; commit `feat: support encrypted credentials per marketplace account`.

### Task 4: Product-to-shop binding store and matching preview

**Files:**
- Create: `Catalog/ProductChannelBinding.cs`
- Create: `Catalog/ProductChannelBindingStore.cs`
- Create: `Catalog/ProductChannelMatchService.cs`
- Modify: `ChannelProductsStore.cs`
- Modify: `Catalog/ChannelListingMatrix.cs`
- Test: `tests/MarketplaceHub.Tests/ProductChannelBindingTests.cs`
- Test: `tests/MarketplaceHub.Tests/ProductChannelMatchingTests.cs`

**Interfaces:**
- Produces: `ProductChannelBinding`, `ProductChannelBindingPreview`, `ProductChannelMatchRow`, `PreviewMatch(connectionId, productIds, remoteRows)`, `ApplyMatches(previewId)`.
- Consumes: enabled `MarketplaceConnection`, catalog product snapshots and channel adapter remote rows.

- [ ] Write failing tests for one product bound to two Trendyol accounts and Etsy; exact barcode match; duplicate local/remote barcode conflicts; missing barcode/new listing; manually reviewed SKU choice; wrong-account remote row; stale catalog/connection/binding rejection.
- [ ] Add the binding contract:

```csharp
public sealed record ProductChannelBinding(
    string ProductId, string ConnectionId, string RemoteId, string RemoteSku, string RemoteBarcode,
    bool ManageContent, bool ManagePrice, bool ManageStock,
    string CategoryId, string TemplateId, string State, long Version, DateTime UpdatedUtc);
```

- [ ] Persist a unique `(ProductId, ConnectionId)` row. Keep immutable preview rows with catalog hash, connection revision, remote snapshot hash and proposed outcome `Matched`, `NewListingCandidate`, `Conflict`, `Skipped` or `Error`.
- [ ] Apply only reviewed `Matched` rows locally. `NewListingCandidate` is handed to the existing channel-specific creation preview and never auto-sent.
- [ ] Migrate verified existing Trendyol/Etsy profiles into bindings without altering remote listings.
- [ ] Run focused tests plus existing Trendyol/Etsy matching tests; commit `feat: add account-scoped product bindings`.

### Task 5: Dynamic marketplace account shell and shop workspaces

**Files:**
- Create: `MarketplaceAccountHomePanel.cs`
- Create: `MarketplaceWorkspaceHost.cs`
- Create: `MarketplaceAdapterRegistry.cs`
- Modify: `MainWindow.Navigation.cs`
- Modify: `MainWindow.xaml.cs`
- Modify: `TrendyolWorkspacePanel.cs`
- Modify: `EtsyWorkspacePanel.cs`
- Modify: `MarketplaceConnectionsPanel.cs`
- Test: `tests/MarketplaceHub.Tests/MarketplaceAccountNavigationTests.cs`

**Interfaces:**
- Produces: `IMarketplaceAdapter`, `MarketplaceAdapterRegistry.Get(channel)`, `MarketplaceWorkspaceHost.Create(connectionId, directory)`.
- Consumes: enabled connection rows, capabilities and existing specialized workspace panels.

- [ ] Write failing WPF tests proving enabled accounts appear as separate cards, disabled accounts do not open, two Trendyol cards carry different connection IDs, newly saved connections appear after refresh, and unsupported operations are absent.
- [ ] Define the adapter boundary:

```csharp
public interface IMarketplaceAdapter
{
    string Channel { get; }
    MarketplaceCapabilities Capabilities { get; }
    Task<IReadOnlyList<RemoteProductIdentity>> ReadProductsAsync(string connectionId, CancellationToken token);
    Task<IReadOnlyList<OrderSnapshot>> ReadOrdersAsync(string connectionId, DateTime fromUtc, CancellationToken token);
}
```

- [ ] Build an Entegra-inspired account home with cards showing shop name, status, linked count and pending/error count. Keep existing left navigation compact; selecting a card opens the exact account.
- [ ] Pass `ConnectionId` into Trendyol/Etsy panels and load only that account's vault, cache, mappings, profiles, plans and receipts.
- [ ] Add a common workspace header and account switcher while retaining channel-specific product/category/template controls.
- [ ] Run WPF navigation and existing Trendyol/Etsy panel tests; commit `feat: add dynamic marketplace account workspaces`.

### Task 6: Central Product Management bulk connection UI

**Files:**
- Modify: `MainWindow.ProductList.cs`
- Modify: `MainWindow.ProductCard.cs`
- Create: `ProductConnectionsWindow.cs`
- Create: `ProductSourceWindow.cs`
- Test: `tests/MarketplaceHub.Tests/ProductManagementBindingPanelTests.cs`

**Interfaces:**
- Consumes: source bindings, inventory balances, enabled marketplace connections and product-channel match service.
- Produces: product selection commands and account badges; no direct marketplace HTTP writes.

- [ ] Write failing WPF tests for multi-selection, dynamic target-account list, compact account badges, product connection details, source selectors, and disabled connect/apply actions before preview.
- [ ] Add **Connect to shop** to the product grid. Its dialog shows enabled account rows grouped by channel and preserves the selected product IDs.
- [ ] Open an immutable match preview with counts and row statuses. Require explicit selection of conflicts/manual matches. Route missing products to the channel's existing new-listing preview.
- [ ] Add **Connections** details with three local controls per binding: manage content, price and stock. Empty/unchecked values preserve current remote state.
- [ ] Add separate local unlink and remote-deactivate-then-unlink commands. Remote deactivation goes through channel dispatch preview and receipt; remote deletion is absent.
- [ ] Add source selection for Content, Price and Online stock, plus online/physical balances. Source changes never create or remove sales bindings.
- [ ] Run focused WPF tests and existing Product Management filter/paging tests; commit `feat: connect selected products to marketplace accounts`.

### Task 7: Shop workspace bulk operations and account-specific settings

**Files:**
- Create: `MarketplaceShopProductsPanel.cs`
- Create: `MarketplaceShopSettingsPanel.cs`
- Modify: `TrendyolWorkspacePanel.Control.cs`
- Modify: `EtsyWorkspacePanel.Control.cs`
- Modify: `TrendyolWorkspacePanel.Mappings.cs`
- Modify: `EtsyWorkspacePanel.Settings.cs`
- Test: `tests/MarketplaceHub.Tests/MarketplaceShopWorkspaceTests.cs`

**Interfaces:**
- Consumes: `ConnectionId`, adapter capabilities, bindings, remote cache and channel-specific preview services.
- Produces: account-scoped filters, bulk action requests and settings; all writes remain previews until explicit approval.

- [ ] Write failing tests for account-scoped rows, linked/unlinked/error filters, whole-filtered-set selection, bulk category matching, management toggles, and preventing cross-account changes.
- [ ] Build the wide Entegra-style table with search, detailed filters, bulk actions, remote state, local/remote stock and price, category, error and management state.
- [ ] Generate bulk menu items from adapter capabilities. Trendyol retains category/brand/delivery operations; Etsy retains taxonomy/properties/shipping/readiness operations.
- [ ] Add account settings sections for Active, connection, product rules, order rules and sync. Store every rule under `ConnectionId` with a revision.
- [ ] Verify that one Trendyol account's category, template, price or stock rule cannot affect another account.
- [ ] Run focused and channel workspace tests; commit `feat: add account-scoped shop product management`.

### Task 8: Unified order collection and manual physical-store sales

**Files:**
- Create: `MarketplaceOrderSyncService.cs`
- Create: `ManualSaleService.cs`
- Modify: `OrdersStore.cs`
- Modify: `OrdersPanel.cs`
- Modify: `Catalog/OrderStockDecisionService.cs`
- Test: `tests/MarketplaceHub.Tests/MultiStoreOrderSyncTests.cs`
- Test: `tests/MarketplaceHub.Tests/ManualStoreSaleTests.cs`

**Interfaces:**
- Consumes: enabled connections with `OrdersRead`, adapter order readers, product bindings/SKU resolution and inventory locations.
- Produces: `RefreshAllAsync`, per-connection result rows, manual-sale preview/receipt and location-aware stock movements.

- [ ] Write failing tests proving orders with the same order number from two shops do not collide, refresh failure in one account preserves other results, duplicate refresh does not rededuct stock, unmatched SKU is flagged, and manual sale affects only the physical location.
- [ ] Refresh enabled order-capable connections independently and upsert by `(Marketplace, ShopId, OrderId)`. Persist per-account cursor/error/backoff without blocking other accounts.
- [ ] Resolve order lines through `(ConnectionId, RemoteSku/Barcode)` bindings to the master product. Unmatched lines require review and cannot alter inventory.
- [ ] Add account/status/source filters to the single Orders screen and show connection display name alongside marketplace.
- [ ] Add **Manual shop sale** with product search, quantity, physical location, immutable preview and receipt. Add stock-transfer entry to the inventory view.
- [ ] Run focused order/inventory tests; commit `feat: unify marketplace orders and physical sales`.

### Task 9: Background scheduler and Windows system tray

**Files:**
- Create: `BackgroundAppController.cs`
- Create: `MonoBridgeTrayIcon.cs`
- Create: `SingleInstanceGuard.cs`
- Modify: `App.xaml`
- Modify: `App.xaml.cs`
- Modify: `TrMarketplaceHubDesktop.csproj` only if the selected tray implementation requires Windows Forms support.
- Modify: `MainWindow.xaml.cs`
- Modify: `Catalog/AutomationStore.cs`
- Test: `tests/MarketplaceHub.Tests/BackgroundAppControllerTests.cs`
- Test: `tests/MarketplaceHub.Tests/SingleInstanceGuardTests.cs`

**Interfaces:**
- Produces: `BackgroundAppController.Start()`, `Pause()`, `Resume()`, `RunNowAsync()`, `ShutdownAsync()` and `SingleInstanceGuard.TryAcquire(dataDirectory)`.
- Consumes: existing XML/automation scheduler and enabled connection read jobs. Remote writes remain behind previously approved plan receipts.

- [ ] Write failing tests for close-to-tray state, explicit exit, pause/resume, single-instance rejection, graceful cancellation, job failure isolation and prevention of an unapproved background marketplace write.
- [ ] Add one application-lifetime controller owning cancellation tokens and scheduler leases. Window creation/closing must not create duplicate timers.
- [ ] Add a Windows tray icon and menu actions: **MonoBridge'i aç**, **Şimdi senkronize et**, **Senkronu duraklat/devam ettir**, and **Programdan çık**.
- [ ] Intercept the main-window X action when `CloseToTray` is enabled, hide the window, keep `ShutdownMode.OnExplicitShutdown`, and show the “MonoBridge arka planda çalışıyor” notification only on the first hide per process.
- [ ] Add a user setting to choose close-to-tray or full exit. Explicit tray exit cancels jobs, releases leases, disposes HTTP/timers and then shuts down WPF.
- [ ] Add a named single-instance guard scoped to the normalized data directory. A second launch activates the existing window and exits without opening the catalog twice.
- [ ] Route background errors to redacted operation history and a bounded notification; never open modal dialogs from background threads.
- [ ] Run focused automation/restart tests; commit `feat: keep MonoBridge running in the system tray`.

### Task 10: Migration, end-to-end safety and release

**Files:**
- Create: `MultiStoreMigrationService.cs`
- Create: `tests/MarketplaceHub.Tests/MultiStoreMigrationTests.cs`
- Modify: `TODO.md`
- Modify: `IMPLEMENTATION_STATUS.md`
- Modify: `docs/superpowers/specs/2026-09-18-multi-store-product-management-design.md` only if verified implementation differs.

**Interfaces:**
- Consumes: all stores and background-lifetime services from Tasks 1–9.
- Produces: restart-safe migration version, dry-run report, applied receipt and rollback-before-external-write path.

- [ ] Write migration tests from a copy of the current single-account schema: products, XML sources, mappings, credentials, Trendyol/Etsy profiles, orders, scheduler settings and receipts remain readable and keep identity.
- [ ] Add a dry-run report with counts for source bindings, balances, connections and product bindings. Require local confirmation before applying; migration itself performs no marketplace HTTP calls.
- [ ] Add restart tests after every migration checkpoint and verify re-running is idempotent.
- [ ] Run targeted multi-store tests, then the required full command:

```powershell
dotnet test tests\MarketplaceHub.Tests\MarketplaceHub.Tests.csproj -c Release
```

- [ ] Run the required publish command:

```powershell
dotnet publish TrMarketplaceHubDesktop.csproj -c Release -r win-x64 --self-contained true -o Windows-Excel-Final
```

- [ ] Launch `Windows-Excel-Final\TrMarketplaceHubDesktop.exe` and visually verify: two account cards, product multi-select and connect preview, shop-scoped grids, source selectors, online/physical balances, unified order filters, manual-sale preview, close-to-tray, reopen, pause/resume and explicit exit. Do not perform an unapproved live marketplace write.
- [ ] Update status documentation with exact test totals and live-read/live-write boundaries; commit `feat: complete multi-store product orchestration` and push the current branch.

## Recommended execution order

Tasks 1–4 establish safe data ownership. Tasks 5–7 expose it in the desktop UI. Task 8 connects order and physical-store flows. Task 9 keeps scheduled work alive safely in one tray process. Task 10 migrates and releases. Do not begin account UI work before the connection-scoped vault and product binding identities pass their tests.
