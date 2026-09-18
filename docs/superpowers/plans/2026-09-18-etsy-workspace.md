# Etsy Workspace Implementation Plan

> **For agentic workers:** Use superpowers:subagent-driven-development for isolated API work and root-led workspace integration. Steps use checkbox syntax for tracking.

**Goal:** Expose a usable Etsy integration with the Trendyol control/settings/history workflow, current API contracts and safe previews.
**Architecture:** Keep existing OAuth, credential, image and catalog services. Add an Etsy metadata client, shop-scoped workspace/plan/receipt service and focused WPF partial panels. Root owns UI/integration; independent API task owns metadata/auth contracts.
**Tech Stack:** .NET 8 WPF, HttpClient, System.Text.Json, Microsoft.Data.Sqlite, MSTest.
**Spec:** docs/superpowers/specs/2026-09-18-etsy-workspace-design.md

## Global Constraints

Preserve live credentials/data and unrelated BizimHesap edits. Never run live writes in automated tests. No variant/bundle/inline-edit/critical-price/XML-variant/fulfillment/settlement implementation. No secrets in tracked files. All account comparisons use shop and authorized app/user identity; token refresh does not invalidate the account identity.

## Task 1: Current API metadata and account contract

Files: EtsyMetadataClient.cs, EtsyOAuth.cs, EtsyConnector.cs, tests/MarketplaceHub.Tests/EtsyMetadataTests.cs. Independently testable.

Interfaces consumed: existing EtsyCredentials and EtsyHttp.
Produced: EtsyShopInfo(ShopId,Name,Currency,UserId), EtsyTaxonomyNode(Id,Name,Path,ParentId), EtsyPropertyDefinition(Id,Name,Required,SupportsAttributes,SupportsVariations,Values,Scales), EtsyNamedValue(Id,Name), EtsyShippingProfile(Id,Name), EtsyProcessingProfile(Id,Name,ReadinessState), EtsyShopSection(Id,Name). All IDs long; collection properties IReadOnlyList. Metadata methods GetShopAsync, GetSellerTaxonomyAsync, GetPropertiesAsync(credentials,taxonomyId), GetShippingProfilesAsync, GetProcessingProfilesAsync, GetSectionsAsync; all Task and optional CancellationToken.

- [x] Verify actual official schema paths, pagination, shop ownership and supported OAuth scopes.
- [x] Fake HTTP tests first: exact routes/headers, taxonomy recursion, properties, profile pagination, wrong shop/user, invalid response, cancellation, granted-scope preservation.
- [x] Implement bounded validated GETs and compatible OAuth scope tracking; no UI changes or real credentials.
- [x] Run focused tests and review APIs before workspace integration.

## Task 2: Workspace, matching and immutable operation plans

Files: Etsy/EtsyWorkspaceModels.cs, Etsy/EtsyWorkspaceStore.cs, Etsy/EtsyWorkspaceService.cs, Etsy/EtsyWorkspaceDispatch.cs; tests/MarketplaceHub.Tests/EtsyWorkspaceTests.cs.

- [x] Shop-scoped SQLite state with revision; named templates, product profiles, category mappings, cached remote rows/metadata, read timestamps.
- [x] Tests first for exact/ambiguous SKU matching, shop isolation, template inheritance, selected-field updates, wrong currency, invalid or unsupported product state.
- [x] Persist immutable preview snapshots and atomic claim/receipt entries. Tests reject stale catalog/state/account, duplicate replay and uncertain retry. Dispatch uses documented listing/inventory endpoints and verifies ownership.
- [x] Verify that local catalog identity and unrelated fields remain unchanged.

## Task 3: Etsy desktop workspace and connection

Files: EtsyWorkspacePanel*.cs, MainWindow.Navigation.cs, MainWindow.xaml.cs, tests/MarketplaceHub.Tests/EtsyWorkspacePanelTests.cs.

- [x] Test Etsy navigation visibility, separate settings tabs, useful grid height and no-send-without-preview.
- [x] Build control/list/filter/bulk/matching/product-card/preview/history UI with Turkish labels and current shop context.
- [x] Build masked credential connection, OAuth callback, API dictionaries and named template/category controls. Reuse existing credentials and preserve unsaved fields deliberately.
- [x] Open browser account session to inspect existing integration; use saved tokens for bounded read-only shop/profile/listing verification. Do not invent connection success or request new credentials unnecessarily.

## Task 4: Review and release

- [x] Inspect combined changes for secrets, deferred scope, conflicting writes and mutation guards. Resolve findings.
- [x] Run dotnet test tests/MarketplaceHub.Tests/MarketplaceHub.Tests.csproj -c Release.
- [x] Run dotnet publish TrMarketplaceHubDesktop.csproj -c Release -r win-x64 --self-contained true -o Windows-Excel-Final.
- [x] Launch published EXE, verify Etsy navigation/control/settings with real screenshots, update TODO/IMPLEMENTATION_STATUS and commit/push only task files.
- [x] Report precise live-read and live-write status separately.
