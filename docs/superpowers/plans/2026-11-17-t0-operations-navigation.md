# T0 Operations Navigation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every already-implemented operations panel discoverable from the MonoBridge shell without adding new business engines or triggering network/write side effects on navigation.

**Architecture:** Extend the existing `MainWindow.BuildNavigation` route registry and `ScreenParityAudit` contract. Add lazy route factories so heavy local panels are created only on first selection, reuse each panel factory exactly once, preserve the current grouped left navigation, and keep channel-specific navigation in `MarketplaceAccountHomePanel`. T0 exposes existing local panels only; inventory ledger work remains T1.

**Tech Stack:** .NET 8, WPF, MSTest, SQLite-backed local stores.

**Spec:** `docs/CLAUDE-ENTEGRA-COMPREHENSIVE-TASK.md` (T0)

## Global Constraints

- No new login, application password, or role/user system.
- Preserve existing data and encrypted credentials; tests use temporary directories only.
- No live marketplace HTTP/write in tests or route construction.
- Do not add code, screens, models, or tests for the seven `DEFERRED_BY_USER` areas.
- Unverified channels remain `LIVE_API_BLOCKED`.
- Reuse existing panel factories; do not duplicate services or repositories.
- Every change runs relevant tests, the full Release suite, and self-contained `Windows-Current` publish.
- Update `TODO.md` and `IMPLEMENTATION_STATUS.md`; commit and push the branch.

## Review Focus

- Application startup and route registration must not start connector reads, scheduler jobs, publishing, restore, migration apply, or heavy panel refreshes. Temporary-store schema initialization is allowed only when the user first opens the corresponding route.
- Route keys must be unique and stable; saved `last-route` values must reopen the correct page.
- Navigation labels and groups must remain usable at 1366×720 and via keyboard focus.
- Panel callbacks must use the shared route resolver and preserve exact-account marketplace context.
- Destructive/privileged panels must open in preview-first or local read-only state; no action occurs merely by opening them.

### Task 1: Expand the route contract with the existing hidden operations panels

**Files:**

- Modify: `ScreenParityAudit.cs`
- Modify: `tests/MarketplaceHub.Tests/SafetyAndStabilityTests.cs`

- [ ] Add a failing test that passes the old route set and asserts the audit reports these missing route keys: `bulk-products`, `listing-matrix`, `order-exceptions`, `readiness`, `backup`, `migration`, and `locale`.
- [ ] Run the focused test and verify RED because the new route keys are absent from `RequiredRoutes`.
- [ ] Expand `ScreenParityAudit.RequiredRoutes` to the real shell contract, including the already registered `categories`, `brands`, `media`, `policy-center`, `marketplaces`, `bizimhesap`, `nes`, and `data-quality` routes plus the seven new route keys.
- [ ] Run the focused test and verify GREEN.
- [ ] Commit: `test: expand operations route contract`.

### Task 2: Add lazy route construction and register the hidden panels

**Files:**

- Modify: `MainWindow.Navigation.cs`
- Modify: `tests/MarketplaceHub.Tests/MarketplaceAccountNavigationTests.cs`

- [ ] Add a failing WPF test in `MarketplaceAccountNavigationTests` that creates the shell with a temporary data root and verifies each new route has one discoverable navigation item and one page.
- [ ] Assert the new heavy route pages have no content before first selection, create content on first selection, and reuse the same content instance on a second selection.
- [ ] Verify RED against the current shell.
- [ ] Add a `Func<UIElement>` lazy `Page` overload (or equivalent small route-factory helper) that stores one factory per route and materializes it once on first selection. Keep the current eager overload for existing lightweight pages.
- [ ] Register routes using existing factories only:
  - `bulk-products` → `BulkProductsPanel.Create(dataDirectory, OpenMarketplaceRoute)` under **KATALOG**.
  - `listing-matrix` → `ChannelListingMatrixPanel.Create(dataDirectory, OpenMarketplaceRoute)` under **MAĞAZALAR**.
  - `order-exceptions` → `OrderExceptionsPanel.Create(dataDirectory, OpenMarketplaceRoute)` under **OPERASYON**.
  - `readiness` → `ProductionReadinessPanel.Create(dataDirectory)` under **İZLEME**.
  - `locale` → `LocaleSettingsPanel.Create(dataDirectory)` under **SİSTEM**.
  - `backup` → `DataBackupPanel.Create(dataDirectory)` under **SİSTEM**.
  - `migration` → `MigrationAssistantPanel.Create(dataDirectory)` under **SİSTEM**.
- [ ] Give every route a concise Turkish title, description, breadcrumb title, and stable route key.
- [ ] Confirm first selection performs local reads/schema initialization only. If a factory performs network I/O or business-data mutation during construction, stop and split that behavior behind an explicit user action before registering it.
- [ ] Run the focused navigation tests and verify GREEN.
- [ ] Commit: `feat: expose existing operations panels`.

### Task 3: Connect the exact known cross-panel entry points

**Files:**

- Modify: `SettingsHubPanel.cs`
- Modify: `MarketplaceAccountHomePanel.cs`
- Modify: `tests/MarketplaceHub.Tests/SettingsHubPanelTests.cs`
- Modify: `tests/MarketplaceHub.Tests/MarketplaceAccountNavigationTests.cs`
- Modify/add focused panel tests for each changed callback.

- [ ] Write failing tests that Settings exposes route callbacks for `locale`, `backup`, `migration`, and `readiness` without embedding duplicate `LocaleSettingsPanel`/`DataBackupPanel` instances.
- [ ] Replace Settings “Yerel ayarlar” and “Veri ve yedek” embedded panel content with clear route buttons; add Migration under “Veri ve yedek” and Readiness under “Sağlık ve tanılama”. Preserve the seven Settings section labels and named roots.
- [ ] Write a failing test that the marketplace account home exposes a visible `listing-matrix` shortcut and emits exactly that route.
- [ ] Add the marketplace listing-matrix shortcut without choosing an account or triggering a connector read.
- [ ] Keep the specialist Orders “İade yönetimi” modal unchanged; the canonical `order-exceptions` page is an additional discoverable entry point, not a replacement.
- [ ] Verify `BulkProductsPanel`, `ChannelListingMatrixPanel`, and `OrderExceptionsPanel` existing callbacks continue to emit their established route keys.
- [ ] Verify the existing Global Search unknown-channel result now lands on the registered `listing-matrix` route; no Global Search behavior rewrite is needed.
- [ ] Do not bind legacy accountless marketplace panels (`TrendyolPanel`, `AmazonPanel`, `HepsiburadaPanel`, `EbayPanel`, `OzonPanel`, `WishPanel`, `AllegroPanel`, `FruugoPanel`, `JoomPanel`, `MarketplaceSetupPanel`, global `EtsyReadinessPanel`). Exact-account navigation remains authoritative.
- [ ] Do not create a standalone inventory route in T0: physical locations and transfers remain reachable from Orders/Product inventory and are completed in T1.
- [ ] Run focused tests and verify GREEN.
- [ ] Commit: `feat: connect operations navigation shortcuts`.

### Task 4: Verify the complete shell and document the release

**Files:**

- Modify: `TODO.md`
- Modify: `IMPLEMENTATION_STATUS.md`
- Modify if needed: `docs/UI-HANDOFF-NAVIGATION.md`

- [ ] Run navigation, settings, panel construction, and `ScreenParityAudit` focused tests.
- [ ] Run `dotnet test tests/MarketplaceHub.Tests/MarketplaceHub.Tests.csproj -c Release`.
- [ ] Run `dotnet publish TrMarketplaceHubDesktop.csproj -c Release -r win-x64 --self-contained true -o Windows-Current`.
- [ ] Launch only against an isolated temporary data directory for smoke verification; do not use the real `%LOCALAPPDATA%\MonoBridgeDesktop` profile.
- [ ] Verify the seven new pages are reachable, show their correct title/description/breadcrumb, and opening them causes no network/write action or business-data mutation.
- [ ] Update `TODO.md` and `IMPLEMENTATION_STATUS.md` with exact test count, publish result, and any visual/UAC limitation.
- [ ] Commit: `docs: record T0 operations navigation release`.

## Completion Evidence

- Exact focused and full test commands with pass counts.
- `Windows-Current\TrMarketplaceHubDesktop.exe` publish path.
- Route inventory showing old and new keys.
- Confirmation that no live marketplace HTTP/write and no real profile mutation occurred.
- Separate list of T1+ work not implemented in T0.
