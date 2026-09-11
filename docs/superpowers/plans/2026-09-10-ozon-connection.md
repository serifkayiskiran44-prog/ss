# Ozon Connection Implementation Plan

**Goal:** Save Ozon credentials with Windows DPAPI and verify product and warehouse read access.
**Architecture:** Follow EbayConnection / EbaySettingsStore / EbayPanel, with independent read checks and no publishing.
**Tech Stack:** .NET 8 WPF, HttpClient, System.Text.Json, xUnit.
**Scope:** Client-Id and Api-Key headers; fixed api-seller.ozon.ru origin; no writes, redirects or logged secrets.

- [x] Add HTTP boundary and encrypted-storage tests in OzonConnectionTests.cs; run and observe missing implementation failure.
- [x] Implement OzonConnection.cs for POST /v3/product/list (limit 1) and POST /v1/warehouse/list (empty JSON). Reject malformed success payloads, return HTTP errors without response bodies.
- [x] Implement OzonSettingsStore.cs using CredentialStore DPAPI and atomic replacement. Roundtrip and deletion tests.
- [x] Implement OzonPanel.cs with PasswordBox, save/test/delete and honest session status; wire MarketplaceSetupPanel.cs.
- [x] Run full test project and Release build; publish self-contained win-x64 to ../MonoBridgeNet8/Windows-Ozon-Baglanti.

Verification: 199 tests passed, 0 failed/skipped; Release build 0 warnings/errors; self-contained publish succeeded. Live credentials were not available to this implementation. Official docs could not be fetched or rendered; see docs/ozon-baglanti.md.
