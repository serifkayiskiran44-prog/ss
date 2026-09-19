using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;
using TrMarketplaceHubDesktop.Trendyol;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class MultiStoreOrderSyncTests
{
    string root = null!;

    [TestInitialize]
    public void Setup() => root = Path.Combine(Path.GetTempPath(), "multi-store-orders-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    [TestMethod]
    public async Task SameOrderNumberInTwoShopsKeepsTwoOrdersAndTwoExactReceipts()
    {
        var setup = SetupTwoAccounts();
        setup.FirstReader.Set(Order("101", "ORD-1", "REMOTE-A", 1, 10));
        setup.SecondReader.Set(Order("202", "ORD-1", "REMOTE-B", 1, 11));

        var results = await setup.Service.RefreshAllAsync();

        Assert.AreEqual(2, results.Count(result => result.Status == MarketplaceOrderSyncStatus.Succeeded));
        Assert.AreEqual(2, new OrdersStore(root).ReadAll().Count(order => order.OrderId == "ORD-1"));
        Assert.AreEqual(8, new InventoryLocationStore(root).GetBalance(setup.Product.Id, InventoryLocationStore.OnlineLocationId).Quantity);
        Assert.AreEqual(2, new InventoryLocationStore(root).Movements(setup.Product.Id).Count(movement => movement.Kind == InventoryMovementKind.OnlineOrder));
    }

    [TestMethod]
    public async Task FailedAccountDoesNotHideOrRollbackSuccessfulAccount()
    {
        var setup = SetupTwoAccounts();
        setup.FirstReader.Error = new InvalidOperationException("api-secret-value");
        setup.SecondReader.Set(Order("202", "ORD-2", "REMOTE-B", 1, 12));

        var results = await setup.Service.RefreshAllAsync();

        Assert.AreEqual(MarketplaceOrderSyncStatus.Failed, results.Single(result => result.ConnectionId == setup.First.Id).Status);
        Assert.AreEqual(MarketplaceOrderSyncStatus.Succeeded, results.Single(result => result.ConnectionId == setup.Second.Id).Status);
        Assert.AreEqual("ORD-2", new OrdersStore(root).ReadAll().Single().OrderId);
        var state = new OrdersStore(root).GetSyncState(setup.First.Id)!;
        Assert.AreEqual(1, state.FailureCount);
        Assert.IsTrue(state.NextAttemptUtc > setup.Now);
        Assert.IsFalse(state.LastError.Contains("api-secret-value", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DuplicateRefreshAndNewerStatusReadDeductOnlineStockOnce()
    {
        var setup = SetupOneAccount();
        setup.Reader.Set(Order("101", "ORD-1", "REMOTE-A", 2, 10));
        await setup.Service.RefreshAllAsync();
        setup.Reader.Set(Order("101", "ORD-1", "REMOTE-A", 2, 20, "Shipped"));

        await setup.Service.RefreshAllAsync();

        Assert.AreEqual(8, new InventoryLocationStore(root).GetBalance(setup.Product.Id, InventoryLocationStore.OnlineLocationId).Quantity);
        Assert.AreEqual(1, new InventoryLocationStore(root).Movements(setup.Product.Id).Count(movement => movement.Kind == InventoryMovementKind.OnlineOrder));
        Assert.AreEqual("shipped", new OrdersStore(root).ReadAll().Single().RawStatus);
    }

    [TestMethod]
    public async Task CancelledOrderIsCollectedButNeverDeductsInventory()
    {
        var setup = SetupOneAccount();
        setup.Reader.Set(Order("101", "ORD-CANCELLED", "REMOTE-A", 2, 10, "Cancelled"));

        var result = (await setup.Service.RefreshAllAsync()).Single();

        Assert.AreEqual(MarketplaceOrderSyncStatus.Succeeded, result.Status);
        Assert.AreEqual("cancelled", new OrdersStore(root).ReadAll().Single().RawStatus);
        Assert.AreEqual(10, new InventoryLocationStore(root).GetBalance(setup.Product.Id, InventoryLocationStore.OnlineLocationId).Quantity);
    }

    [TestMethod]
    public async Task DefaultTrendyolAdapterLoadsOnlyTheSelectedConnectionVaultEnvelope()
    {
        var connections = new MarketplaceConnectionStore(root);
        var first = connections.Save("trendyol", "101", "First", true);
        var second = connections.Save("trendyol", "202", "Second", true);
        var vault = new MarketplaceCredentialVault(root);
        vault.Save(first.Id, first.Channel, first.ShopId, new TrendyolSettings("101", "first-key", "first-secret", "first-agent"));
        vault.Save(second.Id, second.Channel, second.ShopId, new TrendyolSettings("202", "second-key", "second-secret", "second-agent"));
        HttpRequestMessage? captured = null;
        var registry = MarketplaceAdapterRegistry.CreateDefault(root, () => new HttpClient(new Handler(request =>
        {
            captured = Clone(request);
            return Json("""{"page":0,"totalPages":0,"totalElements":0,"content":[]}""");
        })) { Timeout = TimeSpan.FromSeconds(5) });

        var rows = await registry.Get("trendyol").ReadOrdersAsync(second.Id, DateTime.MinValue, CancellationToken.None);

        Assert.AreEqual(0, rows.Count);
        StringAssert.Contains(captured!.RequestUri!.AbsolutePath, "/sellers/202/orders");
        Assert.AreEqual(Convert.ToBase64String(Encoding.UTF8.GetBytes("second-key:second-secret")), captured.Headers.Authorization!.Parameter);
        Assert.AreNotEqual(Convert.ToBase64String(Encoding.UTF8.GetBytes("first-key:first-secret")), captured.Headers.Authorization.Parameter);
    }

    [TestMethod]
    public async Task UnmatchedOrOtherAccountBindingRequiresReviewAndNeverChangesStock()
    {
        var setup = SetupTwoAccounts();
        setup.FirstReader.Set(Order("101", "ORD-X", "REMOTE-B", 2, 10));
        setup.SecondReader.Set();

        await setup.Service.RefreshAllAsync();

        var order = new OrdersStore(root).ReadAll().Single();
        Assert.IsTrue(order.ReviewRequired);
        Assert.IsTrue(order.Items.Single().ReviewRequired);
        Assert.ThrowsException<InvalidOperationException>(() => new OrderStockDecisionService(new CatalogStore(root)).CreatePreview(order));
        Assert.AreEqual(10, new InventoryLocationStore(root).GetBalance(setup.Product.Id, InventoryLocationStore.OnlineLocationId).Quantity);
        Assert.AreEqual(0, new InventoryLocationStore(root).Movements(setup.Product.Id).Count(movement => movement.Kind == InventoryMovementKind.OnlineOrder));
    }

    [TestMethod]
    public async Task BindingRepairReevaluatesSameRemoteVersionAndPersistsResolvedIdentity()
    {
        var setup = SetupOneAccount();
        setup.Reader.Set(Order("101", "ORD-REPAIR", "REMOTE-X", 2, 10));
        await setup.Service.RefreshAllAsync();
        var bindings = new ProductChannelBindingStore(root);
        var existing = bindings.Get(setup.Product.Id, setup.Connection.Id)!;
        bindings.Save(existing with { RemoteSku = "REMOTE-X" }, existing.Version);

        var result = (await setup.Service.RefreshAllAsync()).Single();

        Assert.AreEqual(MarketplaceOrderSyncStatus.Succeeded, result.Status);
        Assert.AreEqual(8, new InventoryLocationStore(root).GetBalance(setup.Product.Id, InventoryLocationStore.OnlineLocationId).Quantity);
        var repaired = new OrdersStore(root).ReadAll().Single();
        Assert.IsFalse(repaired.ReviewRequired);
        Assert.AreEqual(setup.Product.Id, repaired.Items.Single().ProductId);
    }

    [TestMethod]
    public async Task OlderResolvedResponseCannotApplyStockWhenNewerReviewOrderWins()
    {
        var setup = SetupOneAccount();
        setup.Reader.Set(Order("101", "ORD-OLD", "UNMATCHED", 2, 20));
        await setup.Service.RefreshAllAsync();
        var binding = new ProductChannelBindingStore(root).Get(setup.Product.Id, setup.Connection.Id)!;
        new ProductChannelBindingStore(root).Save(binding with { RemoteSku = "UNMATCHED" }, binding.Version);
        setup.Reader.Set(Order("101", "ORD-OLD", "UNMATCHED", 2, 10));

        var result = (await setup.Service.RefreshAllAsync()).Single();

        Assert.AreEqual(MarketplaceOrderSyncStatus.Succeeded, result.Status);
        var stored = new OrdersStore(root).ReadAll().Single();
        Assert.IsTrue(stored.ReviewRequired);
        Assert.AreEqual(DateTimeOffset.UnixEpoch.AddSeconds(20), stored.UpdatedAt);
        Assert.IsNull(new CatalogStore(root).GetOrderStockStatus("Trendyol", "101", "ORD-OLD"));
        Assert.AreEqual(10, new InventoryLocationStore(root).GetBalance(setup.Product.Id, InventoryLocationStore.OnlineLocationId).Quantity);
        Assert.AreEqual(0, new InventoryLocationStore(root).Movements(setup.Product.Id).Count(movement => movement.Kind == InventoryMovementKind.OnlineOrder));
    }

    [DataTestMethod]
    [DataRow("delete")]
    [DataRow("disable")]
    [DataRow("repoint")]
    public void BindingMutationAfterResolveRollsBackOrderAndStock(string mutation)
    {
        var setup = SetupOneAccount();
        var service = new OrderStockDecisionService(root);
        var resolution = service.ResolveAccountOrder(setup.Connection, Order("101", "ORD-BINDING-RACE", "REMOTE-A", 2, 10));
        var bindings = new ProductChannelBindingStore(root);
        var original = bindings.Get(setup.Product.Id, setup.Connection.Id)!;
        if (mutation == "disable") bindings.Save(original with { ManageStock = false }, original.Version);
        else
        {
            bindings.Delete(original.ProductId, original.ConnectionId, original.Version, setup.Connection.Revision);
            if (mutation == "repoint")
            {
                var replacement = new CatalogStore(root).CreateManual(new() { Sku = "REPOINTED", Name = "Replacement", Stock = 9, Currency = "TRY" });
                bindings.Save(original with { ProductId = replacement.Id, Version = 0, UpdatedUtc = default }, 0);
            }
        }

        Assert.ThrowsException<InvalidOperationException>(() => service.ApplyAccountOrder(setup.Connection, resolution, deductStock: true));

        Assert.AreEqual(0, new OrdersStore(root).ReadAll().Count);
        Assert.IsNull(new CatalogStore(root).GetOrderStockStatus("Trendyol", "101", "ORD-BINDING-RACE"));
        Assert.AreEqual(10, new InventoryLocationStore(root).GetBalance(setup.Product.Id, InventoryLocationStore.OnlineLocationId).Quantity);
        Assert.AreEqual(0, new InventoryLocationStore(root).Movements(setup.Product.Id).Count(movement => movement.Kind == InventoryMovementKind.OnlineOrder));
    }

    [TestMethod]
    public async Task AmbiguousExactBindingRequiresReviewAndDoesNotChooseEitherProduct()
    {
        var setup = SetupOneAccount();
        var second = new CatalogStore(root).CreateManual(new() { Sku = "LOCAL-2", Barcode = "LOCAL-BAR-2", Name = "Second", Stock = 7, Currency = "TRY" });
        new ProductChannelBindingStore(root).Save(new(second.Id, setup.Connection.Id, "r-second", "REMOTE-A", "BAR-SECOND", true, true, true, "", "", "Active", 0, default), 0);
        setup.Reader.Set(Order("101", "ORD-AMBIGUOUS", "REMOTE-A", 2, 10));

        await setup.Service.RefreshAllAsync();

        var order = new OrdersStore(root).ReadAll().Single();
        Assert.IsTrue(order.ReviewRequired);
        Assert.AreEqual("", order.Items.Single().ProductId);
        Assert.AreEqual(0, new InventoryLocationStore(root).Movements(setup.Product.Id).Count(movement => movement.Kind == InventoryMovementKind.OnlineOrder));
        Assert.AreEqual(0, new InventoryLocationStore(root).Movements(second.Id).Count(movement => movement.Kind == InventoryMovementKind.OnlineOrder));
    }

    [TestMethod]
    public async Task CursorAndBackoffAreIsolatedPerConnection()
    {
        var setup = SetupTwoAccounts();
        setup.FirstReader.Error = new HttpRequestException("boom");
        setup.SecondReader.Set(Order("202", "ORD-2", "REMOTE-B", 1, 42));
        await setup.Service.RefreshAllAsync();
        var firstCalls = setup.FirstReader.Calls;
        var secondState = new OrdersStore(root).GetSyncState(setup.Second.Id)!;

        var retry = await setup.Service.RefreshAllAsync();

        Assert.AreEqual(firstCalls, setup.FirstReader.Calls, "Bounded backoff must skip only the failed connection.");
        Assert.AreEqual(MarketplaceOrderSyncStatus.Backoff, retry.Single(result => result.ConnectionId == setup.First.Id).Status);
        Assert.AreEqual(DateTime.UnixEpoch.AddSeconds(42), secondState.CursorUtc);
        Assert.AreEqual(0, secondState.FailureCount);
        Assert.AreEqual(DateTime.MinValue, setup.FirstReader.FromUtc.Single());
        Assert.AreEqual(DateTime.UnixEpoch.AddSeconds(42), setup.SecondReader.FromUtc.Last());
    }

    [TestMethod]
    public async Task DisabledAndUnsupportedAccountsAreNotRead()
    {
        var connections = new MarketplaceConnectionStore(root);
        var disabled = connections.Save("trendyol", "101", "Disabled", false);
        var enabled = connections.Save("trendyol", "202", "Enabled", true);
        var reader = new FakeAdapter("trendyol", false);
        var service = new MarketplaceOrderSyncService(root, new MarketplaceAdapterRegistry(new[] { reader }), () => DateTime.UtcNow);

        var results = await service.RefreshAllAsync();

        Assert.AreEqual(0, reader.Calls);
        Assert.AreEqual(0, results.Count);
        Assert.IsNull(new OrdersStore(root).GetSyncState(disabled.Id));
        Assert.IsNull(new OrdersStore(root).GetSyncState(enabled.Id));
    }

    [TestMethod]
    public async Task ResultFinishingAfterConnectionWasDisabledCannotSaveOrderOrCursor()
    {
        var setup = SetupOneAccount(blocking: true);
        setup.Reader.Set(Order("101", "ORD-LATE", "REMOTE-A", 1, 10));
        var refresh = setup.Service.RefreshAllAsync();
        await setup.Reader.Started.Task;
        new MarketplaceConnectionStore(root).Save("trendyol", "101", "A", false, setup.Connection.Id);
        setup.Reader.Release.SetResult();

        var result = (await refresh).Single();

        Assert.AreEqual(MarketplaceOrderSyncStatus.Stale, result.Status);
        Assert.AreEqual(0, new OrdersStore(root).ReadAll().Count);
        Assert.IsNull(new OrdersStore(root).GetSyncState(setup.Connection.Id));
    }

    [TestMethod]
    public async Task DisableAfterReadFenceButBeforeAtomicPersistLeavesNoOrderOrStockMutation()
    {
        var setup = SetupOneAccount();
        setup.Reader.Set(Order("101", "ORD-RACE", "REMOTE-A", 2, 10));
        var service = new MarketplaceOrderSyncService(root,
            new MarketplaceAdapterRegistry(new[] { setup.Reader }),
            () => DateTime.UnixEpoch.AddDays(100),
            beforePersist: connection => new MarketplaceConnectionStore(root).SetEnabled(connection.Id, false));

        var result = (await service.RefreshAllAsync()).Single();

        Assert.AreEqual(MarketplaceOrderSyncStatus.Stale, result.Status);
        Assert.AreEqual(0, new OrdersStore(root).ReadAll().Count);
        Assert.AreEqual(10, new InventoryLocationStore(root).GetBalance(setup.Product.Id, InventoryLocationStore.OnlineLocationId).Quantity);
        Assert.AreEqual(0, new InventoryLocationStore(root).Movements(setup.Product.Id).Count(movement => movement.Kind == InventoryMovementKind.OnlineOrder));
        Assert.IsNull(new OrdersStore(root).GetSyncState(setup.Connection.Id));
    }

    [TestMethod]
    public async Task DisableBetweenFinalCheckAndCursorWriteLeavesNoStaleCursor()
    {
        var setup = SetupOneAccount();
        setup.Reader.Set();
        var service = new MarketplaceOrderSyncService(root,
            new MarketplaceAdapterRegistry(new[] { setup.Reader }),
            () => DateTime.UnixEpoch.AddDays(100),
            beforeSyncStatePersist: connection => new MarketplaceConnectionStore(root).SetEnabled(connection.Id, false));

        var result = (await service.RefreshAllAsync()).Single();

        Assert.AreEqual(MarketplaceOrderSyncStatus.Stale, result.Status);
        Assert.IsNull(new OrdersStore(root).GetSyncState(setup.Connection.Id));
    }

    [TestMethod]
    public async Task CorruptAccountSyncStateFailsClosedWithoutBlockingAnotherAccount()
    {
        var setup = SetupTwoAccounts();
        setup.SecondReader.Set(Order("202", "ORD-OK", "REMOTE-B", 1, 12));
        _ = new OrdersStore(root);
        using (var connection = new SqliteConnection("Data Source=" + Path.Combine(root, "orders.db")))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO order_sync_accounts VALUES($id,'trendyol','101',1,'not-a-date',0,NULL,'',1,'not-a-date')";
            command.Parameters.AddWithValue("$id", setup.First.Id); command.ExecuteNonQuery();
        }

        var results = await setup.Service.RefreshAllAsync();

        Assert.AreEqual(MarketplaceOrderSyncStatus.Failed, results.Single(result => result.ConnectionId == setup.First.Id).Status);
        Assert.AreEqual(MarketplaceOrderSyncStatus.Succeeded, results.Single(result => result.ConnectionId == setup.Second.Id).Status);
        Assert.AreEqual(0, setup.FirstReader.Calls);
        Assert.AreEqual("ORD-OK", new OrdersStore(root).ReadAll().Single().OrderId);
    }

    [TestMethod]
    public async Task TrendyolReaderUsesOfficialAccountScopedRequestAndParsesRemoteIdentity()
    {
        HttpRequestMessage? captured = null;
        var handler = new Handler(request =>
        {
            captured = Clone(request);
            return Json("""{"page":0,"totalPages":1,"totalElements":1,"content":[{"orderNumber":"ORD-9","shipmentPackageId":77,"status":"Created","lastModifiedDate":10000,"totalPrice":125.50,"currencyCode":"TRY","customerFirstName":"Ada","customerLastName":"Lovelace","shipmentAddress":{"fullAddress":"Sokak 1","city":"İstanbul","district":"Kadıköy"},"invoiceAddress":{"fullAddress":"Cadde 2","city":"İstanbul","district":"Üsküdar","taxNumber":"123"},"lines":[{"productName":"Ürün","merchantSku":"REMOTE-SKU","barcode":"REMOTE-BAR","quantity":2,"price":62.75}]}]}""");
        });
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        using var client = new TrendyolApiClient(new("42", "api-key", "api-secret", "agent"), http);

        var rows = await client.GetOrdersAsync(DateTime.UnixEpoch, CancellationToken.None);

        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual("ORD-9", rows[0].OrderId);
        Assert.AreEqual("REMOTE-SKU", rows[0].Items.Single().Sku);
        Assert.AreEqual("REMOTE-BAR", rows[0].Items.Single().Barcode);
        StringAssert.Contains(captured!.RequestUri!.AbsolutePath, "/integration/order/sellers/42/orders");
        Assert.IsFalse(captured.RequestUri.Query.Contains("startDate=0&", StringComparison.Ordinal), "Initial sync must use Trendyol's bounded order-history window.");
        StringAssert.Contains(captured.RequestUri.Query, "orderByField=PackageLastModifiedDate");
        Assert.AreEqual("Basic", captured.Headers.Authorization!.Scheme);
    }

    [TestMethod]
    public void TrendyolOrdersReadCapabilityIsEnabledOnlyForItsRealReader()
    {
        Assert.IsTrue(MarketplaceConnectionCatalog.Get("trendyol").Capabilities.Supports(MarketplaceOperation.OrdersRead));
        Assert.IsFalse(MarketplaceConnectionCatalog.Get("ebay").Capabilities.Supports(MarketplaceOperation.OrdersRead));
        Assert.IsFalse(MarketplaceConnectionCatalog.Get("allegro").Capabilities.Supports(MarketplaceOperation.OrdersRead));
    }

    (MarketplaceOrderSyncService Service, MarketplaceConnection First, MarketplaceConnection Second, FakeAdapter FirstReader, FakeAdapter SecondReader, CatalogProduct Product, DateTime Now) SetupTwoAccounts()
    {
        var connections = new MarketplaceConnectionStore(root);
        var first = connections.Save("trendyol", "101", "A", true);
        var second = connections.Save("etsy", "202", "B", true);
        var catalog = new CatalogStore(root);
        var product = catalog.CreateManual(new() { Sku = "LOCAL", Barcode = "LOCAL-BAR", Name = "Product", Stock = 10, Currency = "TRY" });
        var bindings = new ProductChannelBindingStore(root);
        bindings.Save(new(product.Id, first.Id, "r-a", "REMOTE-A", "BAR-A", true, true, true, "", "", "Active", 0, default), 0);
        bindings.Save(new(product.Id, second.Id, "r-b", "REMOTE-B", "BAR-B", true, true, true, "", "", "Active", 0, default), 0);
        var firstReader = new FakeAdapter("trendyol", true);
        var secondReader = new FakeAdapter("etsy", true);
        var now = DateTime.UnixEpoch.AddDays(100);
        var service = new MarketplaceOrderSyncService(root, new MarketplaceAdapterRegistry(new IMarketplaceAdapter[] { firstReader, secondReader }), () => now);
        return (service, first, second, firstReader, secondReader, product, now);
    }

    (MarketplaceOrderSyncService Service, MarketplaceConnection Connection, FakeAdapter Reader, CatalogProduct Product) SetupOneAccount(bool blocking = false)
    {
        var connections = new MarketplaceConnectionStore(root);
        var connection = connections.Save("trendyol", "101", "A", true);
        var catalog = new CatalogStore(root);
        var product = catalog.CreateManual(new() { Sku = "LOCAL", Barcode = "LOCAL-BAR", Name = "Product", Stock = 10, Currency = "TRY" });
        new ProductChannelBindingStore(root).Save(new(product.Id, connection.Id, "r-a", "REMOTE-A", "BAR-A", true, true, true, "", "", "Active", 0, default), 0);
        var reader = new FakeAdapter("trendyol", true, blocking);
        return (new(root, new MarketplaceAdapterRegistry(new[] { reader }), () => DateTime.UnixEpoch.AddDays(100)), connection, reader, product);
    }

    static OrderSnapshot Order(string shop, string id, string sku, int quantity, long updatedSeconds, string status = "Created") => new()
    {
        Marketplace = shop == "202" ? "Etsy" : "Trendyol",
        ShopId = shop,
        OrderId = id,
        RawStatus = status,
        Source = "Remote API",
        UpdatedAt = DateTimeOffset.UnixEpoch.AddSeconds(updatedSeconds),
        SourceUpdatedAt = DateTimeOffset.UnixEpoch.AddSeconds(updatedSeconds),
        Items = [new() { Title = "Product", Sku = sku, Quantity = quantity }]
    };

    sealed class FakeAdapter(string channel, bool ordersRead, bool blocking = false) : IMarketplaceAdapter
    {
        IReadOnlyList<OrderSnapshot> orders = Array.Empty<OrderSnapshot>();
        public string Channel { get; } = channel;
        public MarketplaceCapabilities Capabilities { get; } = new(new HashSet<MarketplaceOperation>(ordersRead ? [MarketplaceOperation.OrdersRead] : []));
        public Exception? Error { get; set; }
        public int Calls { get; private set; }
        public List<DateTime> FromUtc { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Set(params OrderSnapshot[] value) => orders = value;
        public Task<IReadOnlyList<RemoteProductIdentity>> ReadProductsAsync(string connectionId, CancellationToken token) => throw new NotSupportedException();
        public async Task<IReadOnlyList<OrderSnapshot>> ReadOrdersAsync(string connectionId, DateTime fromUtc, CancellationToken token)
        {
            Calls++; FromUtc.Add(fromUtc); Started.TrySetResult();
            if (blocking) await Release.Task.WaitAsync(token);
            if (Error is not null) throw Error;
            return orders.Select(order => order.Copy()).ToArray();
        }
    }

    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }

    static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    static HttpRequestMessage Clone(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers) clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return clone;
    }
}
