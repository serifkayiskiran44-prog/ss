using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #791 (TEST: End-to-end data integrity regression suite). One deterministic chain through the real stores and
// services -- supplier feed import → product master → price policy / stock decision → order, shipment
// observation, return reconciliation → report and dashboard -- then the same chain under partial failure,
// restart, duplicate input, wrong-store access and secret redaction. Every step is the production entry point;
// no store is touched with raw SQL.
[TestClass]
public sealed class EndToEndIntegritySuiteTests
{
    static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
    static string NewRoot() => Path.Combine(Path.GetTempPath(), "e2e-integrity-" + Guid.NewGuid().ToString("N"));
    static void Cleanup(string root) { DashboardDataService.InvalidateCache(root); SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }

    sealed record Chain(string Root, CatalogStore Catalog, OrdersStore Orders, XmlSource Source, string IdA, string IdB, OrderSnapshot Order);

    static CatalogProduct Product(string sku, decimal price, decimal cost, int stock) => new() { SourceId = "sup", Sku = sku, Name = "Ürün " + sku, Price = price, Cost = cost, CostCurrency = "TRY", Stock = stock, Active = true };

    // The happy path, as far as the return; each test continues from here.
    static Chain Run()
    {
        var root = NewRoot();
        var catalog = new CatalogStore(root);
        var source = new XmlSource { Id = "sup", Name = "Tedarikçi" };
        catalog.Import(source, new[] { Product("A", 100m, 60m, 10), Product("B", 80m, 40m, 5) }, CancellationToken.None, new XmlImportContext { CompleteFeed = true, FeedHash = "feed-1" });
        var idA = catalog.Products().Single(p => p.Sku == "A").Id; var idB = catalog.Products().Single(p => p.Sku == "B").Id;
        catalog.SavePricePolicy(new PricePolicy { Channel = "local", Shop = "shop-a", Formula = "x*2", Currency = "TRY", TryPerUnit = 1, Enabled = true, CommissionPercent = 10m, EstimatedShippingTry = 5m, TransactionCostTry = 0m, VatRatePercent = 0m });
        new MarketplaceMappingStore(root).Save(new("etsy", "shop-a", idA, "ext-A"));

        var orders = new OrdersStore(root);
        orders.SaveManual(new OrderSnapshot { Marketplace = "Yerel", ShopId = "shop-a", OrderId = "o-1", RawStatus = "Açık", Total = 240m, Currency = "TRY", Items = { new OrderItem { Title = "Ürün A", Sku = "A", Quantity = 2 } } });
        var order = orders.Find("Yerel", "shop-a", "o-1")!;
        var stock = new OrderStockDecisionService(catalog);
        stock.ApplyApproved(stock.CreatePreview(order), approved: true);
        order.Shipments.Add(new OrderShipment { Id = "pkg-1", Carrier = "aras", TrackingNumber = "TN-1", State = "InTransit" });
        orders.SaveManual(order);
        order = orders.Find("Yerel", "shop-a", "o-1")!;
        catalog.ApplyOrderReturn(order, new OrderReturnEvent("Yerel", "shop-a", "o-1", "ret-1", "A", 1, 120m, "TRY", Now), approved: true);
        return new(root, catalog, orders, source, idA, idB, order);
    }

    [TestMethod]
    public void TheHappyPathKeepsIdentityAndProvenanceFromFeedToReport()
    {
        var c = Run();
        try
        {
            // Identity: a second feed with a changed price updates the same product, never a new one.
            var summary = c.Catalog.Import(c.Source, new[] { Product("A", 110m, 60m, 10), Product("B", 80m, 40m, 5) }, CancellationToken.None, new XmlImportContext { CompleteFeed = true, FeedHash = "feed-2" });
            Assert.AreEqual(0, summary.Added, "No new identities for known SKUs.");
            Assert.AreEqual(2, c.Catalog.Products().Count);
            var a = c.Catalog.Products().Single(p => p.Sku == "A");
            Assert.AreEqual(c.IdA, a.Id, "Re-importing the SKU keeps the product identity.");
            Assert.AreEqual(110m, a.Price);
            // Stock provenance: the supplier feed is the source of truth for stock unless the product is stock-locked;
            // the order's deduction (10 → 8) and the return (→ 9) are transient local movements that the next feed
            // overwrites, while the receipt and the return ledger below keep the full history of what happened.
            Assert.AreEqual(10, a.Stock, "The feed's stock overwrites the transient local movement; the movements themselves stay on record.");
            Assert.AreEqual("xml", a.StockSource);

            // Provenance: the price preview carries the cost it priced from; the feed state names the feed it came from.
            var preview = c.Catalog.PreviewPrice("local", "shop-a", c.IdA);
            Assert.AreEqual(120m, preview.Price); Assert.AreEqual(60m, preview.CostTry); Assert.AreEqual(120m, preview.FormulaPriceTry);
            Assert.AreEqual("feed-2", c.Catalog.Sources().Single().LastSuccessfulFeedHash);

            // Order → shipment → return → report/dashboard all agree on one order and one return.
            var order = c.Orders.Find("Yerel", "shop-a", "o-1")!;
            Assert.AreEqual("Aras Kargo", order.Shipments.Single().Carrier, "The shipment went through the same normalizer as an API shipment.");
            Assert.IsNotNull(c.Catalog.GetOrderStockStatus("Yerel", "shop-a", "o-1"));
            Assert.AreEqual(1, c.Catalog.PreviewOrderReturn(order, new OrderReturnEvent("Yerel", "shop-a", "o-1", "probe", "A", 1, 0m, "TRY", Now)).ReturnedBefore);
            var report = ProductOrderReport.Build("A", c.Orders.ReadPage(shopId: "shop-a").Items);
            Assert.AreEqual(1, report.OrderCount); Assert.AreEqual(2, report.Quantity); Assert.AreEqual("Yerel / shop-a", report.LatestChannelShop);
            var dashboard = new DashboardDataService(c.Root).Load(bypassCache: true);
            Assert.AreEqual(2, dashboard.TotalProducts); Assert.AreEqual(1, dashboard.OpenOrders); Assert.AreEqual(0, dashboard.StockWaitingOrders, "The order's stock decision was applied, so nothing waits.");
        }
        finally { Cleanup(c.Root); }
    }

    [TestMethod]
    public void APartialFailureInTheMiddleOfTheChainLeavesEveryEarlierStepIntact()
    {
        var c = Run();
        try
        {
            // The next feed fails on its third row (duplicate SKU): nothing of it may land.
            Assert.ThrowsException<InvalidOperationException>(() => c.Catalog.Import(c.Source, new[] { Product("A", 999m, 60m, 1), Product("B", 999m, 40m, 1), Product("A", 1m, 1m, 1) }, CancellationToken.None, new XmlImportContext { CompleteFeed = true, FeedHash = "feed-broken" }));
            var a = c.Catalog.Products().Single(p => p.Sku == "A");
            Assert.AreEqual(c.IdA, a.Id); Assert.AreEqual(100m, a.Price); Assert.AreEqual(9, a.Stock);
            Assert.AreEqual("feed-1", c.Catalog.Sources().Single().LastSuccessfulFeedHash, "The source still points at the last good feed.");

            // A blocked return (over-return) changes nothing downstream either.
            Assert.ThrowsException<InvalidOperationException>(() => c.Catalog.ApplyOrderReturn(c.Order, new OrderReturnEvent("Yerel", "shop-a", "o-1", "ret-over", "A", 5, 0m, "TRY", Now), approved: true));
            Assert.AreEqual(9, c.Catalog.Products().Single(p => p.Sku == "A").Stock);
            Assert.AreEqual(120m, c.Catalog.PreviewPrice("local", "shop-a", c.IdA).Price, "Pricing still works on the untouched data.");
            Assert.AreEqual(1, c.Orders.ReadPage(shopId: "shop-a").Total);
        }
        finally { Cleanup(c.Root); }
    }

    [TestMethod]
    public void EverythingTheChainWroteIsStillThereForFreshStoreInstancesAfterARestart()
    {
        var c = Run();
        try
        {
            SqliteConnection.ClearAllPools();   // the process is gone; only the files remain
            var catalog = new CatalogStore(c.Root); var orders = new OrdersStore(c.Root);
            Assert.AreEqual(2, catalog.Products().Count);
            Assert.AreEqual(c.IdA, catalog.Products().Single(p => p.Sku == "A").Id);
            Assert.IsNotNull(catalog.GetPricePolicy("local", "shop-a"));
            Assert.AreEqual("ext-A", new MarketplaceMappingStore(c.Root).Find("etsy", "shop-a", c.IdA)!.ExternalId);
            var order = orders.Find("Yerel", "shop-a", "o-1")!;
            Assert.AreEqual("InTransit", order.Shipments.Single().State);
            Assert.IsTrue(order.Shipments.Single().Events.Count > 0, "Observation history is persisted, not held in memory.");
            Assert.IsNotNull(catalog.GetOrderStockStatus("Yerel", "shop-a", "o-1"));
            var recon = catalog.PreviewOrderReturn(order, new OrderReturnEvent("Yerel", "shop-a", "o-1", "probe", "A", 1, 0m, "TRY", Now));
            Assert.AreEqual(1, recon.ReturnedBefore); Assert.AreEqual(120m, recon.RefundedBefore);
            Assert.IsTrue(new OrderStockDecisionService(catalog).CreatePreview(order).AlreadyApplied);
        }
        finally { Cleanup(c.Root); }
    }

    [TestMethod]
    public void DuplicateInputAtEveryStepIsRecognisedAndAppliedExactlyOnce()
    {
        var c = Run();
        try
        {
            var again = c.Catalog.Import(c.Source, new[] { Product("A", 100m, 60m, 10), Product("B", 80m, 40m, 5) }, CancellationToken.None, new XmlImportContext { CompleteFeed = true, FeedHash = "feed-1" });
            Assert.IsTrue(again.AlreadyApplied, "The same feed hash is not applied twice.");
            Assert.AreEqual(9, c.Catalog.Products().Single(p => p.Sku == "A").Stock);

            var stock = new OrderStockDecisionService(c.Catalog);
            Assert.IsTrue(stock.ApplyApproved(stock.CreatePreview(c.Order), approved: true).AlreadyApplied, "A second stock decision for the same order deducts nothing.");
            Assert.AreEqual(9, c.Catalog.Products().Single(p => p.Sku == "A").Stock);

            Assert.AreEqual("DUPLICATE", c.Catalog.ApplyOrderReturn(c.Order, new OrderReturnEvent("Yerel", "shop-a", "o-1", "ret-1", "A", 1, 120m, "TRY", Now), approved: true).Status);
            Assert.AreEqual(9, c.Catalog.Products().Single(p => p.Sku == "A").Stock);

            c.Orders.SaveBatch([c.Order.Copy()]);   // the same snapshot again, same UpdatedAt: ignored
            Assert.AreEqual(1, c.Orders.ReadAll().Count);
            Assert.AreEqual(1, c.Orders.Find("Yerel", "shop-a", "o-1")!.Shipments.Count);
        }
        finally { Cleanup(c.Root); }
    }

    [TestMethod]
    public void AnotherShopSeesNothingOfTheChainAndCannotActOnIt()
    {
        var c = Run();
        try
        {
            Assert.IsNull(c.Orders.Find("Yerel", "shop-b", "o-1"));
            Assert.AreEqual(0, c.Orders.ReadPage(shopId: "shop-b").Total);
            Assert.IsNull(c.Catalog.GetOrderStockStatus("Yerel", "shop-b", "o-1"));
            Assert.IsNull(new MarketplaceMappingStore(c.Root).Find("etsy", "shop-b", c.IdA));
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => c.Catalog.PreviewPrice("local", "shop-b", c.IdA)).Message, "fiyat kuralı");
            var foreign = c.Catalog.PreviewOrderReturn(c.Order, new OrderReturnEvent("Yerel", "shop-b", "o-1", "ret-b", "A", 1, 0m, "TRY", Now));
            Assert.AreEqual("BLOCKED", foreign.Status);
            CollectionAssert.Contains(foreign.Reasons.ToList(), "ORDER_SCOPE_MISMATCH");
            Assert.AreEqual(0, ProductOrderReport.Build("A", c.Orders.ReadPage(shopId: "shop-b").Items).OrderCount);
        }
        finally { Cleanup(c.Root); }
    }

    [TestMethod]
    public void SecretsThatEnterTheChainThroughErrorsAndRecordsNeverComeBackOut()
    {
        var c = Run();
        try
        {
            const string bearer = "xyz789bearer"; const string token = "abc123secret"; const string apiKey = "k12345key";

            // A failed sync job stores its error; the value of the token must not survive, not just its name.
            var job = new SyncStore(c.Root).EnqueueFailed(new SyncRequest("local", "price", c.IdA, c.IdA + ":1:error", "shop-a"), $"Etsy 401: access_token={token} Authorization: Bearer {bearer}");
            Assert.IsFalse(job.LastError.Contains(token, StringComparison.Ordinal), "LastError leaked the access token value: " + job.LastError);
            Assert.IsFalse(job.LastError.Contains(bearer, StringComparison.Ordinal), "LastError leaked the bearer value: " + job.LastError);

            // An order exception message is redacted on save.
            var exceptions = new OrderExceptionStore(c.Root);
            var record = exceptions.Save(new OrderExceptionRecord { Marketplace = "Yerel", ShopId = "shop-a", OrderId = "o-1", Type = "Return", EventKey = "return:test", Message = $"Authorization: Bearer {bearer} https://api.example/x?api_key={apiKey}" });
            Assert.IsFalse(record.Message.Contains(bearer, StringComparison.Ordinal), record.Message);
            Assert.IsFalse(record.Message.Contains(apiKey, StringComparison.Ordinal), record.Message);

            // The global search index sanitizes what it indexes.
            c.Catalog.Import(c.Source, new[] { Product("A", 100m, 60m, 10), new CatalogProduct { SourceId = "sup", Sku = "B", Name = $"Ürün B Authorization: Bearer {bearer}", Price = 80m, Cost = 40m, CostCurrency = "TRY", Stock = 5, Active = true } }, CancellationToken.None, new XmlImportContext { CompleteFeed = true, FeedHash = "feed-3" });
            var search = new GlobalSearchIndexService(c.Root); search.Rebuild();
            var hits = search.SearchAsync("Ürün B").GetAwaiter().GetResult();
            Assert.IsTrue(hits.Count > 0);
            Assert.IsTrue(hits.All(h => !h.Title.Contains(bearer, StringComparison.Ordinal) && !h.Detail.Contains(bearer, StringComparison.Ordinal)), "The search index leaked a bearer token.");
        }
        finally { Cleanup(c.Root); }
    }
}
