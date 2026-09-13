using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #900 (SOURCE DISABLE: safety preview). Before a source is turned off, the operator sees what it leaves behind --
// linked products, the scheduler reading it, a running import, and which products would still have a fallback --
// and must confirm; a stale edit is refused; a cancel changes nothing; never a feed address in the words.
[TestClass]
public sealed class SourceDisableTests
{
    static readonly DateTime Now = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);
    static XmlSource Source(string id, bool enabled = true, bool auto = true, int revision = 2) => new()
    {
        Id = id, Name = "Tedarikçi " + id.ToUpperInvariant(), Location = "https://feeds.example.com/" + id + ".xml?key=abc123", Enabled = enabled, AutoImport = auto, IntervalMinutes = 30, ItemPath = "/p", ConfigRevision = revision,
        Fields = new Dictionary<string, string> { ["Sku"] = "s", ["Name"] = "n", ["Cost"] = "c", ["Stock"] = "q" },
    };
    static CatalogProduct Product(string id, string sourceId = "a") => new() { Id = id, SourceId = sourceId, SourceKind = "xml", Sku = "SKU-" + id, Name = "Ürün " + id, Price = 10, Cost = 4, Stock = 3 };
    static IReadOnlyDictionary<string, IReadOnlyDictionary<string, DateTime>> Seen(params (string Product, string Source, DateTime At)[] rows) =>
        rows.GroupBy(r => r.Product).ToDictionary(g => g.Key, g => (IReadOnlyDictionary<string, DateTime>)g.ToDictionary(r => r.Source, r => r.At, StringComparer.Ordinal), StringComparer.Ordinal);

    [TestMethod]
    public void ThePreviewNamesWhatDisablingWouldLeaveBehindAndRefusesAStaleEdit()
    {
        var a = Source("a"); var b = Source("b"); var off = Source("a", enabled: false);
        Assert.IsTrue(SourceDisable.IsDisable(a, off)); Assert.IsFalse(SourceDisable.IsDisable(Source("a", enabled: false), off), "already off");
        Assert.IsFalse(SourceDisable.IsDisable(a, Source("a")), "still on"); Assert.IsFalse(SourceDisable.IsDisable(null, off), "a record that was never saved");

        // An unused source: nothing linked, nothing scheduled, nothing running -- it still asks, and says the switch is safe.
        var unused = SourceDisable.Preview(Source("a", auto: false), Source("a", enabled: false, auto: false), [], [a, b], Seen(), null, Now);
        Assert.IsTrue(unused.Unused); Assert.IsTrue(unused.RequiresConfirmation); StringAssert.Contains(unused.Headline, "kullanılmıyor"); StringAssert.Contains(unused.Headline, "hiçbir ürünü etkilemez");

        // An active source: linked products, the scheduler reading it, an import running; two products have a fresh fallback on B, one has none.
        var products = new[] { Product("p1"), Product("p2"), Product("p3") };
        var seen = Seen(("p1", "a", Now.AddMinutes(-10)), ("p1", "b", Now.AddMinutes(-5)), ("p2", "a", Now.AddMinutes(-10)), ("p2", "b", Now.AddMinutes(-5)), ("p3", "a", Now.AddMinutes(-10)));
        var active = SourceDisable.Preview(a, off, products, [a, b], seen, new("Running", Now.AddMinutes(-3), null, Now.AddMinutes(7), ""), Now);
        Assert.IsFalse(active.Stale); Assert.IsFalse(active.Unused); Assert.AreEqual(3, active.LinkedProducts); Assert.IsTrue(active.SchedulerActive); Assert.IsTrue(active.RunningJob);
        Assert.AreEqual(2, active.WithFallback); Assert.AreEqual(1, active.WithoutFallback);
        StringAssert.Contains(active.Headline, "3 bağlı ürün"); StringAssert.Contains(active.Headline, "yedek 2 uygun / 1 yok"); StringAssert.Contains(active.Headline, "zamanlayıcı duracak"); StringAssert.Contains(active.Headline, "çalışan aktarım var");
        Assert.IsTrue(active.Lines.Any(l => l.Contains("1 ürün tazelenmeden kalır")), string.Join(" | ", active.Lines));
        var expiredLease = SourceDisable.Preview(a, off, products, [a, b], seen, new("Running", Now.AddHours(-2), null, Now.AddMinutes(-50), ""), Now);
        Assert.IsFalse(expiredLease.RunningJob, "a run whose lease expired is not running");

        // No fallback at all when the other source is itself off, or has never seen the products.
        var alone = SourceDisable.Preview(a, off, products, [a, Source("b", enabled: false)], seen, null, Now);
        Assert.AreEqual(0, alone.WithFallback); Assert.AreEqual(3, alone.WithoutFallback); StringAssert.Contains(alone.Headline, "yedek 0 uygun / 3 yok");
        var unseen = SourceDisable.Preview(a, off, products, [a, b], Seen(("p1", "a", Now)), null, Now);
        Assert.AreEqual(3, unseen.WithoutFallback);

        // Stale: the record changed under the editor -- refused, not asked.
        var stale = SourceDisable.Preview(Source("a", revision: 3), off, products, [a, b], seen, null, Now);
        Assert.IsTrue(stale.Stale); Assert.IsFalse(stale.RequiresConfirmation); StringAssert.Contains(stale.Headline, "yeniden seçin");

        // The audit row for a confirmed switch-off carries the headline; never an address or a key anywhere.
        var audit = SourceDisable.ToAudit(a, active);
        Assert.AreEqual("import", audit.Module); Assert.AreEqual(SourceDisable.AuditAction, audit.Action); Assert.AreEqual("Warning", audit.Outcome); StringAssert.Contains(audit.Detail, "Tedarikçi A");
        Assert.AreEqual("Info", SourceDisable.ToAudit(a, unused).Outcome);
        var text = string.Join(" ", new[] { unused, active, alone, unseen, stale }.SelectMany(p => p.Lines.Append(p.Headline)).Append(audit.Detail));
        Assert.IsFalse(text.Contains("example.com") || text.Contains("abc123") || text.Contains("key="), text);
    }

    [TestMethod]
    public void TheStoreFactsFeedThePreviewAndAConcurrentEditIsRefused()
    {
        var root = Path.Combine(Path.GetTempPath(), "disable-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CatalogStore(root); var runs = new XmlRunStore(root);
            var a = Source("a", revision: 0); var b = Source("b", revision: 0); store.SaveSource(a); store.SaveSource(b);
            void Import(XmlSource s, string[] skus, DateTime at)
            {
                var run = runs.Start(s.Id, Guid.NewGuid().ToString("N"), TimeSpan.FromMinutes(5), s.ConfigRevision);
                store.Import(s, skus.Select(k => new CatalogProduct { SourceId = s.Id, SourceKind = "xml", Sku = k, Name = "Ürün " + k, Price = 10, Currency = "TRY", Cost = 4, Stock = 3 }).ToList(), CancellationToken.None, new XmlImportContext { RunId = run, SourceRevision = s.ConfigRevision, ObservedAtUtc = at });
                runs.Complete(run, new ImportSummary(0, 0, 0));
            }
            Import(a, ["S1", "S2", "S3"], Now); Import(b, ["S1", "S2"], Now.AddMinutes(5));

            var persisted = store.Sources().Single(x => x.Id == "a"); var edited = store.Sources().Single(x => x.Id == "a"); edited.Enabled = false;
            var linked = store.Products().Where(p => p.SourceId == "a").ToList();
            Assert.AreEqual(3, linked.Count, "B's rows met A's products instead of creating twins");
            var preview = SourceDisable.Preview(persisted, edited, linked, store.Sources(), store.SightingsByProduct(), null, Now.AddMinutes(10));
            Assert.IsFalse(preview.Stale); Assert.AreEqual(3, preview.LinkedProducts); Assert.AreEqual(2, preview.WithFallback); Assert.AreEqual(1, preview.WithoutFallback); Assert.IsTrue(preview.SchedulerActive);

            // Another editor saves first: the preview is stale, the store refuses the edit, and the source stays on.
            var other = store.Sources().Single(x => x.Id == "a"); other.Priority = 120; store.SaveSource(other);
            Assert.IsTrue(SourceDisable.Preview(store.Sources().Single(x => x.Id == "a"), edited, linked, store.Sources(), store.SightingsByProduct(), null, Now.AddMinutes(10)).Stale);
            Assert.ThrowsException<SourceRevisionConflictException>(() => store.SaveSource(edited));
            Assert.IsTrue(store.Sources().Single(x => x.Id == "a").Enabled);
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
