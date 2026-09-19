# Hepsiburada Integration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Hepsiburada a fully operational, multi-account MonoBridge channel for safe connection, product matching/publication, listing control, Buybox analysis and unified order stock.

**Architecture:** Add a typed fixed-host Hepsiburada HTTP boundary and account-scoped DPAPI credentials, then build shop-scoped SQLite state, immutable previews/receipts and a specialized WPF workspace inside the existing marketplace shell. Deliver in three working phases: read-only connection/products, reviewed catalog/listing/competition writes, and unified orders/release verification.

**Tech Stack:** .NET 8, WPF, HttpClient, System.Text.Json, Microsoft.Data.Sqlite, Windows DPAPI, MSTest.

**Spec:** `docs/superpowers/specs/2026-09-19-hepsiburada-integration-design.md`

## Global Constraints

- The official Hepsiburada Developer Portal is authoritative for request paths, fields and authentication; Entegra pages are workflow references only.
- Windows 11 and PowerShell 5.1 remain supported; the application targets .NET 8 WPF.
- New credentials use the connection-scoped Windows DPAPI vault; plaintext secrets never enter source, SQLite, fixtures, logs, screenshots, reports or package manifests.
- Merchant ID is the account `ShopId`; the supplied service key is tested only through a merchant-scoped read request before the account becomes operational.
- SIT and Production hosts cannot mix in one client. Production is the default; SIT requires an explicit setting.
- Automatic identity uses exact barcode, then non-contradictory exact Merchant SKU, then explicit manual review. Local SKU is never copied into barcode.
- Every remote mutation uses an immutable preview, explicit approval, account/product/binding revision checks and a durable receipt.
- Automated tests use temporary directories and fake HTTP handlers; they never call or write to a live marketplace.
- First-release Buybox scheduling is read-only; proposed prices require the ordinary price preview and approval flow.
- Campaign writes, package-state writes, cargo labels, invoice upload, settlements and return/claim mutations remain excluded.
- Exact final regression command: `dotnet test tests\MarketplaceHub.Tests\MarketplaceHub.Tests.csproj -c Release`.
- Exact publish command: `dotnet publish TrMarketplaceHubDesktop.csproj -c Release -r win-x64 --self-contained true -o Windows-Excel-Final`.

## File Structure

- `Hepsiburada/HepsiburadaContracts.cs`: credentials, environments and typed API models.
- `Hepsiburada/HepsiburadaApiClient.cs`: fixed-host authenticated HTTP, bounded JSON and read/write endpoints.
- `Hepsiburada/HepsiburadaWorkspaceModels.cs`: cached metadata, merchant products, listings, mappings, plans and receipts.
- `Hepsiburada/HepsiburadaWorkspaceStore.cs`: connection-scoped SQLite cache, mappings, revisions, plans and receipts.
- `Hepsiburada/HepsiburadaMatching.cs`: deterministic barcode/SKU/manual matching.
- `Hepsiburada/HepsiburadaWorkspaceService.cs`: metadata/product refresh and preview construction.
- `Hepsiburada/HepsiburadaDispatch.cs`: claimed plan dispatch and result reconciliation.
- `Hepsiburada/HepsiburadaCompetition.cs`: bounded Buybox price proposals.
- `Hepsiburada/HepsiburadaOrderReader.cs`: order API projection to shared `OrderSnapshot`.
- `HepsiburadaWorkspacePanel*.cs`: specialized account control/settings/competition/history UI.
- Existing `HepsiburadaConnection.cs` and `HepsiburadaPanel.cs`: legacy migration facade and simple account credential editor.
- Existing marketplace stores/registry/host: capability registration, credential type allow-list, account adapter and workspace routing.

## Review Focus

- A Merchant ID/service-key pair that authenticates but returns another merchant must stay non-operational; Task 2 tests identity mismatch.
- Duplicate remote barcodes or a barcode/SKU contradiction must become review conflicts rather than silent bindings; Task 4 tests both.
- A `429` with malformed or extreme reset headers must use a bounded retry and never sleep indefinitely; Task 3 tests the bounds.
- A network loss after a catalog/listing POST must create an uncertain receipt and block automatic replay; Task 6 tests this failure.
- The same Hepsiburada order returned in two pages or sync runs must reduce online stock once; Task 8 tests duplicate pages and repeated runs.

---

### Task 1: Account-scoped credentials and migration boundary

**Files:**
- Create: `Hepsiburada/HepsiburadaContracts.cs`
- Modify: `HepsiburadaConnection.cs`
- Modify: `MarketplaceCredentialVault.cs`
- Test: `tests/MarketplaceHub.Tests/HepsiburadaCredentialTests.cs`

**Interfaces:**
- Consumes: `MarketplaceCredentialVault.Save<T>` / `Load<T>` and legacy `HepsiburadaSettingsStore`.
- Produces: `HepsiburadaEnvironment`, `HepsiburadaCredentials`, `HepsiburadaConnection.Validate(HepsiburadaCredentials)` and exact vault support for channel `hepsiburada`.

- [ ] **Step 1: Write failing credential, isolation and redaction tests**

```csharp
[TestMethod]
public void HepsiburadaCredentialsAreAccountScopedAndNeverCrossShop()
{
    var vault = new MarketplaceCredentialVault(directory);
    var credentials = new HepsiburadaCredentials("merchant-a", "secret", HepsiburadaEnvironment.Production, "MonoBridge/1");
    vault.Save("hb-a", "hepsiburada", "merchant-a", credentials);
    Assert.AreEqual(credentials, vault.Load<HepsiburadaCredentials>("hb-a", "hepsiburada", "merchant-a"));
    Assert.ThrowsException<InvalidOperationException>(() => vault.Load<HepsiburadaCredentials>("hb-a", "hepsiburada", "merchant-b"));
}

[TestMethod]
public void HepsiburadaValidationRejectsControlCharactersAndUnboundedSecrets()
{
    Assert.ThrowsException<ArgumentException>(() =>
        HepsiburadaConnection.Validate(new("merchant", "bad\r\nsecret", HepsiburadaEnvironment.Production, "MonoBridge/1")));
}
```

- [ ] **Step 2: Run the credential tests and verify RED**

Run: `dotnet test tests\MarketplaceHub.Tests\MarketplaceHub.Tests.csproj -c Release --filter FullyQualifiedName~HepsiburadaCredentialTests`

Expected: FAIL because `HepsiburadaCredentials` and its vault allow-list entry do not exist.

- [ ] **Step 3: Implement the exact credential contract and vault identity check**

```csharp
namespace TrMarketplaceHubDesktop.Hepsiburada;

public enum HepsiburadaEnvironment { Production, Sit }

public sealed record HepsiburadaCredentials(
    string MerchantId,
    string ServiceKey,
    HepsiburadaEnvironment Environment,
    string UserAgent);
```

Update `HepsiburadaConnection.Validate` to require a trimmed Merchant ID, service key and User-Agent within explicit length bounds and without control characters. Extend `EnsureAllowedSave`, `EnsureAllowedLoad` and `ValidatePayloadIdentity` so `HepsiburadaCredentials.MerchantId` must equal the vault `shopId`. Keep the legacy settings store readable; do not copy it into a connection vault until its Merchant ID matches the target connection and save/read-back succeeds.

- [ ] **Step 4: Run focused credential and existing vault tests**

Run: `dotnet test tests\MarketplaceHub.Tests\MarketplaceHub.Tests.csproj -c Release --filter "FullyQualifiedName~HepsiburadaCredentialTests|FullyQualifiedName~MarketplaceCredentialVaultTests"`

Expected: PASS; existing Etsy and Trendyol vault isolation remains intact.

- [ ] **Step 5: Commit the credential boundary**

```powershell
git add Hepsiburada/HepsiburadaContracts.cs HepsiburadaConnection.cs MarketplaceCredentialVault.cs tests/MarketplaceHub.Tests/HepsiburadaCredentialTests.cs
git commit -m "feat: add account-scoped Hepsiburada credentials"
```

### Task 2: Safe read-only API boundary and identity verification

**Files:**
- Create: `Hepsiburada/HepsiburadaApiClient.cs`
- Modify: `Hepsiburada/HepsiburadaContracts.cs`
- Modify: `HepsiburadaConnection.cs`
- Test: `tests/MarketplaceHub.Tests/HepsiburadaApiTests.cs`

**Interfaces:**
- Consumes: `HepsiburadaCredentials` from Task 1 and an injectable `HttpClient`.
- Produces: `HepsiburadaApiClient`, `HepsiburadaMerchantProduct`, `HepsiburadaListing`, `GetMerchantProductsAsync`, `GetListingsAsync` and `TestReadOnlyAsync`.

- [ ] **Step 1: Write failing fixed-host, authorization, pagination and identity tests**

```csharp
[TestMethod]
public async Task MerchantReadUsesBasicAuthMerchantPathAndUserAgent()
{
    var handler = new RecordingHandler("""{"data":[{"merchantId":"merchant-a","merchantSku":"SKU-1","barcode":"8690000000001","hbSku":"HB1"}],"totalPages":1,"number":0}""");
    using var client = new HepsiburadaApiClient(Credentials("merchant-a"), new HttpClient(handler));
    var rows = await client.GetMerchantProductsAsync();
    Assert.AreEqual("/product/api/products/all-products-of-merchant/merchant-a?page=0&size=1000", handler.Requests.Single().PathAndQuery);
    Assert.AreEqual("Basic", handler.Requests.Single().AuthorizationScheme);
    Assert.AreEqual("MonoBridge/1", handler.Requests.Single().UserAgent);
    Assert.AreEqual("HB1", rows.Single().HepsiburadaSku);
}

[TestMethod]
public async Task ConnectionTestRejectsAResponseOwnedByAnotherMerchant()
{
    using var client = ClientReturningMerchant("merchant-b");
    await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => client.TestReadOnlyAsync());
}
```

Also pin Production/SIT host selection, URL escaping, duplicate pages, invalid JSON, oversized response, cancellation, `401` redaction and disabled redirects.

- [ ] **Step 2: Run the API tests and verify RED**

Run: `dotnet test tests\MarketplaceHub.Tests\MarketplaceHub.Tests.csproj -c Release --filter FullyQualifiedName~HepsiburadaApiTests`

Expected: FAIL because no live API client exists.

- [ ] **Step 3: Implement the bounded API client core and read endpoints**

```csharp
public sealed class HepsiburadaApiClient : IDisposable
{
    public Task<IReadOnlyList<HepsiburadaMerchantProduct>> GetMerchantProductsAsync(
        CancellationToken cancellationToken = default);

    public Task<IReadOnlyList<HepsiburadaListing>> GetListingsAsync(
        CancellationToken cancellationToken = default);

    public async Task<HepsiburadaConnectionIdentity> TestReadOnlyAsync(
        CancellationToken cancellationToken = default)
    {
        var products = await GetMerchantProductsPageAsync(0, 1, cancellationToken);
        if (products.MerchantId.Length > 0 && products.MerchantId != credentials.MerchantId)
            throw new InvalidOperationException("Hepsiburada yanıtı farklı mağazaya ait.");
        return new(credentials.MerchantId, credentials.Environment, DateTime.UtcNow);
    }
}
```

Use `https://mpop.hepsiburada.com/product` and `https://listing-external.hepsiburada.com` in Production; insert `-sit` only in SIT. Apply Basic Auth from Merchant ID/service key, add required User-Agent and JSON headers, cap response bytes/pages/rows, and create the production handler with `AllowAutoRedirect = false` and `UseCookies = false`.

- [ ] **Step 4: Run focused API and HTTP safety tests**

Run: `dotnet test tests\MarketplaceHub.Tests\MarketplaceHub.Tests.csproj -c Release --filter "FullyQualifiedName~HepsiburadaApiTests|FullyQualifiedName~AuthHttpRedirectSafetyTests|FullyQualifiedName~SecretRedactionTests"`

Expected: PASS with no credential text in assertion output.

- [ ] **Step 5: Commit read-only API support**

```powershell
git add Hepsiburada/HepsiburadaApiClient.cs Hepsiburada/HepsiburadaContracts.cs HepsiburadaConnection.cs tests/MarketplaceHub.Tests/HepsiburadaApiTests.cs
git commit -m "feat: add safe Hepsiburada read API"
```

### Task 3: Rate limits and current metadata contracts

**Files:**
- Modify: `Hepsiburada/HepsiburadaApiClient.cs`
- Modify: `Hepsiburada/HepsiburadaContracts.cs`
- Test: `tests/MarketplaceHub.Tests/HepsiburadaMetadataTests.cs`

**Interfaces:**
- Consumes: API client core from Task 2.
- Produces: `GetCategoriesAsync`, `GetCategoryAttributesAsync`, `GetAttributeValuesAsync`, `GetBuyboxAsync`, `GetCommissionsAsync` and bounded `429` retry behavior.

- [ ] **Step 1: Write failing metadata, leaf-category and bounded-rate-limit tests**

```csharp
[TestMethod]
public async Task CategoriesExposeOnlyPublishableLeafEntriesToTheWorkspace()
{
    using var client = ClientReturningCategories(
        new { categoryId = 10, name = "Kap", leaf = true, available = true, status = "ACTIVE" },
        new { categoryId = 11, name = "Kapalı", leaf = true, available = false, status = "ACTIVE" });
    var categories = await client.GetCategoriesAsync();
    CollectionAssert.AreEqual(new long[] { 10 }, categories.Where(x => x.Publishable).Select(x => x.Id).ToArray());
}

[TestMethod]
public async Task ExtremeRateLimitResetUsesBoundedDelay()
{
    var delay = new RecordingDelay();
    using var client = ClientReturning429ThenSuccess("999999999", delay);
    await client.GetBuyboxAsync(["HB1"]);
    Assert.IsTrue(delay.Delays.Single() <= TimeSpan.FromSeconds(30));
}
```

- [ ] **Step 2: Run metadata tests and verify RED**

Run: `dotnet test tests\MarketplaceHub.Tests\MarketplaceHub.Tests.csproj -c Release --filter FullyQualifiedName~HepsiburadaMetadataTests`

Expected: FAIL because metadata/Buybox contracts are absent.

- [ ] **Step 3: Implement metadata and Buybox reads with bounded retries**

```csharp
public sealed record HepsiburadaCategory(long Id, string Name, string Path, bool Leaf, bool Available, bool Active)
{
    public bool Publishable => Leaf && Available && Active;
}

public sealed record HepsiburadaAttribute(
    string Id, string Name, bool Mandatory, bool MultiValue, HepsiburadaAttributeKind Kind,
    IReadOnlyList<HepsiburadaAttributeValue> Values);

public sealed record HepsiburadaBuybox(
    string HepsiburadaSku, string MerchantSku, int? Rank, decimal? WinningPrice, decimal? OwnPrice, DateTime ReadUtc);
```

Honor the portal's page-size ceilings. Retry only safe GET requests and at most the bounded count; parse `Retry-After`/rate-limit reset defensively and cap each delay at 30 seconds. Reject duplicate IDs with conflicting values.

- [ ] **Step 4: Run focused metadata/API tests**

Run: `dotnet test tests\MarketplaceHub.Tests\MarketplaceHub.Tests.csproj -c Release --filter "FullyQualifiedName~HepsiburadaMetadataTests|FullyQualifiedName~HepsiburadaApiTests"`

Expected: PASS.

- [ ] **Step 5: Commit current metadata contracts**

```powershell
git add Hepsiburada/HepsiburadaApiClient.cs Hepsiburada/HepsiburadaContracts.cs tests/MarketplaceHub.Tests/HepsiburadaMetadataTests.cs
git commit -m "feat: add Hepsiburada metadata and Buybox reads"
```

### Task 4: Shop-scoped cache and deterministic product matching

**Files:**
- Create: `Hepsiburada/HepsiburadaWorkspaceModels.cs`
- Create: `Hepsiburada/HepsiburadaWorkspaceStore.cs`
- Create: `Hepsiburada/HepsiburadaMatching.cs`
- Create: `Hepsiburada/HepsiburadaWorkspaceService.cs`
- Test: `tests/MarketplaceHub.Tests/HepsiburadaWorkspaceTests.cs`

**Interfaces:**
- Consumes: read models from Tasks 2-3, `CatalogStore`, `ProductChannelBindingStore` and `MarketplaceConnection`.
- Produces: `HepsiburadaWorkspaceState`, `HepsiburadaProductMatch`, `RefreshProductsAsync`, `SuggestMatches` and account-scoped cached snapshots.

- [ ] **Step 1: Write failing account-isolation and matching-conflict tests**

```csharp
[TestMethod]
public void BarcodeWinsAndSkuCannotOverrideAContradictoryBarcode()
{
    var local = Product("p1", sku: "SKU-1", barcode: "8691");
    var rows = new[]
    {
        Remote("r1", sku: "OTHER", barcode: "8691"),
        Remote("r2", sku: "SKU-1", barcode: "8692")
    };
    var match = HepsiburadaMatching.Suggest(local, rows);
    Assert.AreEqual(ProductChannelMatchOutcome.Matched, match.Outcome);
    Assert.AreEqual("r1", match.RemoteId);
}

[TestMethod]
public void DuplicateRemoteBarcodeRequiresReview()
{
    var match = HepsiburadaMatching.Suggest(Product("p1", "SKU", "8691"),
        [Remote("r1", "A", "8691"), Remote("r2", "B", "8691")]);
    Assert.AreEqual(ProductChannelMatchOutcome.Conflict, match.Outcome);
}
```

Add tests proving Connection A cannot see Connection B's cached rows/mappings, blank identifiers remain unmatched, and manual review stores the chosen remote ID with the current snapshot revision.

- [ ] **Step 2: Run workspace tests and verify RED**

Run: `dotnet test tests\MarketplaceHub.Tests\MarketplaceHub.Tests.csproj -c Release --filter FullyQualifiedName~HepsiburadaWorkspaceTests`

Expected: FAIL because the workspace store and matcher do not exist.

- [ ] **Step 3: Implement atomic cache replacement and matching**

```csharp
public static HepsiburadaProductMatch Suggest(
    CatalogProduct product,
    IReadOnlyList<HepsiburadaMerchantProduct> remote)
{
    var barcode = product.Barcode.Trim();
    var byBarcode = barcode.Length == 0 ? [] : remote.Where(x => x.Barcode == barcode).ToArray();
    if (byBarcode.Length == 1) return HepsiburadaProductMatch.Matched(product.Id, byBarcode[0], "Barkod");
    if (byBarcode.Length > 1) return HepsiburadaProductMatch.Conflict(product.Id, "Aynı barkod birden fazla uzaktaki üründe var.");
    var bySku = remote.Where(x => x.MerchantSku == product.Sku).ToArray();
    if (bySku.Length == 1 && (bySku[0].Barcode.Length == 0 || barcode.Length == 0 || bySku[0].Barcode == barcode))
        return HepsiburadaProductMatch.Matched(product.Id, bySku[0], "Merchant SKU");
    return bySku.Length > 0
        ? HepsiburadaProductMatch.Conflict(product.Id, "SKU eşleşiyor ancak barkod çelişiyor.")
        : HepsiburadaProductMatch.NewCandidate(product.Id);
}
```

Store state in `catalog.db` with `ConnectionId` in every primary key. Replace one account's remote snapshot in a single transaction, increment its revision, and preserve other accounts. Use the shared binding preview/apply flow for selected central products.

- [ ] **Step 4: Run workspace and common binding tests**

Run: `dotnet test tests\MarketplaceHub.Tests\MarketplaceHub.Tests.csproj -c Release --filter "FullyQualifiedName~HepsiburadaWorkspaceTests|FullyQualifiedName~ProductChannelBindingTests|FullyQualifiedName~ProductChannelMatchingTests"`

Expected: PASS.

- [ ] **Step 5: Commit the read-only product workspace core**

```powershell
git add Hepsiburada/HepsiburadaWorkspaceModels.cs Hepsiburada/HepsiburadaWorkspaceStore.cs Hepsiburada/HepsiburadaMatching.cs Hepsiburada/HepsiburadaWorkspaceService.cs tests/MarketplaceHub.Tests/HepsiburadaWorkspaceTests.cs
git commit -m "feat: add Hepsiburada product matching workspace"
```

### Task 5: Multi-account connection UI and read-only workspace

**Files:**
- Modify: `HepsiburadaPanel.cs`
- Create: `HepsiburadaWorkspacePanel.cs`
- Create: `HepsiburadaWorkspacePanel.Control.cs`
- Create: `HepsiburadaWorkspacePanel.Settings.cs`
- Modify: `MarketplaceConnectionStore.cs`
- Modify: `MarketplaceAdapterRegistry.cs`
- Modify: `MarketplaceWorkspaceHost.cs`
- Modify: `MarketplaceConnectionsPanel.cs`
- Test: `tests/MarketplaceHub.Tests/HepsiburadaPanelTests.cs`
- Test: `tests/MarketplaceHub.Tests/MarketplaceAccountNavigationTests.cs`

**Interfaces:**
- Consumes: Tasks 1-4, common marketplace shell and `ProductCardRequested` event.
- Produces: operational Hepsiburada account setup, `AccountScopedMarketplaceAdapter` product reads and specialized workspace routing.

- [ ] **Step 1: Write failing UI/navigation and capability tests**

```csharp
[TestMethod]
public void ConnectedHepsiburadaAccountOpensSpecializedWorkspace()
{
    var connection = SaveConnectedHepsiburadaAccount(directory);
    using var host = MarketplaceWorkspaceHost.Create(connection.Id, directory, RegistryWithFakeHepsiburada());
    Assert.IsInstanceOfType(host.Workspace, typeof(HepsiburadaWorkspacePanel));
    Assert.IsNotNull(FindByName(host, "HepsiburadaProducts"));
    Assert.IsNotNull(FindByName(host, "HepsiburadaOpenProductCard"));
}

[TestMethod]
public void ServiceKeyIsNeverRenderedAfterSave()
{
    var panel = HepsiburadaPanel.CreateForConnection(connection.Id, directory);
    Assert.IsFalse(AllVisibleText(panel).Contains("test-secret", StringComparison.Ordinal));
}
```

Add tests for multiple Hepsiburada account cards, account switch isolation, Turkish labels, disabled write controls before read verification and product-card event forwarding.

- [ ] **Step 2: Run UI tests and verify RED**

Run: `dotnet test tests\MarketplaceHub.Tests\MarketplaceHub.Tests.csproj -c Release --filter "FullyQualifiedName~HepsiburadaPanelTests|FullyQualifiedName~MarketplaceAccountNavigationTests"`

Expected: FAIL because Hepsiburada still routes to the generic shell.

- [ ] **Step 3: Implement simple connection form and read-only account workspace**

```csharp
UIElement CreateWorkspace(MarketplaceConnection connection) => connection.Channel switch
{
    "trendyol" => new TrendyolWorkspacePanel(connection.Id, directory),
    "etsy" => new EtsyWorkspacePanel(connection.Id, directory),
    "hepsiburada" => new HepsiburadaWorkspacePanel(connection.Id, directory),
    _ => CreateUnsupportedWorkspace(connection)
};
```

Register Hepsiburada initially with `ProductsRead` and `ProductManagement` only, set `LiveApiBlocked` false after the read implementation exists, and extend the account adapter's `ReadProductsAsync`. The form contains Merchant ID, replacement-only service key, environment, optional User-Agent override, save and read-only test. Successful testing records `CONNECTED_READ_ONLY`; authentication/network failures are redacted and preserve credentials.

- [ ] **Step 4: Run Hepsiburada UI, navigation and multi-account tests**

Run: `dotnet test tests\MarketplaceHub.Tests\MarketplaceHub.Tests.csproj -c Release --filter "FullyQualifiedName~HepsiburadaPanelTests|FullyQualifiedName~MarketplaceAccountNavigationTests|FullyQualifiedName~MultiAccountConnectionTests"`

Expected: PASS; Trendyol and Etsy account workspaces still open.

- [ ] **Step 5: Commit the first working phase**

```powershell
git add HepsiburadaPanel.cs HepsiburadaWorkspacePanel.cs HepsiburadaWorkspacePanel.Control.cs HepsiburadaWorkspacePanel.Settings.cs MarketplaceConnectionStore.cs MarketplaceAdapterRegistry.cs MarketplaceWorkspaceHost.cs MarketplaceConnectionsPanel.cs tests/MarketplaceHub.Tests/HepsiburadaPanelTests.cs tests/MarketplaceHub.Tests/MarketplaceAccountNavigationTests.cs
git commit -m "feat: add Hepsiburada account workspace"
```

### Task 6: Immutable catalog and listing previews with durable receipts

**Files:**
- Modify: `Hepsiburada/HepsiburadaApiClient.cs`
- Modify: `Hepsiburada/HepsiburadaWorkspaceModels.cs`
- Modify: `Hepsiburada/HepsiburadaWorkspaceStore.cs`
- Modify: `Hepsiburada/HepsiburadaWorkspaceService.cs`
- Create: `Hepsiburada/HepsiburadaDispatch.cs`
- Test: `tests/MarketplaceHub.Tests/HepsiburadaDispatchTests.cs`

**Interfaces:**
- Consumes: cached metadata/matches and common product bindings.
- Produces: `HepsiburadaOperation`, `HepsiburadaPlan`, `HepsiburadaReceipt`, `CreatePreview`, `Claim`, `SendAsync` and `RefreshResultAsync`.

- [ ] **Step 1: Write failing payload-isolation, stale-plan and uncertain-write tests**

```csharp
[TestMethod]
public void StockPreviewContainsNoPriceOrContentFields()
{
    var plan = service.CreatePreview(connection.Id, [product.Id], HepsiburadaOperation.Stock);
    using var json = JsonDocument.Parse(plan.PayloadJson);
    var row = json.RootElement.GetProperty("listings")[0];
    Assert.IsTrue(row.TryGetProperty("availableStock", out _));
    Assert.IsFalse(row.TryGetProperty("price", out _));
    Assert.IsFalse(row.TryGetProperty("title", out _));
}

[TestMethod]
public async Task NetworkLossAfterPostCreatesUncertainReceiptAndBlocksReplay()
{
    var plan = ValidListingPlan();
    await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => dispatch.SendAsync(plan.Id, approved: true));
    Assert.AreEqual("Belirsiz", store.Receipts(connection.Id).Single().Status);
    await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => dispatch.SendAsync(plan.Id, approved: true));
}
```

Also test missing mandatory attributes, non-publishable category, unknown attribute values, wrong account/environment, expired preview, changed catalog/binding revision, duplicate payload, blank barcode and explicit approval false.

- [ ] **Step 2: Run dispatch tests and verify RED**

Run: `dotnet test tests\MarketplaceHub.Tests\MarketplaceHub.Tests.csproj -c Release --filter FullyQualifiedName~HepsiburadaDispatchTests`

Expected: FAIL because plans and dispatch do not exist.

- [ ] **Step 3: Implement exact operation plans and guarded dispatch**

```csharp
public enum HepsiburadaOperation
{
    CatalogCreate,
    CatalogUpdate,
    ListingCreate,
    Price,
    Stock,
    PriceAndStock,
    DispatchTime,
    SaleState
}

public sealed record HepsiburadaPlan(
    string Id, string ConnectionId, string MerchantId, HepsiburadaEnvironment Environment,
    HepsiburadaOperation Operation, DateTime CreatedUtc, long ConnectionRevision,
    string CatalogHash, string BindingHash, string WorkspaceHash,
    IReadOnlyList<HepsiburadaPlanRow> Rows, string PayloadJson);
```

Validate active leaf categories and current mandatory attributes while constructing a preview. `Claim` runs in one SQLite transaction, rejects stale/wrong/duplicate/uncertain plans and inserts a `Sonuç bekleniyor` receipt before HTTP begins. A transport failure updates it to `Belirsiz`; it never retries automatically. Successful asynchronous tracking IDs remain `Kuyrukta` until status reconciliation proves row results.

- [ ] **Step 4: Run dispatch, workspace and credential tests**

Run: `dotnet test tests\MarketplaceHub.Tests\MarketplaceHub.Tests.csproj -c Release --filter "FullyQualifiedName~HepsiburadaDispatchTests|FullyQualifiedName~HepsiburadaWorkspaceTests|FullyQualifiedName~HepsiburadaCredentialTests"`

Expected: PASS.

- [ ] **Step 5: Commit safe write orchestration**

```powershell
git add Hepsiburada/HepsiburadaApiClient.cs Hepsiburada/HepsiburadaWorkspaceModels.cs Hepsiburada/HepsiburadaWorkspaceStore.cs Hepsiburada/HepsiburadaWorkspaceService.cs Hepsiburada/HepsiburadaDispatch.cs tests/MarketplaceHub.Tests/HepsiburadaDispatchTests.cs
git commit -m "feat: add reviewed Hepsiburada listing dispatch"
```

### Task 7: Listing controls and Buybox competition workspace

**Files:**
- Create: `Hepsiburada/HepsiburadaCompetition.cs`
- Create: `HepsiburadaWorkspacePanel.Mappings.cs`
- Create: `HepsiburadaWorkspacePanel.Operations.cs`
- Create: `HepsiburadaWorkspacePanel.Competition.cs`
- Modify: `HepsiburadaWorkspacePanel.cs`
- Modify: `MarketplaceConnectionStore.cs`
- Test: `tests/MarketplaceHub.Tests/HepsiburadaCompetitionTests.cs`
- Test: `tests/MarketplaceHub.Tests/HepsiburadaPanelTests.cs`

**Interfaces:**
- Consumes: Tasks 3 and 6.
- Produces: `HepsiburadaCompetition.Suggest`, category/attribute editors, preview/approval dialogs and competition grid.

- [ ] **Step 1: Write failing minimum-price and UI command tests**

```csharp
[TestMethod]
public void BuyboxSuggestionNeverDropsBelowProtectedMinimum()
{
    var result = HepsiburadaCompetition.Suggest(
        new HepsiburadaBuybox("HB1", "SKU1", 2, 90m, 100m, DateTime.UtcNow),
        minimum: 95m, maximumDecrease: 20m, undercut: 1m);
    Assert.AreEqual(95m, result.Price);
    Assert.IsFalse(result.AutomaticallyApplied);
}

[TestMethod]
public void WriteButtonsCreatePreviewsAndDoNotSendDirectly()
{
    var panel = ConnectedPanelWithOneSelectedRow();
    Click(panel, "HepsiburadaPricePreview");
    Assert.AreEqual(1, fakePlans.Created.Count);
    Assert.AreEqual(0, fakeTransport.PostCount);
}
```

Test invalid negative/over-precision bounds, missing competitor price, rank one/second-price logic, multi-selection, category property validation, disabled management switches and Turkish operation labels.

- [ ] **Step 2: Run competition/panel tests and verify RED**

Run: `dotnet test tests\MarketplaceHub.Tests\MarketplaceHub.Tests.csproj -c Release --filter "FullyQualifiedName~HepsiburadaCompetitionTests|FullyQualifiedName~HepsiburadaPanelTests"`

Expected: FAIL because the competition service and write UI are absent.

- [ ] **Step 3: Implement protected suggestions and preview-only controls**

```csharp
public static HepsiburadaPriceSuggestion Suggest(
    HepsiburadaBuybox row, decimal minimum, decimal maximumDecrease, decimal undercut)
{
    if (minimum <= 0 || maximumDecrease < 0 || undercut < 0)
        return new(null, false, "Fiyat sınırları geçersiz.");
    if (row.WinningPrice is null or <= 0 || row.OwnPrice is null or <= 0)
        return new(null, false, "Buybox fiyatı eksik.");
    var floor = Math.Max(minimum, row.OwnPrice.Value - maximumDecrease);
    var proposed = decimal.Round(Math.Max(floor, row.WinningPrice.Value - undercut), 2, MidpointRounding.AwayFromZero);
    return new(proposed, false, "Korunan Buybox fiyat önerisi; otomatik gönderilmez.");
}
```

Add workspace tabs **Hepsiburada kontrol**, **Ayarlar**, **Rekabet analizi**, **İşlem geçmişi**. Show account-scoped products and category/property mapping. Every write button first opens a row-level preview; only the preview dialog exposes the explicit send action. Enable the remaining capabilities only after their implementation tests pass: content/category/properties/listing/price/stock/delivery.

- [ ] **Step 4: Run Hepsiburada UI, dispatch and common shop-workspace tests**

Run: `dotnet test tests\MarketplaceHub.Tests\MarketplaceHub.Tests.csproj -c Release --filter "FullyQualifiedName~Hepsiburada|FullyQualifiedName~MarketplaceShopWorkspaceTests"`

Expected: PASS with zero fake POSTs before approval.

- [ ] **Step 5: Commit listing and competition UI**

```powershell
git add Hepsiburada/HepsiburadaCompetition.cs HepsiburadaWorkspacePanel.cs HepsiburadaWorkspacePanel.Mappings.cs HepsiburadaWorkspacePanel.Operations.cs HepsiburadaWorkspacePanel.Competition.cs MarketplaceConnectionStore.cs tests/MarketplaceHub.Tests/HepsiburadaCompetitionTests.cs tests/MarketplaceHub.Tests/HepsiburadaPanelTests.cs
git commit -m "feat: add Hepsiburada listing and Buybox workspace"
```

### Task 8: Unified Hepsiburada order reads and idempotent stock

**Files:**
- Modify: `Hepsiburada/HepsiburadaApiClient.cs`
- Create: `Hepsiburada/HepsiburadaOrderReader.cs`
- Modify: `MarketplaceAdapterRegistry.cs`
- Modify: `MarketplaceConnectionStore.cs`
- Test: `tests/MarketplaceHub.Tests/HepsiburadaOrderTests.cs`
- Test: `tests/MarketplaceHub.Tests/MultiStoreOrderSyncTests.cs`

**Interfaces:**
- Consumes: `OrderSnapshot`, `OrderItem`, `MarketplaceOrderSyncService`, Hepsiburada credentials and account adapter.
- Produces: `GetOrdersAsync(DateTime fromUtc, CancellationToken)`, `HepsiburadaOrderReader.ReadAsync` and Hepsiburada `OrdersRead` capability.

- [ ] **Step 1: Write failing paging, projection and duplicate-stock tests**

```csharp
[TestMethod]
public async Task RepeatedOrderAcrossPagesAndRunsReducesStockOnce()
{
    var adapter = HepsiburadaAdapterReturningSameOrderOnTwoPages();
    var service = CreateOrderSync(adapter, productStock: 10);
    await service.SyncAsync(connection.Id, DateTime.UnixEpoch);
    await service.SyncAsync(connection.Id, DateTime.UnixEpoch);
    Assert.AreEqual(8, catalog.Get(product.Id)!.Stock);
    Assert.AreEqual(1, orders.List().Count(x => x.Marketplace == "hepsiburada"));
}

[TestMethod]
public async Task OrderProjectionRetainsConnectionMerchantAndRemoteProductIdentity()
{
    var order = (await reader.ReadAsync(credentials, connection.Id, DateTime.UnixEpoch)).Single();
    Assert.AreEqual(connection.Id, order.ConnectionId);
    Assert.AreEqual(credentials.MerchantId, order.ShopId);
    Assert.AreEqual("HB1", order.Items.Single().ProductId);
}
```

Add tests for paid/new status filtering, unknown currency, invalid quantity, cancellation, next-page loops, malformed addresses, connection isolation and redacted API failures.

- [ ] **Step 2: Run order tests and verify RED**

Run: `dotnet test tests\MarketplaceHub.Tests\MarketplaceHub.Tests.csproj -c Release --filter "FullyQualifiedName~HepsiburadaOrderTests|FullyQualifiedName~MultiStoreOrderSyncTests"`

Expected: FAIL because the Hepsiburada account adapter has no order reader.

- [ ] **Step 3: Implement bounded order reads and shared projection**

```csharp
public async Task<IReadOnlyList<OrderSnapshot>> ReadAsync(
    HepsiburadaCredentials credentials,
    string connectionId,
    DateTime fromUtc,
    CancellationToken cancellationToken = default)
{
    var remote = await client.GetOrdersAsync(fromUtc, cancellationToken);
    return remote.Select(order => new OrderSnapshot
    {
        Marketplace = "hepsiburada",
        ShopId = credentials.MerchantId,
        ConnectionId = connectionId,
        OrderId = order.OrderNumber,
        SourceUpdatedAt = order.UpdatedUtc,
        Items = order.Lines.Select(line => new OrderItem
        {
            Sku = line.MerchantSku,
            Barcode = line.Barcode,
            ProductId = line.HepsiburadaSku,
            Title = line.Title,
            Quantity = line.Quantity
        }).ToList()
    }).ToArray();
}
```

Use the existing unified store's idempotency/stock transaction instead of implementing a second ledger. Register `OrdersRead` only when this reader is active. Display returned package/cargo/desi values as read-only fields; do not add package mutations.

- [ ] **Step 4: Run order and stock regression tests**

Run: `dotnet test tests\MarketplaceHub.Tests\MarketplaceHub.Tests.csproj -c Release --filter "FullyQualifiedName~HepsiburadaOrderTests|FullyQualifiedName~MultiStoreOrderSyncTests|FullyQualifiedName~OrderStock"`

Expected: PASS with a single stock movement for repeated data.

- [ ] **Step 5: Commit unified order integration**

```powershell
git add Hepsiburada/HepsiburadaApiClient.cs Hepsiburada/HepsiburadaOrderReader.cs MarketplaceAdapterRegistry.cs MarketplaceConnectionStore.cs tests/MarketplaceHub.Tests/HepsiburadaOrderTests.cs tests/MarketplaceHub.Tests/MultiStoreOrderSyncTests.cs
git commit -m "feat: add Hepsiburada order synchronization"
```

### Task 9: Encrypted live read verification, regression and release

**Files:**
- Modify: `IMPLEMENTATION_STATUS.md`
- Modify: `TODO.md`
- Modify: `docs/pazaryeri-api-durumu.md`
- Modify: `installer/Build-Package.ps1` only if the existing manifest list requires new runtime files

**Interfaces:**
- Consumes: all previous tasks and the user-supplied Merchant ID/service key at runtime.
- Produces: verified encrypted local account, release evidence, self-contained application and ZIP package.

- [ ] **Step 1: Run the complete Release regression suite**

Run: `dotnet test tests\MarketplaceHub.Tests\MarketplaceHub.Tests.csproj -c Release`

Expected: PASS with zero failed/skipped tests unless an explicitly documented platform-only test already existed.

- [ ] **Step 2: Inspect the complete branch for secrets and unsafe live paths**

```powershell
git diff --check
rg -n -S "ServiceKey\s*=|MerchantId\s*=" . -g "!bin/**" -g "!obj/**" -g "!Windows-Excel-Final/**" -g "!*.zip"
git status --short
```

Expected: `git diff --check` succeeds; the credential search returns no tracked source/document/test occurrence; only intended files are modified.

- [ ] **Step 3: Save the supplied credentials through the running local form and run one read-only connection test**

Use the Hepsiburada account form to create/update the supplied shop. Confirm the UI reports `Salt okunur bağlı`, then refresh merchant products and listings. Do not invoke catalog/listing/price/stock POST operations. Record only redacted counts/status in `IMPLEMENTATION_STATUS.md`.

- [ ] **Step 4: Run focused tests again if live-read findings required a code correction**

Run after any correction: `dotnet test tests\MarketplaceHub.Tests\MarketplaceHub.Tests.csproj -c Release --filter FullyQualifiedName~Hepsiburada`

Expected: PASS. If production code changed, repeat Step 1 before release.

- [ ] **Step 5: Publish, package and visibly open the application**

```powershell
dotnet publish TrMarketplaceHubDesktop.csproj -c Release -r win-x64 --self-contained true -o Windows-Excel-Final
powershell -NoProfile -ExecutionPolicy Bypass -File installer\Build-Package.ps1 -OutputDirectory Windows-Excel-Final -PackagePath MonoBridge-Windows-Excel-Final.zip
Start-Process .\Windows-Excel-Final\TrMarketplaceHubDesktop.exe
```

Expected: package smoke passes, `MonoBridge — Yönetim Merkezi` is responsive, and Hepsiburada account/products/settings/competition/history pages open for the exact selected account.

- [ ] **Step 6: Update status documents with precise implementation/live verification state**

```markdown
- Hepsiburada connection: implemented; production read verified for the configured merchant.
- Product/listing/Buybox/order contracts: implemented and covered by fake-HTTP tests.
- Live writes: available only through reviewed previews; no product write was used for connection verification.
- Deferred: campaign, package mutation, label, invoice, settlement and claim writes.
```

- [ ] **Step 7: Commit and push the release state**

```powershell
git add IMPLEMENTATION_STATUS.md TODO.md docs/pazaryeri-api-durumu.md installer/Build-Package.ps1
git commit -m "docs: record Hepsiburada release status"
git push origin codex/multi-store-product-management
```

Expected: the remote branch contains all reviewed commits, the working tree is clean, and the final report separates automated tests, live reads and live writes.
