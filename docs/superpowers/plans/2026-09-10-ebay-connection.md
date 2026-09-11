# eBay connection Implementation Plan

> **For agentic workers:** Execute inline using executing-plans; preserve the shared workspace and existing connectors.

**Goal:** Let the owner configure eBay developer keys, authorize their seller account, and read seller registration status without publishing.

**Architecture:** A UI-independent OAuth client owns a ten-minute, single-use authorization attempt. A separate DPAPI store persists configuration and tokens; a WPF panel invokes only OAuth token POST and Account privilege GET.

**Tech Stack:** .NET 8, WPF, HttpClient, System.Text.Json, existing Windows CurrentUser DPAPI.

**Spec:** Parent task: bounded OAuth/settings/read-only verification; no developer keys or completed seller onboarding currently available.

## Global Constraints
- Preserve Etsy/Navlungo and the shared catalog.
- Never report connected based on saved configuration or tokens alone.
- No external account mutation, publishing, secrets in errors, or running binary replacement.
- HTTPS callback configured separately from eBay RuName; reject wrong origin/path, duplicate parameters, missing/mismatched state, expired/replayed attempts.

### Task 1: OAuth and read-only API boundary
Files: create `EbayConnection.cs`; tests `../MonoBridgeDesktop.Tests/EbayConnectionTests.cs`; link into tests project.
- [x] Write failing tests for encoded scope/RuName and sandbox isolation; callback mismatch/replay/expiration; token form exchange; privilege true/false/malformed/error handling.
- [x] Run `dotnet test ../MonoBridgeDesktop.Tests --filter FullyQualifiedName~EbayConnectionTests` and confirm missing implementation.
- [x] Implement `EbayConnection.Begin(EbaySettings)` returning attempt, `CompleteAsync(attempt, callback)` returning expiring token, and `VerifyAsync(settings, token)` returning registration flag. Refresh only expired tokens; redact HTTP errors.
- [x] Run the tests and inspect every failure.

### Task 2: Local credential storage and UI
Files: create `EbaySettingsStore.cs`, `EbayPanel.cs`; modify `MarketplaceSetupPanel.cs`.
- [x] Write DPAPI roundtrip test proving secrets absent from stored bytes.
- [x] Implement atomic encrypted file storage, clearing byte buffers after use.
- [x] Add App ID, masked Cert ID, RuName, accepted HTTPS callback, sandbox choice, save, consent, callback exchange, verify and local disconnect controls. Reset displayed verification when configuration changes. Keep pending attempt only in memory.
- [x] Run full test project and desktop Release build. Report that real eBay verification remains unavailable until the user supplies keys and consent.

References: https://developer.ebay.com/develop/guides/sell/authorization and https://developer.ebay.com/api-docs/sell/static/seller-accounts/ht_get-selling-limits.html

Verification: official Account OpenAPI confirms `/privilege` and `sell.account.readonly`: https://developer.ebay.com/api-docs/master/sell/account/openapi/3/sell_account_v1_oas3.json . The older guide shows a trailing slash; the implementation uses the OpenAPI canonical path because redirects are disabled to protect credentials.

