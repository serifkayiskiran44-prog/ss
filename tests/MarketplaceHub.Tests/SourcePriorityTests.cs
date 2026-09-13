using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #896 (SOURCE PRIORITY: an explainable outcome). When two sources carry the same product, a field goes to the
// operator's lock first, then to the higher priority, then — priorities equal — to the fresher observation, and a
// stale holder loses even to a lower priority; every decision names winner, loser, priorities, reason and moment,
// lands in the audit trail and stays on the field's origin across a restart. Never a value.
[TestClass]
public sealed class SourcePriorityTests
{
    static readonly DateTime Now = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);
    static XmlSource Source(string id, int priority, int interval = 30) => new() { Id = id, Name = "Tedarikçi " + id.ToUpperInvariant(), Priority = priority, IntervalMinutes = interval, ItemPath = "/p", Fields = new Dictionary<string, string> { ["Sku"] = "s", ["Name"] = "n" } };
    static FieldOrigin Held(string sourceId, DateTime observed) => new() { Kind = FieldProvenance.FeedKind, SourceId = sourceId, SourceRevision = 1, RunId = "run-a", ObservedUtc = observed };
    static string NameOf(string id) => id switch { "a" => "Tedarikçi A", "b" => "Tedarikçi B", "" => "operatör", _ => id };

    [TestMethod]
    public void TheRulesDecideInOrderLockPriorityFreshnessAndStalenessWithReadableWords()
    {
        var a = Source("a", 100); var b = Source("b", 100);
        Assert.IsNull(SourcePriority.Decide("p", "Stock", b, null, null, locked: false, Now), "no holder, no contest");
        Assert.IsNull(SourcePriority.Decide("p", "Stock", b, Held("b", Now.AddHours(-1)), b, false, Now), "the same source rewrites its own field");
        Assert.IsNull(SourcePriority.Decide("p", "Stock", b, new FieldOrigin { Kind = FieldProvenance.ManualKind, ObservedUtc = Now }, null, false, Now), "an unlocked manual field is the feed's to rewrite, as before");

        var locked = SourcePriority.Decide("p", "Price", b, Held("a", Now.AddHours(-1)), a, locked: true, Now)!;
        Assert.IsFalse(locked.IncomingWins); Assert.AreEqual(SourcePriority.ManualLock, locked.Reason); StringAssert.Contains(locked.Words(NameOf), "elle kilitli"); StringAssert.Contains(locked.Words(NameOf), "Tedarikçi B yazamadı");

        var lower = SourcePriority.Decide("p", "Stock", Source("b", 50), Held("a", Now.AddHours(-1)), a, false, Now)!;
        Assert.IsFalse(lower.IncomingWins); Assert.AreEqual("a", lower.WinnerSourceId); Assert.AreEqual(100, lower.WinnerPriority); Assert.AreEqual(50, lower.LoserPriority); Assert.AreEqual(SourcePriority.HigherPriority, lower.Reason);
        Assert.AreEqual("öncelik 100 > 50 · Tedarikçi A kazandı, Tedarikçi B yazamadı", lower.Words(NameOf));

        var higher = SourcePriority.Decide("p", "Stock", Source("b", 120), Held("a", Now.AddHours(-1)), a, false, Now)!;
        Assert.IsTrue(higher.IncomingWins); Assert.AreEqual("b", higher.WinnerSourceId); StringAssert.StartsWith(higher.Words(NameOf), "öncelik 120 > 100");

        var equal = SourcePriority.Decide("p", "Stock", b, Held("a", Now.AddHours(-1)), a, false, Now)!;
        Assert.IsTrue(equal.IncomingWins); Assert.AreEqual(SourcePriority.EqualPriorityFresher, equal.Reason); StringAssert.Contains(equal.Words(NameOf), "eşit öncelik 100"); StringAssert.Contains(equal.Words(NameOf), "Tedarikçi B");

        // Staleness: three intervals, at least six hours.
        Assert.IsFalse(SourcePriority.IsStale(Held("a", Now.AddHours(-5)), a, Now)); Assert.IsTrue(SourcePriority.IsStale(Held("a", Now.AddHours(-7)), a, Now));
        Assert.IsFalse(SourcePriority.IsStale(Held("a", Now.AddHours(-7)), Source("a", 100, interval: 240), Now), "a twelve-hour grace for a four-hour interval");
        var stale = SourcePriority.Decide("p", "Stock", Source("b", 10), Held("a", Now.AddDays(-2)), a, false, Now)!;
        Assert.IsTrue(stale.IncomingWins); Assert.AreEqual(SourcePriority.StaleHolder, stale.Reason); StringAssert.Contains(stale.Words(NameOf), "bayat");

        var audit = SourcePriority.ToAudit(lower, NameOf);
        Assert.AreEqual("import", audit.Module); Assert.AreEqual(SourcePriority.AuditAction, audit.Action); Assert.AreEqual("p", audit.ProductId); Assert.AreEqual("Warning", audit.Outcome); StringAssert.StartsWith(audit.Detail, "Stock: öncelik 100 > 50");
        Assert.AreEqual("Info", SourcePriority.ToAudit(higher, NameOf).Outcome);
    }

    [TestMethod]
    public void TwoFeedsCarryingTheSameProductLeaveEachFieldWithAnExplainedOwnerAcrossARestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "prio-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CatalogStore(root); var runs = new XmlRunStore(root);
            var a = Source("a", 100); var b = Source("b", 50); store.SaveSource(a); store.SaveSource(b);
            CatalogProduct Row(string sourceId, int stock, decimal price) => new() { SourceId = sourceId, SourceKind = "xml", Sku = "SKU-1", Name = "Kupa", Price = price, Currency = "TRY", Cost = 4, Stock = stock };
            string Import(XmlSource s, CatalogProduct row, DateTime at) { var run = runs.Start(s.Id, Guid.NewGuid().ToString("N"), TimeSpan.FromMinutes(5), s.ConfigRevision); store.Import(s, new[] { row }, CancellationToken.None, new XmlImportContext { RunId = run, SourceRevision = s.ConfigRevision, ObservedAtUtc = at }); runs.Complete(run, new ImportSummary(0, 0, 0)); return run; }

            // A creates the product; B (lower priority) carries the same SKU: the product stays one record, A's fields stand, and every refused field is explained.
            Import(a, Row("a", 3, 10), Now);
            Import(b, Row("b", 9, 12), Now.AddMinutes(10));
            var product = store.Products().Single();
            Assert.AreEqual("a", product.SourceId, "the product keeps its home source"); Assert.AreEqual(3, product.Stock); Assert.AreEqual(10, product.Price);
            var stock = FieldProvenance.Of(product, "Stock")!;
            Assert.AreEqual("a", stock.SourceId); StringAssert.Contains(stock.Decision, "öncelik 100 > 50"); StringAssert.Contains(stock.Decision, "Tedarikçi B yazamadı");
            var audit = new AuditStore(root).List(50).Where(e => e.Action == SourcePriority.AuditAction).ToList();
            Assert.IsTrue(audit.Count >= 2, "every refused field is an audit row: " + audit.Count); Assert.IsTrue(audit.All(e => e.ProductId == product.Id && e.Outcome == "Warning"));
            Assert.IsFalse(audit.Any(e => e.Detail.Contains("12") || e.Detail.Contains("9")), "the audit carries priorities and words, not values");

            // B raised above A: B's next feed wins the fields it writes; A's home stays.
            b = store.Sources().Single(x => x.Id == "b"); b.Priority = 150; store.SaveSource(b);
            var runB = Import(b, Row("b", 11, 13), Now.AddMinutes(20));
            product = store.Products().Single();
            Assert.AreEqual("a", product.SourceId); Assert.AreEqual(11, product.Stock); Assert.AreEqual(13, product.Price);
            Assert.AreEqual("b", FieldProvenance.Of(product, "Stock")!.SourceId); Assert.AreEqual(runB, FieldProvenance.Of(product, "Stock")!.RunId); StringAssert.StartsWith(FieldProvenance.Of(product, "Stock")!.Decision, "öncelik 150 > 100");

            // The operator locks the price (near the feed price: the dropshipping anomaly gate compares a feed against its own stored prices): neither feed may write it, and the decision says so.
            product.LockPrice = true; product.Price = 10.5m; product.PriceSource = "manual"; store.SaveProduct(product);
            Import(b, Row("b", 12, 14), Now.AddMinutes(30));
            product = store.Products().Single();
            Assert.AreEqual(10.5m, product.Price); Assert.AreEqual(12, product.Stock);
            Assert.AreEqual(FieldProvenance.ManualKind, FieldProvenance.Of(product, "Price")!.Kind); StringAssert.Contains(FieldProvenance.Of(product, "Price")!.Decision, "elle kilitli");

            // Equal priority: the fresher feed wins.
            a = store.Sources().Single(x => x.Id == "a"); a.Priority = 150; store.SaveSource(a);
            Import(a, Row("a", 5, 10), Now.AddMinutes(40));
            product = store.Products().Single();
            Assert.AreEqual(5, product.Stock); StringAssert.Contains(FieldProvenance.Of(product, "Stock")!.Decision, "eşit öncelik 150");

            // A stale holder loses even to a lower priority.
            b = store.Sources().Single(x => x.Id == "b"); b.Priority = 120; store.SaveSource(b);
            Import(b, Row("b", 8, 14.75m), Now.AddDays(3));
            product = store.Products().Single();
            Assert.AreEqual(8, product.Stock); StringAssert.Contains(FieldProvenance.Of(product, "Stock")!.Decision, "bayat");

            // Restart: the decision words come back with the product and the provenance row shows them.
            SqliteConnection.ClearAllPools();
            var reopened = new CatalogStore(root); var again = reopened.Products().Single();
            StringAssert.Contains(FieldProvenance.Of(again, "Stock")!.Decision, "bayat");
            var sources = reopened.Sources();
            var view = ProductProvenance.Build(again, sources.Single(x => x.Id == "a"), Now.AddDays(3), id => sources.FirstOrDefault(x => x.Id == id));
            var stockRow = view.Rows.Single(r => r.Field == "Stok");
            Assert.AreEqual("Tedarikçi B", stockRow.Origin, "the row names the source that holds the field, not the product's home source"); StringAssert.Contains(stockRow.Detail, "bayat");
            Assert.IsTrue(view.Rows.Single(r => r.Field == "Fiyat").IsOperatorOwned);
            var text = string.Join(" ", view.Rows.Select(r => r.Origin + " " + r.Detail));
            Assert.IsFalse(text.Contains("14.75") || text.Contains("14,75") || text.Contains("10.5") || text.Contains("10,5"), "the rows carry words, not values: " + text);
        }
        finally
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
                catch (IOException) { Thread.Sleep(300); }
                catch (UnauthorizedAccessException) { Thread.Sleep(300); }
            }
        }
    }
}
