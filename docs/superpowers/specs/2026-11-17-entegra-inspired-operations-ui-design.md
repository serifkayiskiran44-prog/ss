# MonoBridge Entegra-Inspired Operations UI Design

**Date:** 2026-11-17  
**Status:** Approved for implementation  
**Scope:** Application shell, navigation, dashboard, settings hub, Product Management, XML Management, and shared visual system.

## 1. Goal

Turn the merged MonoBridge desktop application into a dense, Turkish-first commerce operations console. The design may reuse proven workflow patterns visible in Entegra—search-first lists, expandable detail filters, explicit bulk actions, dense grids, account context, and persistent paging—but must remain an original MonoBridge interface and codebase.

The redesign must preserve all existing stores, credentials, safety gates, marketplace isolation, immutable previews, approvals, revision checks, receipts, and background behavior.

## 2. Non-goals

- Do not copy Entegra branding, logos, icons, colors, texts, screenshots, source code, proprietary schemas, or pixel layout.
- Do not add an application login or password.
- Do not add unrestricted inline quick edit, variants, bundles/sets, critical-price logic, XML variant mapping, fulfillment core, or settlement/reconciliation core.
- Do not enable capabilities or live marketplace operations that the current adapters mark unsupported or blocked.
- Do not redesign domain stores or marketplace request contracts merely to change presentation.

## 3. Visual direction

### 3.1 Character

The application should feel like a serious Windows operations console: compact, legible, data-dense, and calm. It should not look like a marketing dashboard or a collection of oversized cards.

### 3.2 Design tokens

- Canvas: `#F3F6F8`
- Surface: `#FFFFFF`
- Surface muted: `#EAF0F3`
- Navigation: `#17313D`
- Navigation selected: `#245766`
- Primary action: `#147A83`
- Primary hover: `#0F646C`
- Text strong: `#17313D`
- Text muted: `#5F7480`
- Border: `#CFD9DF`
- Success: `#2D7D5A`
- Warning: `#B46D12`
- Error: `#B43A3A`
- Information: `#2F6F9F`

Use `Segoe UI Variable` when available and `Segoe UI` as fallback. Use 12 px for grids and controls, 13 px for normal text, 18–22 px for page titles, and tabular-looking alignment for numeric columns. Corner radii are restrained: 3–5 px for controls and surfaces. Shadows are not a primary structural device.

### 3.3 Density

- Default grid row height: 27–29 px.
- Toolbar control height: 30–32 px.
- Page margins: 10–14 px.
- Button labels remain explicit Turkish verbs; icon-only controls are limited to universally understood expand/collapse operations.
- Cards are reserved for the dashboard and marketplace account chooser. Operational pages use toolbars, panels, tabs, and grids. The implicit/default button style is neutral; primary, toolbar, compact, and danger actions use separate keyed styles so programmatic panels do not render every command as primary.

## 4. Application shell

```text
┌──────────────────────────────────────────────────────────────────────────────┐
│ MonoBridge | Global search (Ctrl+K)       Account/health state | Back       │
├──────────────────┬───────────────────────────────────────────────────────────┤
│ Search menu      │ Breadcrumb / page title / concise description            │
│                  ├───────────────────────────────────────────────────────────┤
│ OVERVIEW         │ Page-owned primary command bar                           │
│  General view    │ Optional expandable filters / bulk operations            │
│ CATALOG          ├───────────────────────────────────────────────────────────┤
│  Products        │                                                           │
│  Categories      │ Main page content: dense grid or focused workspace       │
│  Brands          │                                                           │
│  XML             │                                                           │
│  Excel           ├───────────────────────────────────────────────────────────┤
│ OPERATIONS       │ Page count / selection / paging / background status       │
│ STORES           │                                                           │
│ MONITORING       │                                                           │
│ SYSTEM           │                                                           │
├──────────────────┴───────────────────────────────────────────────────────────┤
│ Status | expandable operation history                                       │
└──────────────────────────────────────────────────────────────────────────────┘
```

### 4.1 Persistent shell behavior

- The global search remains visible on every route, including Product Management and Marketplace Accounts.
- The left navigation remains searchable and supports the existing last-route preference and back history.
- The operation status and history remain available at the bottom without permanently consuming excessive height.
- The route host must not show internal `TabItem` headers.
- `Ctrl+1` opens General View; `Ctrl+2` opens Product Management; `Ctrl+K` focuses global search.

## 5. Navigation information architecture

### OVERVIEW

- General View (`dashboard`)

### CATALOG

- Product Management (`products`)
- Categories (`categories`)
- Brands (`brands`)
- Category / Brand / Attribute Center (`taxonomy`)
- Media (`media`)
- XML Management (`xml`)
- Excel Operations (`excel`)

### OPERATIONS

- Orders (`orders`)
- Stock / Price Policies (`policy-center`)
- Synchronization (`sync`)
- Automation (`automation`)

### STORES

- Marketplace Accounts (`marketplaces`)
- BizimHesap (`bizimhesap`)
- NES e-Invoice (`nes`)

### MONITORING

- Data Quality (`data-quality`)
- API Health (`api-health`)
- Messages (`messages`)
- Reports / Audit (`reports`)

### SYSTEM

- Store Connections (`connections`)
- Settings (`settings`)

Compatibility shortcuts for Trendyol and Etsy may continue to route into Marketplace Accounts, but channel names must not multiply into separate permanent top-level pages.

Every channel route request—including Amazon, Hepsiburada, Allegro, Ozon, Joom, Wish, and Fruugo—must resolve through `MarketplaceAccountHomePanel.ShowChannel(channel)` and then open `marketplaces`; unsupported or unconfigured channels must show the account chooser rather than fail silently. `reports` uses a dedicated instance of the existing Diagnostics/Audit surface; Settings links to that route and never attempts to parent the same WPF element twice.

The route audit must report `WORKING`; no registered navigation entry may point at a missing route. General View becomes the default route for a fresh profile.

## 6. General View

The existing read-only `DashboardPanel` becomes the home page. It shows only real local values:

- Active products
- Out-of-stock products
- XML source count and XML errors
- Connection warnings
- Open critical/error data-quality notifications
- Channel/account health table
- 14-day order and stock summary

Cards are keyboard focusable and route to the owning workspace. No card triggers a live marketplace mutation.

## 7. Product Management

### 7.1 Layout

```text
┌ Search ───────────────┬ Search │ Clear │ + New product │ Product card ┐
│ Detailed filters ▾    │ Columns │ Excel ▾ │ Store connection ▾       │
├───────────────────────────────────────────────────────────────────────┤
│ Bulk operations ▾    Selected: 0 | Scope | Operation | Preview       │
├───────────────────────────────────────────────────────────────────────┤
│ + │ Status │ SKU │ Product │ Stores │ Cost │ Price │ Stock │ ...     │
│   │        │     │         │        │      │       │       │         │
├───────────────────────────────────────────────────────────────────────┤
│ Total / filtered / selected       Previous  Page  Next  Limit        │
└───────────────────────────────────────────────────────────────────────┘
```

### 7.2 Command hierarchy

Primary commands:

- Search, clear
- New product
- Product card
- Connect to store

Secondary grouped commands:

- Store: connections, source and stock
- Data: columns, Excel export, Excel update, BizimHesap
- Bulk: operation, value, scope, immutable preview

The screen must avoid a single row of equal-weight buttons. Secondary commands may use compact grouped menus or labeled sections, but existing safe operations remain discoverable.

### 7.3 Grid behavior

- Preserve virtualization, frozen identity columns, column resizing/reordering, persisted column visibility, row details, multi-selection, paging, and the current bounded safe editable cells. Do not add another field or an unrestricted quick-edit subsystem.
- Row details continue to show images, exact store bindings, source ownership, and lock state.
- Numeric fields are right-aligned; identity and state columns remain visible when horizontally scrolling.
- Paging footer clearly shows total, filtered, selected, page, and page limit.
- Empty, loading, error, and no-filter-result states use explicit Turkish messages.

### 7.4 Safety

Bulk operations continue to require preview and current-revision validation. Store binding continues to require exact account selection. The redesign must not add a bypass around preview, account isolation, or local receipt behavior.

## 8. XML Management

### 8.1 Main workspace

```text
┌ + New XML │ Open definition │ Duplicate │ Refresh │ Run │ Preview ┐
├────────────────────────────────────────────────────────────────────┤
│ XML SOURCES                                                        │
│ Active │ Name │ Last run │ Result/error │ Queue │ Interval         │
├────────────────────────────────────────────────────────────────────┤
│ Selected source status / queue explanation                         │
├────────────────────────────────────────────────────────────────────┤
│ CATEGORY MAPPING AND MARKETPLACE PRICE FORMULAS                    │
│ XML category │ Our category │ Active │ channel formulas ...       │
├────────────────────────────────────────────────────────────────────┤
│ Selected / active count │ Save formulas │ Bulk enable/disable      │
└────────────────────────────────────────────────────────────────────┘
```

- Keep the source table above and category/formula table below with a resizable splitter instead of fixed star ratios.
- Make the selected source and last run state visually clear.
- Group template import/export under a compact `Template` command.
- Running and previewing a source remain distinct actions.
- The queue explanation stays visible but concise.

### 8.2 XML definition window

Use a two-pane editor with a persistent bottom action/status bar:

- Left: identity, location, item root, field mappings, update flags, authentication.
- Right: number formats, VAT, stock rules, automatic queue, source filters, connection check.
- Bottom: Save, Download/find fields, Preview, Close, current status.

Sections use titled groups and compact aligned label/control rows. Advanced stock rules and authenticated-address settings remain collapsed by default. Unsupported or invalid states must be explained next to the owning field.

### 8.3 XML preview

The preview remains a separate wide window with a dense virtualized grid. It must update the selected count on `SelectionChanged`, show a validation summary, and provide an explicit `Transfer selected products` action. Import continues to reject a stale source revision or a deleted/disabled source.

## 9. Settings hub

Replace the current long StackPanel with a focused `SettingsHubPanel` constructed as `SettingsHubPanel(string? directory, BackgroundAppController? backgroundController, Action<string> navigate)`.

```text
┌ Settings sections ─────┬────────────────────────────────────────────┐
│ General                │ Section title and short explanation        │
│ Store connections      │                                            │
│ Account rules          │ Section-owned controls                     │
│ Scheduling             │                                            │
│ Locale / tax           │                                            │
│ Data / backup          │                                            │
│ Health / diagnostics   │                                            │
└────────────────────────┴────────────────────────────────────────────┘
```

Global settings and account-scoped settings remain separate:

- General: tray behavior and application preferences.
- Store connections: opens the existing connection manager.
- Account rules: opens the exact account settings tabs.
- Scheduling: sync and automation surfaces; unsupported product scheduling stays disabled with explanation.
- Locale/tax: existing locale store and panel.
- Data/backup: existing backup, restore, migration, and data-folder information.
- Health/diagnostics: API health, production readiness, diagnostics/audit.

Credential values remain in the existing DPAPI-backed stores. The settings hub only routes and groups existing safe editors.
Account rules cannot open without an exact account selection; the hub routes the user to Marketplace Accounts or Store Connections first.

## 10. Marketplace accounts

- Account cards retain exact `ConnectionId` isolation and show channel, display name, shop id, connection state, linked/pending/error counts.
- Add a visible `Add store` command that routes to Store Connections.
- Opening a card shows a compact account header, account switcher, store products, channel-specific workspace, account rules, and history.
- Unsupported capabilities remain disabled and visibly explained.
- The uncommitted new-listing-candidate work is not part of this design branch; it will be merged separately after its own verification.

## 11. Accessibility and window behavior

- Every interactive control has a Turkish automation name or visible label.
- Keyboard focus order follows visual order.
- Selected, hover, disabled, warning, and error states do not rely on color alone.
- The shell must remain usable at 1366×720 and 125% scaling; wide grids scroll horizontally instead of shrinking columns into unreadable widths.
- Dialogs clamp to the current work area and keep primary actions visible.
- Long operations remain cancellable where cancellation already exists; the redesign must not introduce duplicate execution paths.

## 12. Acceptance criteria

1. `ScreenParityAudit` returns `WORKING` for the actual registered routes, including the required `taxonomy` key.
2. General View is the default route for a fresh profile; saved last route still wins.
3. Global search is visible on Product Management and Marketplace Accounts.
4. Product Management presents primary, secondary, and bulk commands as separate visual groups while preserving every existing operation and safety gate.
5. XML Management presents source list, status, category/formula table, definition editor, and preview as the specified hierarchy.
6. Settings is a sectioned hub rather than a single long stack and does not duplicate credential storage.
7. The shared theme applies consistent density, typography, states, buttons, tabs, expanders, and data grids without copying Entegra assets.
8. Existing Release tests remain green; new UI contract tests cover route registration, default route, persistent global search, command grouping, settings sections, and XML/Product structure.
9. A self-contained `win-x64` build opens against an isolated `MARKETPLACEHUB_DATA_DIR` and receives a visual smoke pass at 1366×720 and the normal maximized desktop size.
10. No live marketplace write is performed during automated or visual verification.

