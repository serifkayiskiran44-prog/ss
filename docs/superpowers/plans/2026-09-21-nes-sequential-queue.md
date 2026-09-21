# NES Sequential Invoice Queue Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Kullanıcının seçip onayladığı siparişleri tek tek NES'e gönderen, gerçek fatura numarasını bağlayan, PDF'i indiren ve istenirse Trendyol paketine yükleyen kalıcı ve tekrar çalıştırmaya dayanıklı bir kuyruk oluşturmak.

**Architecture:** Kuyruk ve adım durumu ayrı bir SQLite deposunda tutulur. Saf bir planlayıcı seçili siparişleri doğrular; tek lease sahibi olan işlemci her çağrıda yalnız bir dış adımı yürütür ve sonucu kalıcılaştırır. WPF ekranı yalnız önizleme, onay, izleme ve kontrollü yeniden deneme yapar; NES/Trendyol HTTP ayrıntıları mevcut istemcileri kullanan bir adaptörde kalır.

**Tech Stack:** .NET 8 WPF, C# 12, Microsoft.Data.Sqlite 8.0.7, System.Net.Http, MSTest 3.6.1

**Spec:** `docs/superpowers/specs/2026-09-21-marketplace-useful-feature-adaptation-design.md`

## Global Constraints

- Aras Kargo entegrasyonu kapsam dışıdır.
- Yalnız kullanıcı tarafından seçilen ve somut önizleme üzerinden onaylanan siparişler kuyruğa eklenir.
- Aynı pazaryeri, mağaza ve sipariş için ikinci NES faturası gönderilemez.
- Sonucu belirsiz gönderim otomatik yeniden gönderilemez; önce uzaktan mutabakat gerekir.
- Bir sipariş tamamlanmadan, bloke olmadan veya tekrar bekleme durumuna geçmeden sonraki sipariş başlamaz.
- Trendyol PDF yükleme hatası yeni fatura oluşturmaz; yalnız yükleme adımı tekrar edilir.
- API anahtarları, parola ve token değerleri SQLite kuyruğuna, işlem geçmişine veya hata metnine yazılmaz.
- Var olan sipariş ve NES tablolarının kimlikleri değiştirilmez.
- Her üretim değişikliği önce başarısız testle kanıtlanır.

---

### Task 1: Durable Queue Store and Lease Semantics

**Files:**
- Create: `NesInvoiceQueue.cs`
- Create: `tests/MarketplaceHub.Tests/TestTemporaryDirectory.cs`
- Create: `tests/MarketplaceHub.Tests/NesInvoiceQueueStoreTests.cs`

**Interfaces:**
- Produces: `NesQueueState`, `NesQueueStep`, `NesQueueRequest`, `NesQueueItem`
- Produces: `NesInvoiceQueueStore.Enqueue`, `List`, `TryClaimNext`, `Advance`, `ScheduleRetry`, `Block`, `MarkUnknown`, `Complete`, `Cancel`
- Produces: test-only `TemporaryDirectory`
- Consumes: `MarketplaceConnectionStore.Redact` for persisted error text

- [ ] **Step 1: Write the failing store tests**

```csharp
[TestMethod]
public void DuplicateOrderIsEnqueuedOnlyOnce()
{
    using var temp = new TemporaryDirectory();
    var store = new NesInvoiceQueueStore(temp.Path);
    var request = new NesQueueRequest("Trendyol", "411150", "11600461863",
        "4154085881", NesDocumentKind.EArchive, false, "", "order-hash");

    var first = store.Enqueue(request, DateTimeOffset.Parse("2026-09-21T10:00:00Z"));
    var second = store.Enqueue(request, DateTimeOffset.Parse("2026-09-21T10:01:00Z"));

    Assert.AreEqual(first.Id, second.Id);
    Assert.AreEqual(1, store.List().Count);
}

[TestMethod]
public void OnlyOneWorkerCanClaimTheNextOrder()
{
    using var temp = new TemporaryDirectory();
    var store = new NesInvoiceQueueStore(temp.Path);
    var item = store.Enqueue(new("Trendyol", "411150", "1", "10",
        NesDocumentKind.EArchive, false, "", "hash"), DateTimeOffset.UtcNow);

    Assert.IsTrue(store.TryClaimNext(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2), out var claimed));
    Assert.IsFalse(store.TryClaimNext(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2), out _));
    Assert.AreEqual(item.Id, claimed!.Id);
    Assert.IsFalse(string.IsNullOrWhiteSpace(claimed.LeaseToken));
}

[TestMethod]
public void WrongLeaseCannotAdvanceAQueueItem()
{
    using var temp = new TemporaryDirectory();
    var store = new NesInvoiceQueueStore(temp.Path);
    store.Enqueue(new("Trendyol", "411150", "1", "10", NesDocumentKind.EArchive,
        false, "", "hash"), DateTimeOffset.UtcNow);
    store.TryClaimNext(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2), out var claimed);

    Assert.ThrowsException<InvalidOperationException>(() =>
        store.Advance(claimed!.Id, "wrong-token", NesQueueStep.SendInvoice,
            NesQueueState.Ready, DateTimeOffset.UtcNow));
}
```

- [ ] **Step 2: Run the targeted tests and verify RED**

Run: `dotnet test tests/MarketplaceHub.Tests/MarketplaceHub.Tests.csproj -c Release --filter NesInvoiceQueueStoreTests`

Expected: compilation fails because `NesInvoiceQueueStore` and queue contracts do not exist.

- [ ] **Step 3: Implement the minimal queue contracts and SQLite store**

Create these public contracts in `NesInvoiceQueue.cs`:

```csharp
public enum NesQueueState { Planned, Ready, Running, RetryWaiting, Blocked, Unknown, Completed, Cancelled }
public enum NesQueueStep { PrepareDocument, SendInvoice, AwaitNumber, DownloadPdf, UploadMarketplace, Finish }

public sealed record NesQueueRequest(
    string Marketplace, string ShopId, string OrderId, string PackageId,
    NesDocumentKind Kind, bool UploadMarketplace, string ReceiverAlias,
    string OrderFingerprint);

public sealed class NesQueueItem
{
    public string Id { get; set; } = "";
    public string Marketplace { get; set; } = "";
    public string ShopId { get; set; } = "";
    public string OrderId { get; set; } = "";
    public string PackageId { get; set; } = "";
    public NesDocumentKind Kind { get; set; }
    public bool UploadMarketplace { get; set; }
    public string ReceiverAlias { get; set; } = "";
    public string OrderFingerprint { get; set; } = "";
    public NesQueueState State { get; set; }
    public NesQueueStep Step { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? NextAttemptUtc { get; set; }
    public string LeaseToken { get; set; } = "";
    public DateTimeOffset? LeaseUntilUtc { get; set; }
    public string NesUuid { get; set; } = "";
    public string InvoiceNumber { get; set; } = "";
    public string PdfPath { get; set; } = "";
    public string PdfSha256 { get; set; } = "";
    public string LastError { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}
```

Use a unique index on `(marketplace, shop_id, order_id)` and `BEGIN IMMEDIATE`/conditional `UPDATE` semantics for claiming. `Advance` and every terminal transition must include `WHERE id=$id AND lease_token=$token`; zero changed rows throws `InvalidOperationException`. Persist `MarketplaceConnectionStore.Redact(error)` only.

Create the shared test-only directory owner as:

```csharp
internal sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "marketplace-tests-" + Guid.NewGuid().ToString("N"));
    public TemporaryDirectory() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}
```

- [ ] **Step 4: Run targeted tests and verify GREEN**

Run: `dotnet test tests/MarketplaceHub.Tests/MarketplaceHub.Tests.csproj -c Release --filter NesInvoiceQueueStoreTests`

Expected: all queue store tests pass.

- [ ] **Step 5: Add corruption and retry boundary tests**

Add literal-behavior tests proving malformed enum/timestamp rows are skipped and reported by `CorruptRows()`, expired leases are claimable, active leases are not, and retry delay is capped at 24 hours. Run the same filtered test command and verify RED before adding each missing behavior, then GREEN.

- [ ] **Step 6: Commit only the task files**

```powershell
git add -- NesInvoiceQueue.cs tests/MarketplaceHub.Tests/TestTemporaryDirectory.cs tests/MarketplaceHub.Tests/NesInvoiceQueueStoreTests.cs
git commit -m "feat: add durable NES invoice queue"
```

---

### Task 2: Selection Planner, Fingerprints, and Receiver Aliases

**Files:**
- Create: `NesQueuePlanner.cs`
- Create: `NesRecipientAliasStore.cs`
- Create: `tests/MarketplaceHub.Tests/NesQueuePlannerTests.cs`
- Modify: `OrdersStore.cs`

**Interfaces:**
- Consumes: `OrderInvoiceRules.ValidateCanInvoice`, `OrderInvoiceRules.DefaultKind`, `NesReceiverIdentityResolver.Resolve`, `NesSettingsValidator.Validate`
- Produces: `NesQueuePlanRow`, `NesQueuePlan`, `NesQueuePlanner.Build`
- Produces: `NesRecipientAliasStore.Get`, `Save`
- Produces: `OrdersStore.Find(string marketplace, string shopId, string orderId)`

- [ ] **Step 1: Write failing planner tests**

```csharp
[TestMethod]
public void PlannerBlocksEInvoiceWithoutReceiverAlias()
{
    var order = ValidOrder();
    order.EInvoiceAvailable = true;
    order.TaxNumber = "1234567890";

    var plan = NesQueuePlanner.Build([order], Settings(), _ => null, uploadMarketplace: true);

    Assert.AreEqual("Blocked", plan.Rows.Single().Decision);
    StringAssert.Contains(plan.Rows.Single().Reason, "alıcı etiketi");
}

[TestMethod]
public void PlannerUsesSavedAliasAndProducesStableFingerprint()
{
    var order = ValidOrder();
    order.EInvoiceAvailable = true;
    order.TaxNumber = "1234567890";

    var first = NesQueuePlanner.Build([order], Settings(), _ => "urn:mail:pk@buyer.example", true);
    var second = NesQueuePlanner.Build([order.Copy()], Settings(), _ => "urn:mail:pk@buyer.example", true);

    Assert.AreEqual("Ready", first.Rows.Single().Decision);
    Assert.AreEqual(first.Rows.Single().Request!.OrderFingerprint,
        second.Rows.Single().Request!.OrderFingerprint);
}

[TestMethod]
public void PlannerNeverQueuesAnAlreadyInvoicedOrderForAnotherInvoice()
{
    var order = ValidOrder();
    order.InvoiceUuid = "11111111-1111-1111-1111-111111111111";
    order.InvoiceNumber = "EAR2026000000001";

    var plan = NesQueuePlanner.Build([order], Settings(), _ => null, true);

    Assert.IsNull(plan.Rows.Single().Request);
    Assert.AreEqual("AlreadyInvoiced", plan.Rows.Single().Decision);
}
```

- [ ] **Step 2: Run planner tests and verify RED**

Run: `dotnet test tests/MarketplaceHub.Tests/MarketplaceHub.Tests.csproj -c Release --filter NesQueuePlannerTests`

Expected: compilation fails because planner and alias store are absent.

- [ ] **Step 3: Implement the planner and alias store**

Use these contracts:

```csharp
public sealed record NesQueuePlanRow(
    string OrderId, string Decision, string Reason, NesQueueRequest? Request);

public sealed record NesQueuePlan(IReadOnlyList<NesQueuePlanRow> Rows)
{
    public int ReadyCount => Rows.Count(x => x.Decision == "Ready");
    public int BlockedCount => Rows.Count(x => x.Decision == "Blocked");
}

public static class NesQueuePlanner
{
    public static NesQueuePlan Build(
        IEnumerable<OrderSnapshot> orders,
        NesSettings settings,
        Func<string, string?> receiverAliasByTaxNumber,
        bool uploadMarketplace);
}
```

The fingerprint must be SHA-256 over a deterministic JSON projection containing marketplace, shop, order, document kind, receiver identity, receiver alias, currency, billing identity/address, item SKU/title/quantity/net/VAT/discount, and seller company identity. Do not serialize the API key. E-Archive uses an empty receiver alias. E-Invoice requires a saved alias. Trendyol upload requires a positive numeric package ID.

`NesRecipientAliasStore` stores `(tax_number, alias, updated_utc)` in its own SQLite file and validates that tax number is 10 or 11 digits and alias begins with `urn:`. It never discovers aliases from undocumented endpoints.

Add `OrdersStore.Find` using the existing corruption-safe reader; return `null` when the identity is absent and throw `OrderRowCorruptException` when the matching row is corrupt.

- [ ] **Step 4: Run planner tests and verify GREEN**

Run: `dotnet test tests/MarketplaceHub.Tests/MarketplaceHub.Tests.csproj -c Release --filter "NesQueuePlannerTests|OrderFilterTests"`

Expected: planner tests and affected order store tests pass.

- [ ] **Step 5: Commit only the task files**

```powershell
git add -- NesQueuePlanner.cs NesRecipientAliasStore.cs OrdersStore.cs tests/MarketplaceHub.Tests/NesQueuePlannerTests.cs
git commit -m "feat: plan safe NES batch invoices"
```

---

### Task 3: Step Processor and External Operation Boundary

**Files:**
- Create: `NesQueueOperations.cs`
- Create: `NesQueueProcessor.cs`
- Create: `tests/MarketplaceHub.Tests/NesQueueProcessorTests.cs`
- Modify: `NesIntegration.cs`

**Interfaces:**
- Consumes: queue store and planner contracts from Tasks 1–2
- Consumes: `NesDocumentStore`, `NesConnection`, `NesOrderReconciler`, `TrendyolInvoiceClient`, `OrdersStore`, `AuditStore`
- Produces: `INesQueueOperations`, `NesPreparedInvoice`, `NesMatchedInvoice`, `NesDownloadedInvoice`, `NesQueueProcessor.ProcessNextAsync`
- Produces: `NesUnknownResultException`, `NesQueueValidationException`, `NesQueueHttpException`

- [ ] **Step 1: Write failing processor tests with a narrow fake external boundary**

```csharp
[TestMethod]
public async Task ProcessorCompletesStepsInOrderAndThenClaimsSecondOrder()
{
    using var temp = new TemporaryDirectory();
    var queue = SeedTwoReadyOrders(temp.Path);
    var operations = new RecordingOperations();
    var processor = new NesQueueProcessor(queue, new OrdersStore(temp.Path),
        new AuditStore(temp.Path), operations, TimeProvider.System);

    while (await processor.ProcessNextAsync(CancellationToken.None)) { }

    CollectionAssert.AreEqual(
        new[] { "1:prepare", "1:send", "1:match", "1:download", "1:upload",
                "2:prepare", "2:send", "2:match", "2:download", "2:upload" },
        operations.Calls);
    Assert.IsTrue(queue.List().All(x => x.State == NesQueueState.Completed));
}

[TestMethod]
public async Task UnknownSendResultIsNeverAutomaticallySentAgain()
{
    using var temp = new TemporaryDirectory();
    var queue = SeedOneReadyOrder(temp.Path);
    var operations = new RecordingOperations { ThrowUnknownOnSend = true };
    var processor = Processor(temp.Path, queue, operations);

    Assert.IsTrue(await processor.ProcessNextAsync(CancellationToken.None));
    Assert.AreEqual(NesQueueState.Unknown, queue.List().Single().State);
    Assert.IsFalse(await processor.ProcessNextAsync(CancellationToken.None));
    Assert.AreEqual(1, operations.SendCount);
}

[TestMethod]
public async Task MarketplaceUploadFailureRetriesUploadWithoutSendingAnotherInvoice()
{
    using var temp = new TemporaryDirectory();
    var queue = SeedOneReadyOrder(temp.Path);
    var operations = new RecordingOperations { UploadFailuresRemaining = 1 };
    var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-21T10:00:00Z"));
    var processor = Processor(temp.Path, queue, operations, clock);

    await DrainUntilRetry(processor, queue);
    clock.Advance(TimeSpan.FromMinutes(2));
    await Drain(processor);

    Assert.AreEqual(1, operations.SendCount);
    Assert.AreEqual(2, operations.UploadCount);
    Assert.AreEqual(NesQueueState.Completed, queue.List().Single().State);
}
```

`ManualTimeProvider` lives in the test file, overrides `GetUtcNow`, and advances only its private current time. The test fake must return complete literal results and assert observable queue/order state. Do not assert that a mock method merely exists.

Define the test clock and fake boundary in the same test file. `RecordingOperations` appends `$"{item.OrderId}:prepare"`, `$"{item.OrderId}:send"`, `$"{item.OrderId}:match"`, `$"{item.OrderId}:download"`, and `$"{item.OrderId}:upload"` in the corresponding methods; it returns literal UUID `11111111-1111-1111-1111-111111111111`, number `EAR2026000000001`, and bytes `%PDF-test`. Its send method increments `SendCount` before optionally throwing `NesUnknownResultException`; its upload method increments `UploadCount` and throws `NesQueueHttpException(503)` while `UploadFailuresRemaining` is positive. `SeedOneReadyOrder` and `SeedTwoReadyOrders` must use the real `NesInvoiceQueueStore.Enqueue`; `Drain` repeatedly calls `ProcessNextAsync` until it returns false. These helpers live only in the test project.

- [ ] **Step 2: Run processor tests and verify RED**

Run: `dotnet test tests/MarketplaceHub.Tests/MarketplaceHub.Tests.csproj -c Release --filter NesQueueProcessorTests`

Expected: compilation fails because processor and operation boundary are absent.

- [ ] **Step 3: Implement the operation boundary and one-step processor**

```csharp
public interface INesQueueOperations
{
    Task<NesPreparedInvoice> PrepareAsync(NesQueueItem item, OrderSnapshot order, CancellationToken token);
    Task<NesUploadResult> SendAsync(NesQueueItem item, OrderSnapshot order, NesPreparedInvoice prepared, CancellationToken token);
    Task<NesMatchedInvoice?> FindRemoteAsync(NesQueueItem item, CancellationToken token);
    Task<NesDownloadedInvoice> DownloadAsync(NesQueueItem item, CancellationToken token);
    Task UploadMarketplaceAsync(NesQueueItem item, NesDownloadedInvoice pdf, CancellationToken token);
}

public sealed record NesPreparedInvoice(string DraftId, byte[] Xml, string LocalDocumentNumber);
public sealed record NesMatchedInvoice(string Uuid, string Number, string Status, DateTimeOffset? IssuedAt);
public sealed record NesDownloadedInvoice(string Path, string Sha256, byte[] Bytes);
public sealed class NesUnknownResultException(string message) : Exception(message);
public sealed class NesQueueValidationException(string message) : Exception(message);
public sealed class NesQueueHttpException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
```

`ProcessNextAsync` claims one item, executes exactly one step, persists the outcome, releases the lease, and returns `true`. The caller loops to continue. This keeps every network boundary recoverable after process termination.

Map errors as follows:

- `NesUnknownResultException` → `Unknown`
- `NesQueueValidationException`, HTTP 400/401/403/404/409/422 → `Blocked`
- HTTP 429/500/502/503/504, `HttpRequestException`, timeout → `RetryWaiting`
- cancellation requested by caller → release to `Ready` at the same step without increasing attempts

Add typed HTTP status information to errors from `NesConnection` without including response bodies or secrets. Keep current public error messages compatible.

- [ ] **Step 4: Implement the production operations adapter**

`NesQueueOperations` loads current protected settings, validates the fresh order fingerprint before `PrepareAsync`, reuses `NesDocumentStore` for document identity, creates UBL with `NesUblBuilder`, and uses `NesConnection` for upload/list/download. `FindRemoteAsync` matches marketplace, shop, order and kind exactly; multiple matches throw a validation exception. PDF output must start with `%PDF-`, stay below 10 MB, use an alphanumeric filename, and be written atomically under `nes-invoices`.

For Trendyol upload, require numeric `PackageId`, 16-character invoice number validation already enforced by `TrendyolInvoiceClient`, and current protected Trendyol settings. Non-Trendyol rows with `UploadMarketplace=true` are blocked as unsupported.

- [ ] **Step 5: Run processor and existing NES tests and verify GREEN**

Run: `dotnet test tests/MarketplaceHub.Tests/MarketplaceHub.Tests.csproj -c Release --filter "NesQueueProcessorTests|NesIntegrationTests|OrderInvoiceWorkspaceTests"`

Expected: all selected tests pass with zero failures.

- [ ] **Step 6: Commit only the task files**

```powershell
git add -- NesQueueOperations.cs NesQueueProcessor.cs NesIntegration.cs tests/MarketplaceHub.Tests/NesQueueProcessorTests.cs
git commit -m "feat: process NES invoice queue step by step"
```

---

### Task 4: Background Sequential Runner and Recovery

**Files:**
- Create: `NesQueueRunner.cs`
- Create: `tests/MarketplaceHub.Tests/NesQueueRunnerTests.cs`
- Modify: `App.xaml.cs`

**Interfaces:**
- Consumes: `NesQueueProcessor.ProcessNextAsync`
- Produces: `NesQueueRunner.Start`, `StopAsync`, `Wake`, `StatusChanged`

- [ ] **Step 1: Write failing runner tests**

```csharp
[TestMethod]
public async Task WakeCoalescesAndNeverRunsTwoProcessorsConcurrently()
{
    var processor = new ConcurrencyDetectingProcessor(steps: 4);
    await using var runner = new NesQueueRunner(processor.ProcessNextAsync,
        TimeSpan.FromMilliseconds(5));

    runner.Start();
    runner.Wake();
    runner.Wake();
    await processor.Completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

    Assert.AreEqual(1, processor.MaximumConcurrency);
    Assert.AreEqual(4, processor.CompletedSteps);
}

[TestMethod]
public async Task StopCancelsTheCurrentWaitAndDoesNotStartAnotherStep()
{
    var processor = new BlockingProcessor();
    await using var runner = new NesQueueRunner(processor.ProcessNextAsync, TimeSpan.FromMinutes(1));
    runner.Start();
    runner.Wake();
    await processor.Started.Task;

    await runner.StopAsync();

    Assert.AreEqual(1, processor.CallCount);
}
```

Define `ConcurrencyDetectingProcessor.ProcessNextAsync` in the test file with `Interlocked.Increment`/`Decrement`, a five-millisecond delay and a fixed remaining-step counter; it completes `Completed` after four calls and returns `false` on the next call. Define `BlockingProcessor.ProcessNextAsync` to increment `CallCount`, complete `Started`, then await `Task.Delay(Timeout.InfiniteTimeSpan, token)`. These are test-only types and never enter production.

- [ ] **Step 2: Run runner tests and verify RED**

Run: `dotnet test tests/MarketplaceHub.Tests/MarketplaceHub.Tests.csproj -c Release --filter NesQueueRunnerTests`

Expected: compilation fails because `NesQueueRunner` is absent.

- [ ] **Step 3: Implement a single-reader runner**

Use `Channel<bool>` with `SingleReader=true`, bounded capacity 1 and drop-write behavior to coalesce wake signals. The loop must await `ProcessNextAsync` repeatedly while it returns true, publish a redacted status snapshot, then wait for either `Wake` or the earliest retry time. No `System.Timers.Timer` and no overlapping `Task.Run` calls.

`App.xaml.cs` creates one runner after startup checks, starts it after the main window is ready, and awaits disposal during application exit. If initialization fails, log a redacted startup error and keep the manual application usable.

- [ ] **Step 4: Run runner and startup tests and verify GREEN**

Run: `dotnet test tests/MarketplaceHub.Tests/MarketplaceHub.Tests.csproj -c Release --filter "NesQueueRunnerTests|StartupResilienceTests|SingleInstanceGuardTests"`

Expected: selected tests pass and no background task survives disposal.

- [ ] **Step 5: Commit only the task files**

```powershell
git add -- NesQueueRunner.cs App.xaml.cs tests/MarketplaceHub.Tests/NesQueueRunnerTests.cs
git commit -m "feat: run NES queue sequentially in background"
```

---

### Task 5: Batch Preview and Queue Management UI

**Files:**
- Create: `NesQueuePanel.cs`
- Create: `NesBatchInvoiceDialog.cs`
- Create: `tests/MarketplaceHub.Tests/NesQueueWorkspaceTests.cs`
- Modify: `OrdersPanel.cs`
- Modify: `OrderInvoiceDialog.cs`

**Interfaces:**
- Consumes: planner, alias store, queue store and runner from Tasks 1–4
- Produces: selection preview, explicit approval, queue monitor and controlled retry/cancel actions

- [ ] **Step 1: Write failing UI contract tests against extracted view models**

```csharp
[TestMethod]
public void BatchPreviewEnablesApprovalOnlyWhenAtLeastOneRowIsReady()
{
    var request = new NesQueueRequest("Trendyol", "411150", "2", "20",
        NesDocumentKind.EArchive, true, "", "hash-2");
    var blocked = new NesQueuePlan([new("1", "Blocked", "VKN eksik", null)]);
    var mixed = new NesQueuePlan([
        new("1", "Blocked", "VKN eksik", null),
        new("2", "Ready", "Hazır", request)]);

    Assert.IsFalse(NesBatchInvoiceViewState.From(blocked).CanApprove);
    Assert.IsTrue(NesBatchInvoiceViewState.From(mixed).CanApprove);
    Assert.AreEqual(1, NesBatchInvoiceViewState.From(mixed).ReadyCount);
}

[TestMethod]
public void RetrySelectionIncludesOnlyRetryWaitingRows()
{
    var selected = NesQueueSelectionPolicy.Retryable([
        QueueItem("1", NesQueueState.RetryWaiting),
        QueueItem("2", NesQueueState.Unknown),
        QueueItem("3", NesQueueState.Blocked)]);

    CollectionAssert.AreEqual(new[] { "1" }, selected.Select(x => x.Id).ToArray());
}
```

Define `QueueItem` in the test class as `new NesQueueItem { Id=id, Marketplace="Trendyol", ShopId="411150", OrderId=id, State=state }`. `NesBatchInvoiceViewState.From` and `NesQueueSelectionPolicy.Retryable` are pure production view-policy functions in `NesBatchInvoiceDialog.cs` and `NesQueuePanel.cs`; the WPF controls consume their results.

- [ ] **Step 2: Run UI contract tests and verify RED**

Run: `dotnet test tests/MarketplaceHub.Tests/MarketplaceHub.Tests.csproj -c Release --filter NesQueueWorkspaceTests`

Expected: compilation fails because the view-state contracts are absent.

- [ ] **Step 3: Implement batch preview dialog**

The dialog receives the currently selected `OrderSnapshot` objects. It displays order number, customer, document type, VKN/TCKN, receiver alias, total, package, planned steps, decision and reason. E-Invoice rows allow editing the receiver alias; saving writes it to `NesRecipientAliasStore` and rebuilds the plan. The approval checkbox text must state the ready count and that real NES invoices will be sent. The confirm button stays disabled until the checkbox is selected and at least one row is `Ready`.

- [ ] **Step 4: Implement queue panel**

Display order, marketplace/shop, current step, state, invoice number, attempts, next retry and redacted error. Actions:

- `Kuyruğu çalıştır`: wakes the runner
- `Durdur`: stops after cancelling the active request safely
- `Yeniden dene`: enabled only for `RetryWaiting`
- `Mutabakat gerekli`: shows `Unknown` records and opens the existing NES remote list; it never resends
- `İptal et`: only `Planned`, `Ready`, `RetryWaiting` or `Blocked`

- [ ] **Step 5: Wire OrdersPanel bulk actions**

Add these real actions beside the existing preview actions:

- `Seçilileri NES kuyruğuna ekle`
- `NES fatura numaralarını yenile`
- `NES işlem kuyruğu`

Require at least one selected row. After enqueue, wake the runner and refresh the queue window. Keep existing single-order `OrderInvoiceDialog`; refactor only shared fingerprint/validation logic into `NesQueuePlanner` so single and batch behavior cannot disagree.

- [ ] **Step 6: Run UI and order regression tests and verify GREEN**

Run: `dotnet test tests/MarketplaceHub.Tests/MarketplaceHub.Tests.csproj -c Release --filter "NesQueueWorkspaceTests|OrderInvoiceWorkspaceTests|OrderFilterTests|OrderCardWorkspaceTests"`

Expected: selected tests pass with zero failures.

- [ ] **Step 7: Commit only the task files**

```powershell
git add -- NesQueuePanel.cs NesBatchInvoiceDialog.cs OrdersPanel.cs OrderInvoiceDialog.cs tests/MarketplaceHub.Tests/NesQueueWorkspaceTests.cs
git commit -m "feat: add NES batch invoice workspace"
```

---

### Task 6: End-to-End Recovery, Full Regression, and Release Artifact

**Files:**
- Create: `tests/MarketplaceHub.Tests/NesQueueEndToEndTests.cs`
- Modify: `IMPLEMENTATION_STATUS.md`
- Modify: `TODO.md`
- Modify: `docs/MARKETPLACE-ZIP-COMPARISON.md`

**Interfaces:**
- Consumes: all Tasks 1–5
- Produces: a verified end-to-end workflow and documented operator behavior

- [ ] **Step 1: Write the failing end-to-end recovery test**

```csharp
[TestMethod]
public async Task RestartAfterInvoiceSendContinuesAtReconciliationWithoutSecondSend()
{
    using var temp = new TemporaryDirectory();
    var operations = new RecordingOperations { StopAfterSuccessfulSend = true };
    var firstProcess = BuildSystem(temp.Path, operations);
    firstProcess.Enqueue(ValidOrder(), uploadMarketplace: true);

    await firstProcess.RunUntilStopped();
    var restarted = BuildSystem(temp.Path, operations);
    await restarted.Drain();

    Assert.AreEqual(1, operations.SendCount);
    Assert.AreEqual(1, operations.UploadCount);
    var saved = new OrdersStore(temp.Path).Find("Trendyol", "411150", "11600461863");
    Assert.AreEqual("EAR2026000000001", saved!.InvoiceNumber);
    Assert.AreEqual(NesQueueState.Completed,
        new NesInvoiceQueueStore(temp.Path).List().Single().State);
}
```

`BuildSystem` returns a test-only `QueueSystemHarness` holding real `NesInvoiceQueueStore`, `OrdersStore`, `AuditStore` and `NesQueueProcessor` instances. `Enqueue` first saves the supplied order through `OrdersStore.SaveManual`, builds a plan with `NesQueuePlanner.Build`, and enqueues the single ready request. `RunUntilStopped` invokes `ProcessNextAsync` until the recording operations throws the test-only simulated process-stop exception immediately after persisting the successful send result. `Drain` creates no new queue rows and calls the restarted processor until it returns false. The harness never modifies production code to expose a test-only reset or cleanup method.

- [ ] **Step 2: Run the end-to-end test and verify RED**

Run: `dotnet test tests/MarketplaceHub.Tests/MarketplaceHub.Tests.csproj -c Release --filter NesQueueEndToEndTests`

Expected: the test fails at the exact missing recovery transition.

- [ ] **Step 3: Implement the minimal recovery correction**

Persist each successful external result before advancing to the next step. On startup, expired `Running` leases return to `Ready` at the recorded step. A row at `AwaitNumber` must call only `FindRemoteAsync`; it must never call `SendAsync` again. Add an audit event for recovery with module `nes-queue`, action `recover`, and no credentials or raw response bodies.

- [ ] **Step 4: Run targeted tests and verify GREEN**

Run: `dotnet test tests/MarketplaceHub.Tests/MarketplaceHub.Tests.csproj -c Release --filter "NesQueueEndToEndTests|NesQueueProcessorTests|NesInvoiceQueueStoreTests"`

Expected: all selected tests pass.

- [ ] **Step 5: Update operator documentation**

Document the exact menu path, approval behavior, E-Invoice receiver alias requirement, `Unknown` reconciliation rule, retryable status codes, PDF location, and how to stop/resume the queue. Mark only implemented behavior complete in `TODO.md`; leave later printing, live return, archive and Hepsiburada phases open.

- [ ] **Step 6: Run the full Release test suite**

Run: `dotnet test tests/MarketplaceHub.Tests/MarketplaceHub.Tests.csproj -c Release --no-restore`

Expected: zero failed tests. Record the total passed count from this fresh run.

- [ ] **Step 7: Build and publish the Windows artifact**

Run: `dotnet build TrMarketplaceHubDesktop.csproj -c Release --no-restore`

Expected: build exits with code 0 and zero errors.

Run:

```powershell
dotnet publish TrMarketplaceHubDesktop.csproj -c Release -r win-x64 --self-contained true --no-restore -o Windows-NES-Queue
```

Expected: `Windows-NES-Queue\TrMarketplaceHubDesktop.exe` exists and the publish command exits with code 0.

- [ ] **Step 8: Verify the final diff and commit task files**

Run:

```powershell
git diff --check
git status --short
```

Review every changed path. Stage only the NES queue implementation, tests and the three documentation files; do not stage unrelated pre-existing work.

```powershell
git add -- tests/MarketplaceHub.Tests/NesQueueEndToEndTests.cs IMPLEMENTATION_STATUS.md TODO.md docs/MARKETPLACE-ZIP-COMPARISON.md
git commit -m "docs: record NES queue workflow"
```
