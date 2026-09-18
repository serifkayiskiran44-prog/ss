# Multi-Store Product Management Design

## Purpose

Run multiple Trendyol, Etsy, Hepsiburada, Amazon, web-site and future marketplace accounts from one MonoBridge process. A product's information source is independent from the shops where it is sold. Online channels share one online-stock pool; the physical shop has a separate balance and records manual sales in the same order workspace.

## Confirmed user workflow

1. A user imports a product from any XML source or creates it manually.
2. In Product Management, the user selects one or more products and chooses **Connect to shop**.
3. The target is an enabled connection such as `Trendyol / Shop 1`, `Trendyol / Shop 2`, `Etsy / MonoFromTurkiye`, or a web shop.
4. MonoBridge refreshes that exact shop's catalog and matches exact barcodes. Conflicts are shown; missing products go to a new-listing preview. Nothing is created without review and approval.
5. Opening a shop displays that shop's own product workspace: linked, unmatched, pending, failed and unmanaged products; shop-specific category, price, stock and template settings; bulk operations; and history.
6. A connection can be removed locally without changing the remote listing, or its remote sale can be deactivated through a separate preview and then unlinked. Remote deletion is never automatic.
7. Orders from every enabled account are collected into one list while retaining channel and shop identity.

## Core model

### Product source is not a sales destination

`CatalogProduct` remains the single product identity. Source choice is stored independently for these field groups:

- `Content`: title, description, brand, category, attributes and images.
- `Price`: cost/base price before shop-specific rules.
- `OnlineStock`: the quantity feeding the shared online inventory location.

Each group can use `Manual` or a specific `XmlSource`. Importing a product from a Hepsiburada-labelled supplier feed does not connect it to a Hepsiburada sales account and does not prevent publication to Trendyol, Etsy, Amazon or a web shop.

### Inventory locations

The first release creates two location kinds:

- `Online`: one shared balance used by all marketplace and web-shop connections.
- `PhysicalStore`: a separate balance reduced by manual sales.

Additional physical stores can be added later without changing product identity. Stock transfers use an immutable preview and one database transaction. An online order is keyed by `(channel, shopId, orderId)` and may reduce online stock only once. A manual sale is keyed by its local receipt ID and may reduce physical-store stock only once.

### Marketplace connections

`MarketplaceConnectionStore` remains the non-secret account registry. Every account has a stable `ConnectionId`, `Channel`, `ShopId`, display name, enabled state, status and revision. New enabled connections appear automatically in product target selectors and the main marketplace menu.

Secrets move to a connection-scoped DPAPI vault. Existing Etsy and Trendyol credentials are migrated only after their account identity is verified. Legacy encrypted files remain untouched until the new entry has been saved and read back successfully.

### Product-to-shop bindings

One product can have any number of bindings, with at most one binding per `(ProductId, ConnectionId)`. A binding stores remote identity and independent management switches:

- manage listing/content;
- manage price;
- manage stock;
- remote listing/product ID, barcode and SKU;
- category/template references;
- state, last read time, last error and revision.

Disabling stock management leaves the listing linked but stops MonoBridge stock updates for that shop. Price and content behave the same way. Empty or disabled settings never erase remote fields.

## Matching and publication safety

Exact barcode is the automatic identity rule. Duplicate local or remote barcodes are conflicts. SKU can be displayed as evidence and manually selected, but never silently replaces a barcode match. Missing remote matches become `NewListingCandidate`; they are not sent automatically.

Every remote mutation retains the existing safety gates: immutable preview, explicit approval, current connection/shop check, catalog and binding revision check, remote-state check, idempotency key and durable receipt. Tests use fake HTTP clients and temporary databases; they never write to a real marketplace.

## User interface

### Main marketplace menu

The integration area shows account cards rather than one hard-coded item per channel. Cards display channel, shop name, connection status, linked-product count and pending/error counts. Selecting a card opens that account's product workspace. **Add shop** opens connection settings.

### Product Management

The existing central grid gains multi-selection and these commands:

- Connect selected products to shop
- View shop connections
- Change content/price/online-stock source
- Enable or disable price/stock/content management per target
- Remove management link
- Deactivate remotely and remove link

The connection column presents compact badges such as `T1`, `T2`, `Etsy`, and `Web`; expanding a row shows full account names and states.

### Shop workspace

The Entegra-inspired shop workspace has search, detailed filters, bulk actions and a wide product table. The header always shows the exact account. Bulk menus contain only operations supported by that channel adapter. Category matching and remote-value pickers use that account's own metadata.

### Settings

Settings lists marketplace channels and their account tabs. Each account has Active, Connection, Product rules, Order rules and Sync sections. Unsupported options are visibly unavailable instead of being stored as ineffective checkboxes.

### Orders and physical sales

Orders has channel and shop filters but defaults to all enabled accounts. A separate **Manual shop sale** action creates a local order and reduces the selected physical-store location after preview. Transfers between online and physical locations are visible in an inventory ledger.

### Background operation and system tray

Closing the main window with X keeps MonoBridge running in the Windows notification area. XML refresh, enabled account product/order reads and scheduled local jobs continue without an open window. The first close-to-tray action shows one bounded notification explaining that MonoBridge is still running.

The tray menu contains **Open MonoBridge**, **Run sync now**, **Pause/continue sync**, and **Exit MonoBridge**. Only **Exit MonoBridge** stops the process. A setting can change X to fully exit for users who do not want background operation. Background jobs never show modal dialogs, never perform a remote write without an already approved immutable plan, and record redacted failures for the next foreground session. A single-instance guard prevents two background processes from updating the same catalog.

## Adapter boundary

Channel implementations expose capabilities through a shared adapter registry. A newly configured account appears automatically; product read, order read or remote writes are enabled only when its adapter declares and implements that capability. Existing Trendyol and Etsy workspaces remain specialized views hosted inside the common account shell.

## Migration and compatibility

- Existing products become `Manual` source bindings when no XML source is recorded; recorded XML sources are translated into the three field-group bindings without changing values.
- Existing product `Stock` becomes the initial online-location balance.
- Existing Trendyol/Etsy profiles become product-to-shop bindings for their verified account.
- Existing orders already carry marketplace and shop identity and remain readable.
- Legacy credentials and workspace data are preserved until verified migration succeeds.
- Variant, bundle, settlement and fulfillment-core work remains outside this design.

## Success criteria

- Two Trendyol accounts can be enabled and managed independently in one process.
- A product can connect to both accounts and Etsy while using one shared online balance.
- A product imported from one channel-labelled XML can be sold on any other supported target.
- A physical-store manual sale reduces only the physical-store balance.
- All account orders appear in one order list without identity collisions or double stock deductions.
- Closing the window keeps scheduled reads/imports running in one tray process; explicit Exit stops it cleanly.
- Removing a management link never silently deletes a remote listing.
- Existing single-account users migrate without losing products, credentials, mappings or history.
