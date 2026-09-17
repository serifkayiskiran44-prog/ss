# BizimHesap V1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a secure BizimHesap product/depot read connection and reviewed product-create workflow to MonoBridge.

**Architecture:** `BizimHesapConnection` owns the verified `api/b2b` HTTP contract and uses DPAPI-backed settings. A SQLite matching/receipt store prevents duplicate creates. WPF presents connection settings, product matching and immutable creation preview; stock and price writes remain blocked until a verified endpoint is provided.

**Tech Stack:** .NET 8 WPF, Microsoft.Data.Sqlite, System.Net.Http, System.Text.Json, MSTest.

**Spec:** `docs/superpowers/specs/2026-09-17-bizimhesap-xml-integration-design.md`

## Global Constraints

- Credentials use `CredentialStore` DPAPI; no secret enters SQLite, logs, audit, test fixtures or documentation.
- Only `https://bizimhesap.com/api/b2b/` may receive the Token header; redirects never forward it.
- Product creation requires immutable preview and explicit operator confirmation.
- No stock or price update HTTP request is issued without a verified endpoint.
- Finish with Release tests and self-contained win-x64 publish.

---

### Task 1: Connection settings and read-only client

**Files:**
- Create: `BizimHesapConnection.cs`, `BizimHesapSettingsStore.cs`
- Create: `tests/MarketplaceHub.Tests/BizimHesapConnectionTests.cs`
- Modify: `MarketplaceConnectionStore.cs`

**Interfaces:** Produces `BizimHesapSettings(string FirmId, string Token)`, `BizimHesapProduct`, `BizimHesapWarehouse`, `BizimHesapInventory`, and read methods for products, depots and inventory.

- [ ] **Step 1: Write the failing test**

```csharp
[TestMethod]
public async Task ReadProductsAsync_sends_token_only_to_verified_https_host()
{
    var api = new BizimHesapConnection(new HttpClient(new StubHandler("{\"data\":[]}")));
    await api.ReadProductsAsync(new("firm", "token"));
    Assert.AreEqual("https://bizimhesap.com/api/b2b/products?page=1&size=100", StubHandler.LastRequest!.RequestUri!.ToString());
    Assert.AreEqual("token", StubHandler.LastRequest.Headers.GetValues("Token").Single());
}
```

- [ ] **Step 2: Run RED**

Run `dotnet test tests/MarketplaceHub.Tests --filter FullyQualifiedName~BizimHesapConnectionTests`; expected compilation failure because the client is absent.

- [ ] **Step 3: Implement the smallest safe client**

```csharp
public Task<IReadOnlyList<BizimHesapProduct>> ReadProductsAsync(BizimHesapSettings settings, int page = 1, int size = 100, CancellationToken cancellationToken = default);
public Task<IReadOnlyList<BizimHesapWarehouse>> ReadWarehousesAsync(BizimHesapSettings settings, CancellationToken cancellationToken = default);
public Task<IReadOnlyList<BizimHesapInventory>> ReadInventoryAsync(BizimHesapSettings settings, string warehouseId, CancellationToken cancellationToken = default);
```

Validate bounded input, reject redirects, redact errors and parse expected JSON only. Add the `bizimhesap` connection catalog entry.

- [ ] **Step 4: Run GREEN**

Run the same command; expected PASS for success, malformed JSON, unauthorized, rate-limit and redirect-safe cases.

### Task 2: Matching, immutable preview and receipts

**Files:**
- Create: `BizimHesapProductSyncStore.cs`, `BizimHesapProductSync.cs`
- Create: `tests/MarketplaceHub.Tests/BizimHesapProductSyncTests.cs`

**Interfaces:** Consumes Task 1 models and `CatalogProduct`; produces `BizimHesapMatchResult`, `BizimHesapCreatePreview`, `PreviewCreate` and `ApplyApprovedCreateAsync`.

- [ ] **Step 1: Write the failing test**

```csharp
[TestMethod]
public void PreviewCreate_marks_duplicate_barcode_as_ambiguous()
{
    var preview = BizimHesapProductSync.PreviewCreate(local, [remoteA, remoteB], "connection");
    Assert.AreEqual(BizimHesapMatchStatus.Ambiguous, preview.Match.Status);
    Assert.IsFalse(preview.CanCreate);
}
```

- [ ] **Step 2: Run RED**

Run `dotnet test tests/MarketplaceHub.Tests --filter FullyQualifiedName~BizimHesapProductSyncTests`; expected compilation failure because sync types are absent.

- [ ] **Step 3: Implement the minimum safe behavior**

```csharp
public static BizimHesapCreatePreview PreviewCreate(CatalogProduct local, IReadOnlyList<BizimHesapProduct> remote, string connectionId);
public Task<BizimHesapCreateResult> ApplyApprovedCreateAsync(BizimHesapCreatePreview preview, bool approved, CancellationToken cancellationToken = default);
```

Persist only ids, match method, fingerprint and timestamps. Use saved mapping, unique barcode then unique SKU. Block ambiguous, stale, unapproved and already-receipted payloads before HTTP. Build `addproduct` with net price, KDV, currency, quantity and barcode.

- [ ] **Step 4: Run GREEN**

Run the focused sync tests; expected PASS for ambiguity, stale preview, duplicate prevention, currency and KDV behavior.

### Task 3: WPF connection, product and dashboard surface

**Files:**
- Create: `BizimHesapPanel.cs`
- Create: `tests/MarketplaceHub.Tests/BizimHesapPanelTests.cs`
- Modify: `MainWindow.Navigation.cs`, `MarketplaceConnectionsPanel.cs`, `DashboardData.cs`, `DashboardPanel.cs`

**Interfaces:** Consumes Tasks 1 and 2; produces `BizimHesapPanel.Create(string? directory)` and the `bizimhesap` route.

- [ ] **Step 1: Write the failing test**

```csharp
[TestMethod]
public void BizimHesapPanel_exposes_settings_read_and_preview_actions()
{
    var labels = VisualTreeText.Read(BizimHesapPanel.Create(tempDirectory));
    StringAssert.Contains(labels, "Bağlantı ayarları");
    StringAssert.Contains(labels, "Ürün eşleştirme önizlemesi");
    StringAssert.Contains(labels, "Stok / fiyat güncellemesi API doğrulaması bekliyor");
}
```

- [ ] **Step 2: Run RED**

Run `dotnet test tests/MarketplaceHub.Tests --filter FullyQualifiedName~BizimHesapPanelTests`; expected compilation failure because the panel is absent.

- [ ] **Step 3: Implement the WPF screen**

Add a route with product and connection tabs. Show masked credential state, depot selector, read tables, matching/creation preview, distinct confirmation action and receipt outcome. Add a dashboard card and connection-health row. Never display a credential or an automatic write action.

- [ ] **Step 4: Run GREEN**

Run the focused UI tests; expected PASS.

### Task 4: Release verification and operator docs

**Files:**
- Modify: `TODO.md`, `docs/pazaryeri-api-durumu.md`, `README-YONETIM-MERKEZI.md`

- [ ] **Step 1: Document the operator path**

Describe connection test, mandatory preview, explicit create confirmation, read-back and blocked stock/price updates.

- [ ] **Step 2: Verify and publish**

Run `dotnet test tests/MarketplaceHub.Tests -c Release` and `dotnet publish TrMarketplaceHubDesktop.csproj -c Release -r win-x64 --self-contained true -o Windows-BizimHesap-V1`; expected all tests pass and `TrMarketplaceHubDesktop.exe` is produced.

- [ ] **Step 3: Commit**

Run `git add .` followed by `git commit -m "feat: add BizimHesap product integration"` after checking that no credential artifact is staged.
