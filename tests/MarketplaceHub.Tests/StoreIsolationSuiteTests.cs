using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #787 (TEST: Store isolation integration suite). "Store" in this app is the marketplace shop (ShopId): there is
// one data directory per installation, and every shop-owned record is keyed by (channel|marketplace, shop, ...).
// The product master and the XML supplier sources are deliberately shared across shops (one catalog, many
// storefronts), so isolation means: an order, a stock receipt, a price policy, a listing mapping and a sync job
// of shop A are never visible to, borrowed by, or overwritten from shop B's scope -- exercised through the real
// repository/service entry points, never raw SQL. Unscoped reads (ReadAll, global search) are an explicit
// operator choice and are asserted to attribute every record to its shop instead of merging identities.
[TestClass]
public sealed class StoreIsolationSuiteTests
{
    static string NewRoot() => Path.Combine(Path.GetTempPath(), "store-isolation-" + Guid.NewGuid().ToString("N"));
    static void Cleanup(string root) { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }

    static OrderSnapshot Order(string shop, string orderId, string sku, string title, int quantity = 1) => new()
    {
        Marketplace = "Yerel", ShopId = shop, OrderId = orderId, RawStatus = "Açık",
        Items = { new OrderItem { Title = title, Sku = sku, Quantity = quantity } },
    };

    static (CatalogStore Catalog, string ProductId) SeedProduct(string root, string sku = "K1", int stock = 10)
    {
        var catalog = new CatalogStore(root);
        var source = new XmlSource { Id = "src", Name = "Src" };
        catalog.Import(source, new[] { new CatalogProduct { SourceId = source.Id, Sku = sku, Name = "Kupa", Price = 100m, Cost = 60m, CostCurrency = "TRY", Stock = stock, Active = true } });
        return (catalog, catalog.Products().Single().Id);
    }

    [TestMethod]
    public void OrdersAreReadSearchedAndReportedWithinTheirOwnShopWhileAnUnscopedReadIsAnExplicitChoice()
    {
        var root = NewRoot();
        try
        {
            var store = new OrdersStore(root);
            store.SaveManual(Order("shop-a", "o-1", "K1", "Kırmızı kupa"));
            store.SaveManual(Order("shop-b", "o-1", "M1", "Mavi kupa"));   // same order number, other shop

            var a = store.ReadPage(marketplace: "Yerel", shopId: "shop-a");
            Assert.AreEqual(1, a.Total);
            Assert.AreEqual("shop-a", a.Items.Single().ShopId);
            Assert.AreEqual("Kırmızı kupa", a.Items.Single().Items.Single().Title, "Shop A's o-1 is shop A's record, not shop B's o-1 under the same number.");

            Assert.AreEqual(0, store.ReadPage(shopId: "shop-b", query: "Kırmızı").Total, "A text search never crosses the shop scope it was issued in.");
            Assert.AreEqual(1, store.ReadPage(shopId: "shop-a", query: "Kırmızı").Total);
            Assert.AreEqual(0, store.ReadPage(shopId: "shop-c").Total, "An unknown shop sees nothing, not everything.");

            Assert.AreEqual(0, ProductOrderReport.Build("K1", store.ReadPage(shopId: "shop-b").Items).OrderCount, "A report built from shop B's scoped read cannot count shop A's sale of K1.");
            Assert.AreEqual(1, ProductOrderReport.Build("K1", store.ReadPage(shopId: "shop-a").Items).OrderCount);

            Assert.AreEqual(2, store.ReadPage().Total, "Reading without a shop is the operator's explicit all-shops view.");
            CollectionAssert.AreEquivalent(new[] { "shop-a", "shop-b" }, store.ReadAll().Select(o => o.ShopId).ToArray());
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void AStockDeductionAppliedForShopAsOrderIsNotMistakenForShopBsOrderWithTheSameNumber()
    {
        var root = NewRoot();
        try
        {
            var (catalog, _) = SeedProduct(root, "K1", stock: 10);
            var orders = new OrdersStore(root);
            orders.SaveManual(Order("shop-a", "o-1", "K1", "Kırmızı kupa", quantity: 3));
            orders.SaveManual(Order("shop-b", "o-1", "K1", "Kırmızı kupa", quantity: 2));
            var service = new OrderStockDecisionService(catalog);
            var orderA = orders.ReadPage(shopId: "shop-a").Items.Single();
            var orderB = orders.ReadPage(shopId: "shop-b").Items.Single();

            var applied = service.ApplyApproved(service.CreatePreview(orderA), approved: true);
            Assert.IsFalse(applied.AlreadyApplied);
            Assert.AreEqual(7, catalog.Products().Single().Stock, "Shop A's 3 units left the shared product master.");

            var previewB = service.CreatePreview(orderB);
            Assert.IsFalse(previewB.AlreadyApplied, "Shop B's o-1 is a different order; shop A's receipt must not mark it as already deducted.");
            Assert.IsNull(catalog.GetOrderStockStatus("Yerel", "shop-b", "o-1"));
            Assert.IsNotNull(catalog.GetOrderStockStatus("Yerel", "shop-a", "o-1"));

            service.ApplyApproved(previewB, approved: true);
            Assert.AreEqual(5, catalog.Products().Single().Stock, "Both shops draw from the one shared stock, each exactly once.");
            Assert.IsTrue(service.CreatePreview(orderA).AlreadyApplied, "Re-applying shop A's order is idempotent within its own scope.");
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void APricePolicyIsNeverBorrowedAcrossShopsAndAutomationJobsOnlyTouchTheirOwnShop()
    {
        var root = NewRoot();
        try
        {
            var (catalog, id) = SeedProduct(root);
            catalog.SavePricePolicy(new PricePolicy { Channel = "local", Shop = "shop-a", Formula = "x*2", Currency = "TRY", TryPerUnit = 1, Enabled = true, CommissionPercent = 10m, EstimatedShippingTry = 5m, TransactionCostTry = 0m, VatRatePercent = 0m });

            Assert.AreEqual(120m, catalog.PreviewPrice("local", "shop-a", id).Price);
            var ex = Assert.ThrowsException<InvalidOperationException>(() => catalog.PreviewPrice("local", "shop-b", id));
            StringAssert.Contains(ex.Message, "fiyat kuralı", "Shop B has no policy of its own and must not price with shop A's.");

            var automation = new AutomationStore(root);
            automation.Save(new AutomationJob { Kind = AutomationKind.Price, Enabled = true, NextRunUtc = DateTime.UtcNow.AddMinutes(-1), Channel = "local", Shop = "shop-a" });
            automation.Save(new AutomationJob { Kind = AutomationKind.Price, Enabled = true, NextRunUtc = DateTime.UtcNow.AddMinutes(-1), Channel = "local", Shop = "shop-b" });
            var sync = new SyncStore(root);
            var jobA = automation.List().Single(j => j.Shop == "shop-a");
            var jobB = automation.List().Single(j => j.Shop == "shop-b");

            var resultA = AutomationRunner.RunDue(catalog, automation, sync, jobA.Id, DateTime.UtcNow);
            var resultB = AutomationRunner.RunDue(catalog, automation, sync, jobB.Id, DateTime.UtcNow);

            Assert.AreEqual(1, resultA.Queued, string.Join(" | ", resultA.Errors));
            Assert.AreEqual(0, resultB.Queued, "Shop B's job cannot dispatch prices it has no policy for.");
            var jobs = sync.List();
            Assert.AreEqual(2, jobs.Count, "One record per shop for the same product: the job key includes the shop.");
            Assert.IsTrue(jobs.Where(j => j.ShopId == "shop-a").All(j => j.Status == SyncStatus.Pending), "Shop A's dispatchable job is untouched by shop B's failing run.");
            Assert.IsTrue(jobs.Where(j => j.ShopId == "shop-b").All(j => j.Status == SyncStatus.Failed));
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void MarketplaceMappingsResolveOnlyWithinTheirShopAndTwoShopsMayMapTheSameProductAndExternalId()
    {
        var root = NewRoot();
        try
        {
            var (_, id) = SeedProduct(root);
            var mappings = new MarketplaceMappingStore(root);
            mappings.Save(new("etsy", "shop-a", id, "ext-1"));

            Assert.IsNull(mappings.Find("etsy", "shop-b", id), "Shop B must not resolve shop A's listing for the shared product.");
            Assert.AreEqual("ext-1", mappings.Find("etsy", "shop-a", id)!.ExternalId);

            mappings.Save(new("etsy", "shop-b", id, "ext-1"));   // the same external id is legal in another shop
            Assert.AreEqual("ext-1", mappings.Find("etsy", "shop-b", id)!.ExternalId);
            Assert.AreEqual("ext-1", mappings.Find("etsy", "shop-a", id)!.ExternalId, "Shop B's mapping did not overwrite shop A's.");
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void AnOrderExportBuiltFromAShopScopedReadCarriesOnlyThatShopAndRoundTrips()
    {
        var root = NewRoot();
        try
        {
            var store = new OrdersStore(root);
            store.SaveManual(Order("shop-a", "o-1", "K1", "Kırmızı kupa"));
            store.SaveManual(Order("shop-a", "o-2", "K1", "Kırmızı kupa"));
            store.SaveManual(Order("shop-b", "o-1", "M1", "Mavi kupa"));

            var rows = store.ReadPage(shopId: "shop-a").Items.Select(o => new OrderTransferRow(o.Marketplace, o.ShopId, o.OrderId, o.Source, o.UpdatedAt, o.Total ?? 0m)).ToList();
            var xml = OrderTransferCodec.Export(rows);
            var restored = OrderTransferCodec.Import(xml);

            Assert.AreEqual(2, restored.Count);
            Assert.IsTrue(restored.All(r => r.ShopId == "shop-a"), "Nothing from shop B leaks into shop A's export.");
            CollectionAssert.AreEquivalent(new[] { "o-1", "o-2" }, restored.Select(r => r.OrderId).ToArray());
            Assert.IsFalse(xml.Contains("shop-b", StringComparison.Ordinal));
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void GlobalSearchAttributesEveryHitToItsShopInsteadOfMergingSameNumberedOrders()
    {
        var root = NewRoot();
        try
        {
            SeedProduct(root);
            var store = new OrdersStore(root);
            store.SaveManual(Order("shop-a", "o-1", "K1", "Kırmızı kupa"));
            store.SaveManual(Order("shop-b", "o-1", "M1", "Mavi kupa"));
            var search = new GlobalSearchIndexService(root);
            search.Rebuild();

            var hits = search.SearchAsync("o-1").GetAwaiter().GetResult().Where(h => h.Type == "Sipariş").ToList();
            Assert.AreEqual(2, hits.Count, "Two shops, two distinct orders under the same number: two hits, never one merged record.");
            CollectionAssert.AreEquivalent(new[] { "Mağaza: shop-a", "Mağaza: shop-b" }, hits.Select(h => h.Detail.Split(" · ")[0]).ToArray());
            var onlyB = search.SearchAsync("shop-b").GetAwaiter().GetResult().Where(h => h.Type == "Sipariş").ToList();
            Assert.AreEqual(1, onlyB.Count);
            StringAssert.Contains(onlyB.Single().Detail, "Mağaza: shop-b");
        }
        finally { Cleanup(root); }
    }
}
