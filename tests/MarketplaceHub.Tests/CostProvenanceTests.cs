using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #922 (PRICING: supplier cost provenance). The cost carries where it came from -- the source with its
// configuration revision, the import run and the moment, and the currency -- or the operator's entry over a feed's
// value, which keeps naming that value, its source, revision and moment through further edits; a cost nobody
// entered is missing, never a margin against zero; it all reads back after a restart; and every net margin -- the
// card, the simulator, the pricing chain, the provenance summary, the inspect drawer -- names the cost's origin.
[TestClass]
public sealed class CostProvenanceTests
{
    static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);

    static XmlSource Source(string costCurrency) => new() { Id = "src-a", Name = "Tedarikçi A", Location = "https://feed.example.com/a.xml?key=abc123", ItemPath = "/p", Fields = new Dictionary<string, string> { ["Sku"] = "s", ["Name"] = "n" }, CostCurrency = costCurrency };
    static CatalogProduct Row(string sku, decimal cost, string currency, decimal price = 0) => new() { SourceId = "src-a", SourceKind = "xml", Sku = sku, Name = "Ürün " + sku, Price = price, Currency = "TRY", Cost = cost, CostCurrency = currency, Stock = 3, Active = true };

    static string ImportOnce(CatalogStore store, XmlRunStore runs, XmlSource source, IReadOnlyList<CatalogProduct> rows, DateTime observedUtc)
    {
        var run = runs.Start(source.Id, "h-" + Guid.NewGuid().ToString("N")[..8], TimeSpan.FromMinutes(5), source.ConfigRevision);
        store.Import(source, rows, CancellationToken.None, new XmlImportContext { RunId = run, SourceRevision = source.ConfigRevision, ObservedAtUtc = observedUtc });
        runs.Complete(run, new ImportSummary(rows.Count, 0, 0));
        return run;
    }

    [TestMethod]
    public void TheCostNamesItsSourceRevisionCurrencyAndMomentThroughASourceChangeAManualOverrideAndARestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "cost-prov-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CatalogStore(root); var runs = new XmlRunStore(root); var a = Source("USD"); store.SaveSource(a); var r1 = a.ConfigRevision;
            XmlSource? ById(string id) => id == "src-a" ? a : null;

            // A feed's value: the source, its revision, the run, the moment and the currency.
            var run1 = ImportOnce(store, runs, a, new[] { Row("SKU-1", 10, "USD") }, Now.AddHours(-2));
            var feed = CostProvenance.Resolve(store.Products().Single(), ById, Now);
            Assert.AreEqual(CostProvenanceView.Feed, feed.State); Assert.AreEqual(10m, feed.Cost); Assert.AreEqual("USD", feed.Currency); Assert.AreEqual("Tedarikçi A", feed.SourceName); Assert.AreEqual(r1, feed.SourceRevision); Assert.AreEqual(run1, feed.RunId); Assert.AreEqual(Now.AddHours(-2), feed.ObservedUtc); Assert.IsFalse(feed.ManualOverride); Assert.IsFalse(feed.OperatorOwned);
            StringAssert.Contains(feed.Words, "10 USD"); StringAssert.Contains(feed.Words, "Tedarikçi A"); StringAssert.Contains(feed.Words, $"rev. {r1}"); StringAssert.Contains(feed.Words, "2 sa önce");
            Assert.IsFalse(feed.Words.Contains("example.com", StringComparison.Ordinal) || feed.Words.Contains("abc123", StringComparison.Ordinal), "no address, no credential");

            // A source change: the configuration moves on and the next run rewrites the cost -- the new revision, run and moment.
            a.MarkupPercent = 5; store.SaveSource(a); var r2 = a.ConfigRevision; Assert.IsTrue(r2 > r1, "a saved configuration is a new revision");
            var run2 = ImportOnce(store, runs, a, new[] { Row("SKU-1", 12, "USD") }, Now.AddHours(-1));
            var changed = CostProvenance.Resolve(store.Products().Single(), ById, Now);
            Assert.AreEqual(CostProvenanceView.Feed, changed.State); Assert.AreEqual(12m, changed.Cost); Assert.AreEqual(r2, changed.SourceRevision); Assert.AreEqual(run2, changed.RunId); StringAssert.Contains(changed.Words, "1 sa önce");

            // A manual override keeps naming the feed's last value, its source, revision and moment -- through a second edit too.
            var product = store.Products().Single(); product.Cost = 15; store.SaveProduct(product);
            var over = CostProvenance.Resolve(store.Products().Single(), ById, Now);
            Assert.AreEqual(CostProvenanceView.Override, over.State); Assert.IsTrue(over.ManualOverride); Assert.IsTrue(over.OperatorOwned); Assert.AreEqual(15m, over.Cost); Assert.AreEqual(12m, over.SupersededCost); Assert.AreEqual(r2, over.SupersededRevision); Assert.AreEqual("Tedarikçi A", over.SupersededSourceName); Assert.AreEqual(Now.AddHours(-1), over.SupersededObservedUtc);
            StringAssert.Contains(over.Words, "15 USD"); StringAssert.Contains(over.Words, "elle"); StringAssert.Contains(over.Words, "kaynağın son değeri 12 USD"); StringAssert.Contains(over.Words, "yeniden yazabilir");
            product = store.Products().Single(); product.Cost = 16; store.SaveProduct(product);
            var again = CostProvenance.Resolve(store.Products().Single(), ById, Now); Assert.AreEqual(CostProvenanceView.Override, again.State); Assert.AreEqual(16m, again.Cost); Assert.AreEqual(12m, again.SupersededCost, "the feed's last value survives a second manual edit");
            StringAssert.Contains(CostProvenance.Resolve(store.Products().Single(), _ => null, Now).Words, "kaynak silinmiş"); StringAssert.Contains(CostProvenance.Resolve(store.Products().Single(), null, Now).Words, "kaynak src-a");

            // Missing and unrecorded: said as such, never guessed.
            var missing = CostProvenance.Resolve(new CatalogProduct { Sku = "M", Name = "Manuel", Price = 10, Cost = 0 }, ById, Now);
            Assert.AreEqual(CostProvenanceView.Missing, missing.State); Assert.AreEqual("Girilmemiş", missing.OriginLabel); StringAssert.Contains(missing.Words, "maliyet yok"); Assert.IsFalse(missing.OperatorOwned);
            var legacy = CostProvenance.Resolve(new CatalogProduct { Sku = "L", Name = "Eski", Cost = 7, CostCurrency = "TRY", SourceId = "src-a", SourceKind = "xml" }, ById, Now);
            Assert.AreEqual(CostProvenanceView.Unrecorded, legacy.State); StringAssert.Contains(legacy.Words, "7 TRY"); StringAssert.Contains(legacy.Words, "kökeni kaydedilmedi");
            var manual = CostProvenance.Resolve(new CatalogProduct { Sku = "N", Name = "Elle", Cost = 9, CostCurrency = "TRY", FieldOrigins = new Dictionary<string, FieldOrigin> { ["Cost"] = new() { Kind = FieldProvenance.ManualKind, ObservedUtc = Now.AddMinutes(-5) } } }, ById, Now);
            Assert.AreEqual(CostProvenanceView.Manual, manual.State); Assert.IsFalse(manual.ManualOverride); StringAssert.Contains(manual.Words, "9 TRY · elle · 5 dk önce"); Assert.IsFalse(manual.Words.Contains("yeniden yazabilir", StringComparison.Ordinal), "a product no feed owns is not rewritten by an import");

            // Restart: the override, the superseded feed value and its revision read back from the record.
            SqliteConnection.ClearAllPools();
            var fresh = CostProvenance.Resolve(new CatalogStore(root).Products().Single(), ById, Now);
            Assert.AreEqual(CostProvenanceView.Override, fresh.State); Assert.AreEqual(16m, fresh.Cost); Assert.AreEqual(12m, fresh.SupersededCost); Assert.AreEqual(r2, fresh.SupersededRevision); Assert.AreEqual("USD", fresh.Currency);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void EveryNetMarginNamesTheCostOriginAndNoneIsComputedAgainstAMissingCost()
    {
        var root = Path.Combine(Path.GetTempPath(), "cost-prov-margin-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CatalogStore(root); var runs = new XmlRunStore(root); var a = Source("TRY"); store.SaveSource(a);
            XmlSource? ById(string id) => id == "src-a" ? a : null;
            ImportOnce(store, runs, a, new[] { Row("SKU-1", 100, "TRY", price: 150), Row("SKU-0", 0, "TRY", price: 50) }, Now.AddHours(-2));
            var products = store.Products(); var priced = products.Single(p => p.Sku == "SKU-1"); var costless = products.Single(p => p.Sku == "SKU-0"); var beforeOverride = store.Products().Single(p => p.Sku == "SKU-1"); // a separate read: the save below stamps the instance it is handed
            store.SavePricePolicy(new PricePolicy { Channel = "local", Shop = "s1", Formula = "x*2", Currency = "TRY", TryPerUnit = 1, Enabled = true, CommissionPercent = 10, EstimatedShippingTry = 5, TransactionCostTry = 0, VatRatePercent = 0 });

            // The pricing chain: the preview names the feed's cost origin; after an override it names the override and the feed's last value; a missing cost is refused by name.
            var preview = store.PreviewPrice("local", "s1", priced.Id);
            Assert.AreEqual(200m, preview.Price); StringAssert.Contains(preview.CostOrigin, "100 TRY"); StringAssert.Contains(preview.CostOrigin, "Tedarikçi A"); StringAssert.Contains(preview.CostOrigin, "rev.");
            priced.Cost = 120; store.SaveProduct(priced); priced = store.Products().Single(p => p.Sku == "SKU-1");
            var overridden = store.PreviewPrice("local", "s1", priced.Id);
            Assert.AreEqual(240m, overridden.Price); StringAssert.Contains(overridden.CostOrigin, "elle"); StringAssert.Contains(overridden.CostOrigin, "kaynağın son değeri 100 TRY");
            var refused = Assert.ThrowsException<InvalidOperationException>(() => store.PreviewPrice("local", "s1", costless.Id));
            StringAssert.Contains(refused.Message, "maliyeti girilmemiş");

            // The card: the origin beside the approximate margin; a missing cost is a warning and no margin, not a 100% margin.
            var card = ProductPriceSummary.Build(priced, Now, ById);
            StringAssert.Contains(card.CostOrigin, "elle"); StringAssert.Contains(card.CostOrigin, "kaynağın son değeri 100 TRY"); Assert.AreEqual(ProductPriceSummary.Healthy, card.MarginLevel, "150 on a 120 cost is a 20% approximate margin, above the 10% thin line");
            var noCost = ProductPriceSummary.Build(costless, Now, ById);
            CollectionAssert.Contains(noCost.Warnings.ToList(), "Alış fiyatı girilmemiş; kâr hesaplanamaz."); Assert.AreEqual(ProductPriceSummary.Unknown, noCost.MarginLevel); StringAssert.Contains(noCost.CostOrigin, "maliyet yok");

            // The simulator: the words ride along in the input and the result; a zero cost is COST_MISSING, never a contribution.
            var input = new ProfitabilityInput("SKU-1", "local", 240m, 120m, 0m, 10m, 5m, 0m, "TRY", Now, Now, CostProvenance: card.CostOrigin);
            var result = ProfitabilitySimulator.Simulate(input, Now);
            Assert.AreEqual("OK", result.Status); Assert.AreEqual(card.CostOrigin, result.Input.CostProvenance);
            var zero = ProfitabilitySimulator.Simulate(input with { Cost = 0m }, Now);
            Assert.AreEqual("COST_MISSING", zero.Status); Assert.AreEqual(0m, zero.NetContribution); Assert.AreEqual(0m, zero.MarginPercent);

            // The provenance summary and the inspect drawer name it too.
            var provenance = ProductProvenance.Build(priced, a, Now, ById);
            var costRow = provenance.Rows.Single(r => r.Field == "Alış"); Assert.IsTrue(costRow.IsOperatorOwned); StringAssert.Contains(costRow.Origin, "Elle"); StringAssert.Contains(costRow.Detail, "kaynağın son değeri 100 TRY");
            var feedRow = ProductProvenance.Build(beforeOverride, a, Now, ById).Rows.Single(r => r.Field == "Alış"); Assert.IsFalse(feedRow.IsOperatorOwned); Assert.AreEqual("Tedarikçi A", feedRow.Origin);
            var inspect = ProductQuickInspect.Build(priced, Array.Empty<SyncJob>(), Now, new[] { a });
            var inspectRow = inspect.Rows.Single(r => r.Label == "Alış kökeni"); Assert.AreEqual("Fiyat", inspectRow.Section); StringAssert.Contains(inspectRow.Value, "elle"); StringAssert.Contains(inspectRow.Value, "100 TRY");
            StringAssert.Contains(ProductQuickInspect.Build(costless, Array.Empty<SyncJob>(), Now, new[] { a }).Rows.Single(r => r.Label == "Alış kökeni").Value, "maliyet yok");
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
