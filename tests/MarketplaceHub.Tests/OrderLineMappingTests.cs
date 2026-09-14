using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #944 (ORDER LINES: product mapping review queue). A line links to a product only on certainty -- exactly one product
// by SKU, or a resolution an operator recorded for the store; a SKU on several products or on none goes to the review
// queue with its evidence; the operator's resolution becomes a rule the same evidence maps by from then on; a line
// that maps by itself later closes its review; nothing is linked silently; it all reads back after a restart.
[TestClass]
public sealed class OrderLineMappingTests
{
    static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
    static CatalogProduct Product(string id, string sku, string name) => new() { Id = id, Sku = sku, Name = name, Active = true };
    static OrderItem Line(string sku, string title) => new() { Sku = sku, Title = title, Quantity = 1 };
    static readonly CatalogProduct[] Products = { Product("p1", "SKU-1", "Mavi Kupa"), Product("p2", "SKU-2", "Kırmızı Kupa"), Product("p3", "sku-2", "Kırmızı Kupa (2)"), Product("p4", "SKU-4", "Yeşil Kupa") };

    [TestMethod]
    public void ALineLinksOnlyOnCertaintyAndCarriesItsEvidenceOtherwise()
    {
        var none = Array.Empty<OrderLineMappingRule>();
        var exact = OrderLineMapper.Judge("etsy", "S1", Line("sku-1", "Mavi Kupa"), Products, none);
        Assert.AreEqual((OrderLineJudgement.Exact, "p1", true), (exact.Kind, exact.ProductId, exact.Linked)); StringAssert.Contains(exact.Evidence, "birebir");
        var ambiguous = OrderLineMapper.Judge("etsy", "S1", Line("SKU-2", "Kırmızı Kupa"), Products, none);
        Assert.AreEqual((OrderLineJudgement.Ambiguous, "", false), (ambiguous.Kind, ambiguous.ProductId, ambiguous.Linked)); CollectionAssert.AreEquivalent(new[] { "p2", "p3" }, ambiguous.CandidateIds.ToArray()); StringAssert.Contains(ambiguous.Evidence, "2 üründe var; kesin eşleme yok");
        var missing = OrderLineMapper.Judge("etsy", "S1", Line("SKU-9", "yeşil kupa"), Products, none);
        Assert.AreEqual((OrderLineJudgement.Missing, "", false), (missing.Kind, missing.ProductId, missing.Linked)); CollectionAssert.AreEqual(new[] { "p4" }, missing.CandidateIds.ToArray()); StringAssert.Contains(missing.Evidence, "SKU SKU-9 katalogda yok; başlığı 1 ürünle aynı (kanıt, bağlantı değil)");
        var noSku = OrderLineMapper.Judge("etsy", "S1", Line("", "Bilinmeyen"), Products, none);
        Assert.AreEqual(OrderLineJudgement.Missing, noSku.Kind); StringAssert.Contains(noSku.Evidence, "satırda SKU yok; başlık eşleşmesi de yok");
        // A rule for the store resolves the ambiguous SKU and a title-only line; another store's rule does not count; a rule to a product that is gone does not link.
        var rules = new[] { new OrderLineMappingRule("etsy", "S1", "SKU-2", "", "p3", Now, ""), new OrderLineMappingRule("etsy", "S1", "", OrderLineMapper.TitleKey("Bilinmeyen"), "p4", Now, ""), new OrderLineMappingRule("ebay", "E1", "SKU-9", "", "p1", Now, ""), new OrderLineMappingRule("etsy", "S1", "SKU-7", "", "gone", Now, "") };
        Assert.AreEqual((OrderLineJudgement.Resolved, "p3"), (OrderLineMapper.Judge("etsy", "S1", Line("sku-2", "x"), Products, rules).Kind, OrderLineMapper.Judge("etsy", "S1", Line("sku-2", "x"), Products, rules).ProductId));
        Assert.AreEqual((OrderLineJudgement.Resolved, "p4"), (OrderLineMapper.Judge("etsy", "S1", Line("", "bilinmeyen"), Products, rules).Kind, OrderLineMapper.Judge("etsy", "S1", Line("", "bilinmeyen"), Products, rules).ProductId));
        Assert.AreEqual(OrderLineJudgement.Missing, OrderLineMapper.Judge("etsy", "S1", Line("SKU-9", "x"), Products, rules).Kind, "another store's rule is not this store's");
        Assert.AreEqual(OrderLineJudgement.Missing, OrderLineMapper.Judge("etsy", "S1", Line("SKU-7", "x"), Products, rules).Kind, "a rule to a product that no longer exists links nothing");
        Assert.AreEqual(OrderLineMapper.TitleKey(" Mavi,  KUPA! "), OrderLineMapper.TitleKey("mavi kupa"));
    }

    [TestMethod]
    public void TheScanQueuesAmbiguousAndMissingLinesAResolutionBecomesARuleAndItReadsBackAfterARestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "line-map-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var orders = new OrdersStore(root); var reviews = new OrderLineReviewStore(root);
            orders.SaveManual(new OrderSnapshot { Marketplace = "etsy", ShopId = "S1", OrderId = "o-1", RawStatus = "paid", Items = new List<OrderItem> { Line("SKU-1", "Mavi Kupa"), Line("SKU-2", "Kırmızı Kupa"), Line("SKU-9", "Yeşil Kupa"), Line("", "Bilinmeyen Ürün") } });
            orders.SaveManual(new OrderSnapshot { Marketplace = "etsy", ShopId = "S1", OrderId = "o-2", RawStatus = "paid", Items = new List<OrderItem> { Line("SKU-2", "Kırmızı Kupa") } });

            // The scan: one exact line, two ambiguous, two missing -- four reviews opened, nothing linked silently.
            var scan = reviews.Scan(orders.ReadAll(), Products, Now);
            Assert.AreEqual((5, 1, 0, 2, 2, 4, 0), (scan.Lines, scan.Exact, scan.Resolved, scan.Ambiguous, scan.Missing, scan.Opened, scan.Closed), scan.Words); StringAssert.Contains(scan.Words, "hiçbir satır sessizce bağlanmadı");
            var open = reviews.Open("etsy", "S1"); Assert.AreEqual(4, open.Count); Assert.IsTrue(open.All(r => r.ProductId.Length == 0));
            var ambiguous = open.Single(r => r.OrderId == "o-1" && r.LineIndex == 1); Assert.AreEqual(OrderLineJudgement.Ambiguous, ambiguous.Kind); CollectionAssert.AreEquivalent(new[] { "p2", "p3" }, ambiguous.CandidateIds.ToArray()); StringAssert.Contains(ambiguous.Words, "o-1 satır 2: SKU SKU-2");
            var missing = open.Single(r => r.OrderId == "o-1" && r.LineIndex == 2); Assert.AreEqual((OrderLineJudgement.Missing, "p4"), (missing.Kind, missing.CandidateIds.Single()));
            var rescan = reviews.Scan(orders.ReadAll(), Products, Now.AddMinutes(1));
            Assert.AreEqual((0, 4), (rescan.Opened, reviews.Open().Count), "a second scan refreshes the open reviews without opening them twice");

            // The operator resolves the ambiguous SKU-2 to p3: the review closes, a rule is remembered, and the other SKU-2 line resolves by the rule on the next scan.
            var resolved = reviews.Resolve(ambiguous.Id, "p3", Products, "tedarikçi doğruladı", Now.AddMinutes(2));
            Assert.AreEqual((OrderLineJudgement.Resolved, "p3", OrderLineReview.Closed), (resolved.Kind, resolved.ProductId, resolved.State));
            Assert.AreEqual(("SKU-2", "p3"), (reviews.Rules().Single().Sku, reviews.Rules().Single().ProductId));
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => reviews.Resolve(ambiguous.Id, "p2", Products, "", Now)).Message, "zaten çözülmüş");
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => reviews.Resolve(missing.Id, "nope", Products, "", Now)).Message, "Ürün bulunamadı");
            var again = reviews.Scan(orders.ReadAll(), Products, Now.AddMinutes(3));
            Assert.AreEqual((1, 2, 0, 2, 1), (again.Exact, again.Resolved, again.Ambiguous, again.Missing, again.Closed), again.Words);
            Assert.AreEqual(2, reviews.Open().Count, "the o-2 line closed by itself under the rule"); StringAssert.Contains(reviews.All().Single(r => r.OrderId == "o-2").Note, "kendiliğinden çözüldü");
            Assert.IsTrue(new AuditStore(root).List(20).Any(a => a.Action == "line-mapping-resolve" && a.Detail.Contains("SKU SKU-2", StringComparison.Ordinal) && a.Detail.Contains("tedarikçi doğruladı", StringComparison.Ordinal)));

            // A title-only resolution becomes a title rule; the rule's product disappearing reopens the review.
            var noSku = reviews.Open().Single(r => r.Sku.Length == 0); reviews.Resolve(noSku.Id, "p4", Products, "", Now.AddMinutes(4));
            Assert.AreEqual(OrderLineMapper.TitleKey("Bilinmeyen Ürün"), reviews.Rules().Single(r => r.Sku.Length == 0).TitleKey);
            var without = Products.Where(p => p.Id != "p4").ToArray(); var reopened = reviews.Scan(orders.ReadAll(), without, Now.AddMinutes(5));
            Assert.AreEqual(1, reopened.Opened, reopened.Words); StringAssert.Contains(reviews.All().Single(r => r.Id == noSku.Id).Evidence, "çözümün ürünü artık yok");

            // Restart: reviews and rules read back.
            SqliteConnection.ClearAllPools();
            var back = new OrderLineReviewStore(root);
            Assert.AreEqual(2, back.Rules().Count); Assert.AreEqual(OrderLineReview.Closed, back.Get(ambiguous.Id)!.State); Assert.AreEqual(2, back.Open().Count);
        }
        finally { Cleanup(root); }
    }

    static void Cleanup(string root)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
            catch (IOException) { Thread.Sleep(300); }
            catch (UnauthorizedAccessException) { Thread.Sleep(300); }
        }
    }
}
