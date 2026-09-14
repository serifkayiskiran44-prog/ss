using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #939 (STOCK: anomaly explanation details). Every block of the dropship anomaly gate -- and every near miss -- is
// explained and kept as a safe diagnostic: the triggering values, the baseline, the thresholds, the source's age, the
// run's correlation id; the real import writes it and names it in the refusal; nothing of a product or an address
// gets in; it all reads back after a restart.
[TestClass]
public sealed class AnomalyDiagnosticsTests
{
    static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
    static FeedRunMetrics Metrics(int count, int zero, decimal average) => new("s", count, zero, average, 0, 0, Now);

    [TestMethod]
    public void ASpikeADropAMassZeroAndANearMissAreExplainedWithValuesBaselineThresholdsSourceAgeAndCorrelation()
    {
        var guard = new DropshipAnomalyGuard(); var profile = new AnomalyProfile(); var baseline = Metrics(100, 5, 100m);

        // Spike: 60 % more products than the baseline, blocked; the words carry the numbers, the threshold, the source age and the run.
        var spike = AnomalyDiagnostics.Explain(guard.Evaluate(Metrics(160, 5, 100m), baseline), profile, Now.AddHours(-10), "run-1", Now)!;
        Assert.AreEqual((AnomalyDiagnostic.Blocked, 160, 100, 60m, 30m, "run-1"), (spike.Decision, spike.CurrentCount, spike.BaselineCount, spike.CountDeltaPercent, spike.MaxCountDeltaPercent, spike.CorrelationId)); Assert.AreEqual(TimeSpan.FromHours(10), spike.SourceAge);
        CollectionAssert.AreEqual(new[] { "PRODUCT_COUNT_SPIKE" }, spike.Reasons.ToArray()); StringAssert.Contains(spike.Words, "engellendi: PRODUCT_COUNT_SPIKE: ürün sayısı 100 → 160 (%60 fark, sınır %30)"); StringAssert.Contains(spike.Words, "kaynak yaşı 10 sa"); StringAssert.Contains(spike.Words, "korelasyon run-1");

        // Drop: 40 left of 100 -- a spike in the delta and missing products, both explained; a source never read says so.
        var drop = AnomalyDiagnostics.Explain(guard.Evaluate(Metrics(40, 2, 100m), baseline), profile, null, "", Now)!;
        CollectionAssert.AreEquivalent(new[] { "PRODUCT_COUNT_SPIKE", "MISSING_PRODUCTS" }, drop.Reasons.ToArray()); StringAssert.Contains(drop.Words, "MISSING_PRODUCTS: ürün sayısı 100 → 40 (%60 fark"); StringAssert.Contains(drop.Words, "kaynak hiç okunmamış"); Assert.IsNull(drop.SourceAge); Assert.IsFalse(drop.Words.Contains("korelasyon", StringComparison.Ordinal));

        // Mass zero: 90 of 100 without stock, blocked at the 80 % threshold.
        var zero = AnomalyDiagnostics.Explain(guard.Evaluate(Metrics(100, 90, 100m), baseline), profile, Now.AddMinutes(-30), "run-3", Now)!;
        Assert.AreEqual((AnomalyDiagnostic.Blocked, 90m, 80m), (zero.Decision, zero.ZeroStockPercent, zero.MaxZeroStockPercent)); StringAssert.Contains(zero.Words, "MASS_ZERO_STOCK: sıfır stoklu ürün %90 (90/100; sınır %80)"); StringAssert.Contains(zero.Words, "kaynak yaşı 30 dk");

        // A near miss: the average price up 22 % against a 25 % threshold -- not blocked, recorded as a warning; a quiet run yields nothing.
        var warn = AnomalyDiagnostics.Explain(guard.Evaluate(Metrics(100, 5, 122m), baseline), profile, Now.AddHours(-1), "run-4", Now)!;
        Assert.AreEqual((AnomalyDiagnostic.Warn, 22m, 25m, 0), (warn.Decision, warn.PriceDeltaPercent, warn.MaxPriceDeltaPercent, warn.Reasons.Count)); StringAssert.Contains(warn.Words, "uyarı (içe aktarım sürdü): ortalama fiyat 100 → 122 (%22 fark, sınır %25'e yakın)");
        Assert.IsNull(AnomalyDiagnostics.Explain(guard.Evaluate(Metrics(105, 5, 101m), baseline), profile, Now, "run-5", Now)); Assert.IsNull(AnomalyDiagnostics.Explain(guard.Evaluate(Metrics(500, 5, 1m), null), profile, Now, "first", Now), "a first run has no baseline to judge against");
    }

    [TestMethod]
    public void TheRealImportKeepsTheDiagnosticNamesItInTheRefusalRedactsItAndReadsItBackAfterARestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "anomaly-diag-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CatalogStore(root); var runs = new XmlRunStore(root);
            var source = new XmlSource { Id = "src", Name = "Tedarikçi", Location = "https://user:pw@feed.example/x.xml?key=zzz", ItemPath = "/p", Fields = new Dictionary<string, string> { ["Sku"] = "s", ["Name"] = "n" }, IntervalMinutes = 30, LastSuccessfulFeedUtc = DateTime.UtcNow.AddHours(-10) };
            store.SaveSource(source);
            List<CatalogProduct> Rows(int count, decimal price, int zeroStock = 0) => Enumerable.Range(1, count).Select(i => new CatalogProduct { SourceId = "src", SourceKind = "xml", Sku = "SKU-" + i, Name = "Ürün " + i, Price = price, Currency = "TRY", Cost = 4, Stock = i <= zeroStock ? 0 : 5, Active = true }).ToList();
            string Import(List<CatalogProduct> rows)
            {
                var run = runs.Start("src", "h-" + Guid.NewGuid().ToString("N")[..8], TimeSpan.FromMinutes(5), source.ConfigRevision);
                store.Import(source, rows, CancellationToken.None, new XmlImportContext { RunId = run, SourceRevision = source.ConfigRevision, ObservedAtUtc = DateTime.UtcNow });
                runs.Complete(run, new ImportSummary(rows.Count, 0, 0)); return run;
            }
            Import(Rows(10, 10m)); Assert.AreEqual(0, store.AnomalyDiagnosticsFor("src").Count, "a first run has no baseline; nothing to explain");

            // A spike: blocked, the diagnostic kept and named in the refusal with its correlation id.
            var run = runs.Start("src", "h-spike", TimeSpan.FromMinutes(5), source.ConfigRevision);
            var refused = Assert.ThrowsException<InvalidOperationException>(() => store.Import(source, Rows(16, 10m), CancellationToken.None, new XmlImportContext { RunId = run, SourceRevision = source.ConfigRevision, ObservedAtUtc = DateTime.UtcNow }));
            StringAssert.Contains(refused.Message, "Dropshipping anomaly gate blocked import: PRODUCT_COUNT_SPIKE"); StringAssert.Contains(refused.Message, "tanı #"); StringAssert.Contains(refused.Message, "korelasyon " + run);
            runs.Fail(run, "blocked by the gate"); // the run's lease must end before the source may start another
            var blocked = store.AnomalyDiagnosticsFor("src").Single();
            Assert.AreEqual((AnomalyDiagnostic.Blocked, 16, 10, 60m, 30m, run), (blocked.Decision, blocked.CurrentCount, blocked.BaselineCount, blocked.CountDeltaPercent, blocked.MaxCountDeltaPercent, blocked.CorrelationId));
            Assert.IsTrue(blocked.SourceAge is { } age && age > TimeSpan.FromHours(9) && age < TimeSpan.FromHours(11), "the source's last successful read is ten hours old");
            Assert.AreEqual(10, store.Products().Count, "the blocked run wrote nothing");

            // A near miss: prices up 22 % -- the import proceeds and a warning is kept; then a mass zero blocks.
            var warnRun = Import(Rows(10, 12.2m));
            var warn = store.AnomalyDiagnosticsFor("src").First(); Assert.AreEqual((AnomalyDiagnostic.Warn, 22m, warnRun), (warn.Decision, warn.PriceDeltaPercent, warn.CorrelationId)); Assert.AreEqual(12.2m, store.Products().First().Price, "the warned run was applied");
            Assert.ThrowsException<InvalidOperationException>(() => store.Import(source, Rows(10, 12.2m, zeroStock: 9), CancellationToken.None, new XmlImportContext { RunId = "h-zero", SourceRevision = source.ConfigRevision, ObservedAtUtc = DateTime.UtcNow }));
            var all = store.AnomalyDiagnosticsFor("src"); Assert.AreEqual(3, all.Count); Assert.AreEqual("MASS_ZERO_STOCK", all[0].Reasons.Single()); Assert.AreEqual((90m, 9), (all[0].ZeroStockPercent, all[0].CurrentZeroStock));

            // Redaction: no address, no credential, no product name in any diagnostic.
            foreach (var d in all) Assert.IsFalse(d.Words.Contains("pw", StringComparison.Ordinal) || d.Words.Contains("feed.example", StringComparison.Ordinal) || d.Words.Contains("zzz", StringComparison.Ordinal) || d.Words.Contains("Ürün", StringComparison.Ordinal) || d.Words.Contains("SKU-", StringComparison.Ordinal), d.Words);

            // Restart: the diagnostics read back, newest first.
            SqliteConnection.ClearAllPools();
            var reopened = new AnomalyDiagnosticStore(root);
            Assert.AreEqual(3, reopened.List("src").Count); Assert.AreEqual(AnomalyDiagnostic.Blocked, reopened.Latest("src")!.Decision); Assert.AreEqual(run, reopened.List("src").Last().CorrelationId);
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
