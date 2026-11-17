# Entegra-Inspired Operations UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebuild MonoBridge's shell, settings, Product Management, and XML Management into one original, dense Turkish operations UI while restoring complete navigation and preserving every existing safety boundary.

**Architecture:** Add one shared WPF resource dictionary and a focused settings hub, then recompose existing panels without changing their stores or live-operation contracts. Navigation will register the already-implemented operational panels, Product and XML pages will expose named command/filter/grid/footer regions, and UI contract tests will pin the hierarchy and route behavior.

**Tech Stack:** .NET 8, WPF, C#, MSTest, SQLite-backed existing stores.

**Spec:** `docs/superpowers/specs/2026-11-17-entegra-inspired-operations-ui-design.md`

## Global Constraints

- Preserve all existing SQLite data, DPAPI credentials, `MARKETPLACEHUB_DATA_DIR`, background behavior, exact account isolation, previews, approvals, stale/revision checks, receipts, and wrong-store guards.
- Do not add an application login or password.
- Do not copy Entegra branding, source code, assets, texts, schemas, or pixel layout.
- Do not enable unsupported/live-blocked marketplace capabilities.
- Do not implement bundles, variants, unrestricted inline quick edit, critical price, XML variant mapping, fulfillment core, or settlement/reconciliation core.
- No automated or visual test may write to a real marketplace.
- Keep the unrelated new-listing-candidate edits in the original dirty worktree separate from this branch.

## Review Focus

- A 1366×720 work area must retain the global search, primary command bars, grid, paging, and status without clipping.
- A saved obsolete route (`trendyol`, `etsy`, or a removed key) must fall back safely to Marketplace Accounts or General View.
- A missing/unsupported account capability must remain disabled with an explanation after the settings redesign.
- Product and XML long-running actions must not acquire a second execution path or bypass their existing preview/revision checks.
- High product/XML row counts must retain virtualization and must not be materialized merely to render the redesigned shell.

---

### Task 1: Shared visual system and compact shell contract

**Files:**
- Create: `Themes/MonoBridgeTheme.xaml`
- Modify: `App.xaml`
- Modify: `MainWindow.xaml`
- Modify: `tests/MarketplaceHub.Tests/MarketplaceAccountNavigationTests.cs`

**Interfaces:**
- Consumes: existing named shell controls (`NavigationList`, `GlobalSearchBox`, `ModuleTabs`, `StatusText`, `LogList`).
- Produces: shared resource keys `MonoCanvasBrush`, `MonoSurfaceBrush`, `MonoNavigationBrush`, `MonoPrimaryBrush`, `MonoBorderBrush`, `MonoTextBrush`, `MonoMutedTextBrush`, `PrimaryButtonStyle`, `SecondaryButtonStyle`, `ToolbarButtonStyle`, `CompactButtonStyle`, `DangerButtonStyle`, and `CompactDataGridStyle`; named shell regions `GlobalCommandBar` and `PageCommandHost`.

- [ ] **Step 1: Write failing shell/theme tests**

Add tests that instantiate `MainWindow`, assert the global search is in a named persistent top command bar, assert the main content uses compact margins, and resolve every required theme resource key.

- [ ] **Step 2: Verify RED**

Run: `dotnet test tests/MarketplaceHub.Tests/MarketplaceHub.Tests.csproj -c Release --filter FullyQualifiedName~MarketplaceAccountNavigationTests`

Expected: FAIL because the named regions and theme resources do not exist.

- [ ] **Step 3: Implement theme and shell**

Create the resource dictionary with a neutral implicit button style plus keyed action styles, merge it from `App.xaml`, and recompose `MainWindow.xaml` with a persistent compact top command bar, searchable navigation, compact page header, route host, bottom status, and collapsed-by-default operation history. Preserve all existing control names and event handlers; programmatic panels apply keyed styles through resource references instead of hard-coded colors.

- [ ] **Step 4: Verify GREEN and full suite**

Run the targeted command, then `dotnet test tests/MarketplaceHub.Tests/MarketplaceHub.Tests.csproj -c Release`.

Expected: all tests pass; grid virtualization settings remain unchanged.

- [ ] **Step 5: Commit**

`git commit -m "feat: add compact MonoBridge operations shell"`

### Task 2: Complete navigation and General View

**Files:**
- Modify: `MainWindow.Navigation.cs`
- Modify: `MainWindow.UX.cs`
- Modify: `DashboardPanel.cs`
- Modify: `tests/MarketplaceHub.Tests/MarketplaceAccountNavigationTests.cs`
- Modify: `tests/MarketplaceHub.Tests/SafetyAndStabilityTests.cs`

**Interfaces:**
- Consumes: existing panel factories and builders (`DashboardPanel.Create`, `BuildSync`, `BuildAutomation`, `PolicyCenterPanel.Create`, `ApiHealthPanel.Create`, `DataQualityPanel.Create`, `MessagePanel.Create`, `DiagnosticsPanel.Create`, `MediaPanel.Create`).
- Produces: all keys required by `ScreenParityAudit.RequiredRoutes`, including `taxonomy`; default route `dashboard`; channel resolver that maps every marketplace id to `marketplaces` and calls `MarketplaceAccountHomePanel.ShowChannel(channel)`.

- [ ] **Step 1: Write failing route tests**

Assert that the actual `NavigationList` contains every required route once, `ScreenParityAudit.Evaluate` reports `WORKING`, fresh preferences select `dashboard`, saved valid routes win, legacy Trendyol/Etsy routes open Marketplace Accounts, and Amazon/Hepsiburada channel requests also open their account chooser rather than a missing route.

- [ ] **Step 2: Verify RED**

Run: `dotnet test tests/MarketplaceHub.Tests/MarketplaceHub.Tests.csproj -c Release --filter "FullyQualifiedName~MarketplaceAccountNavigationTests|FullyQualifiedName~SafetyAndStabilityTests"`

Expected: FAIL with missing dashboard/sync/automation/api-health/messages/reports routes.

- [ ] **Step 3: Register grouped routes and dashboard**

Implement the spec's Overview/Catalog/Operations/Stores/Monitoring/System grouping. Register the existing taxonomy center under `taxonomy`; use the existing panels; bind `reports` to a dedicated Diagnostics/Audit panel instance. Replace the two-channel special case with one channel resolver and make the compact page header visible for all routes.

- [ ] **Step 4: Verify GREEN and full suite**

Run the targeted command, then the full Release suite.

- [ ] **Step 5: Commit**

`git commit -m "feat: restore complete operations navigation"`

### Task 3: Sectioned settings hub

**Files:**
- Create: `SettingsHubPanel.cs`
- Modify: `MainWindow.Navigation.cs`
- Modify: `MarketplaceAccountHomePanel.cs`
- Create: `tests/MarketplaceHub.Tests/SettingsHubPanelTests.cs`
- Modify: `tests/MarketplaceHub.Tests/MarketplaceAccountNavigationTests.cs`

**Interfaces:**
- Produces: `SettingsHubPanel.SettingsHubPanel(string? directory, BackgroundAppController? backgroundController, Action<string> navigate)` and the resulting `UserControl`.
- Produces named sections: `SettingsSections`, `SettingsGeneral`, `SettingsConnections`, `SettingsAccountRules`, `SettingsScheduling`, `SettingsLocale`, `SettingsData`, `SettingsHealth`.
- Consumes existing `DataBackupPanel`, locale, connection, policy, automation, readiness, diagnostics, and API-health routes; no credential value is copied into the hub.

- [ ] **Step 1: Write failing settings tests**

Assert section labels and names, tray-preference binding, navigation actions, absence of password/token inputs, and a visible `MarketplaceAddStore` action on account home that routes to `connections`.

- [ ] **Step 2: Verify RED**

Run: `dotnet test tests/MarketplaceHub.Tests/MarketplaceHub.Tests.csproj -c Release --filter "FullyQualifiedName~SettingsHubPanelTests|FullyQualifiedName~MarketplaceAccountNavigationTests"`

Expected: FAIL because `SettingsHubPanel` and `MarketplaceAddStore` do not exist.

- [ ] **Step 3: Implement settings hub and add-store route**

Build a left section selector and right content host. Reuse existing safe panels/routes, never reuse the same `UIElement` under two parents, require exact account selection before account rules, and keep unsupported options disabled. Replace the long inline settings `StackPanel` in `BuildNavigation` with a `SettingsHubPanel` instance.

- [ ] **Step 4: Verify GREEN and full suite**

Run targeted and full Release suites.

- [ ] **Step 5: Commit**

`git commit -m "feat: centralize application and store settings"`

### Task 4: Product Management command hierarchy and footer

**Files:**
- Modify: `MainWindow.ProductList.cs`
- Modify: `MainWindow.Filters.cs`
- Modify: `MainWindow.xaml.cs`
- Modify: `tests/MarketplaceHub.Tests/ProductManagementBindingPanelTests.cs`

**Interfaces:**
- Produces named regions `ProductPrimaryCommandBar`, `ProductSecondaryCommandBar`, `ProductBulkCommandBar`, `ProductGrid`, `ProductPagingBar`, and `ProductResultSummary`.
- Consumes existing `RefreshProducts`, `OpenProductCard`, store-binding windows, filters, column preferences, Excel operations, `PreviewProductBulk`, and paging fields without changing their signatures.

- [ ] **Step 1: Write failing product workspace tests**

Assert the named hierarchy, every existing operation remains reachable exactly once, bulk preview stays separate from primary commands, no new quick-edit field is introduced, row height remains 27–29, grid virtualization remains enabled, and paging exposes total/filtered/selected/page/limit state.

- [ ] **Step 2: Verify RED**

Run: `dotnet test tests/MarketplaceHub.Tests/MarketplaceHub.Tests.csproj -c Release --filter FullyQualifiedName~ProductManagementBindingPanelTests`

Expected: FAIL because the named grouped command regions and unified summary do not exist.

- [ ] **Step 3: Recompose product header and footer**

Split primary, secondary/data/store, detailed filter, and bulk command regions. Use shared styles, preserve the current grid columns/edit rules/context menu/row details, and add a compact result summary without enumerating all products.

- [ ] **Step 4: Verify GREEN and full suite**

Run targeted and full Release suites.

- [ ] **Step 5: Commit**

`git commit -m "feat: organize dense product operations workspace"`

### Task 5: XML Management hierarchy and definition editor

**Files:**
- Modify: `MainWindow.XmlWorkspace.cs`
- Modify: `MainWindow.XmlSamples.cs`
- Create: `tests/MarketplaceHub.Tests/XmlWorkspaceUiTests.cs`
- Modify: `tests/MarketplaceHub.Tests/XmlManagementParityTests.cs`

**Interfaces:**
- Produces named regions `XmlPrimaryCommandBar`, `XmlSourcesGrid`, `XmlWorkspaceSplitter`, `XmlCategoryCommandBar`, `XmlCategoriesGrid`, `XmlDefinitionGeneral`, `XmlDefinitionRules`, `XmlDefinitionActions`, and `XmlPreviewSummary`.
- Consumes existing source selection, `SaveSource`, `InspectAsync`, `PreviewAsync`, `RunSelectedXmlAsync`, `ImportAsync`, category-rule editing, stale revision protection, and source auth store.

- [ ] **Step 1: Write failing XML UI tests**

Assert the named hierarchy, a real resizable `GridSplitter`, distinct Run and Preview actions, grouped Template actions, two-pane definition structure, collapsed advanced/auth sections, and a preview summary that updates after `SelectionChanged`.

- [ ] **Step 2: Verify RED**

Run: `dotnet test tests/MarketplaceHub.Tests/MarketplaceHub.Tests.csproj -c Release --filter "FullyQualifiedName~XmlWorkspaceUiTests|FullyQualifiedName~XmlManagementParityTests"`

Expected: FAIL because the named regions and splitter do not exist.

- [ ] **Step 3: Recompose XML workspace**

Apply the shared compact styles, introduce a splitter between the source and category tables, group template commands, preserve all source/category/formula logic, and add persistent status/action bars to definition and preview windows.

- [ ] **Step 4: Verify GREEN and full suite**

Run targeted and full Release suites.

- [ ] **Step 5: Commit**

`git commit -m "feat: redesign XML operations workspace"`

### Task 6: Release verification, visual smoke, and documentation

**Files:**
- Modify: `TODO.md`
- Modify: `IMPLEMENTATION_STATUS.md`
- Create: `docs/UI-ENTEGRA-INSPIRED-OPERATIONS-SMOKE.md`

**Interfaces:**
- Consumes: completed Tasks 1–5.
- Produces: self-contained output directory `Windows-Entegra-Operations`; isolated smoke profile under a temporary directory set through `MARKETPLACEHUB_DATA_DIR`.

- [ ] **Step 1: Run full verification**

Run: `dotnet test tests/MarketplaceHub.Tests/MarketplaceHub.Tests.csproj -c Release`

Expected: all tests pass with zero failures.

- [ ] **Step 2: Publish**

Run: `dotnet publish TrMarketplaceHubDesktop.csproj -c Release -r win-x64 --self-contained true -o Windows-Entegra-Operations`

Expected: `Windows-Entegra-Operations/TrMarketplaceHubDesktop.exe` and its runtime metadata exist.

- [ ] **Step 3: Perform isolated visual smoke**

Launch with a new temporary `MARKETPLACEHUB_DATA_DIR`; inspect General View, Product Management, XML Management, Settings, Marketplace Accounts, and Orders at 1366×720 and maximized size. Confirm no clipping, correct focus order, persistent search, route access, dense grids, and no live HTTP/write.

- [ ] **Step 4: Update status documentation**

Record exact test count, publish path, visual observations, known warnings, and the separate unmerged new-listing-candidate work. Reconcile stale navigation parity statements.

- [ ] **Step 5: Commit**

`git commit -m "docs: record operations UI verification"`

