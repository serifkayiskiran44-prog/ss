using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #901 (SOURCE DELETE: guard on a source in use). A source that products, field origins, sightings, run history or
// quality records still point at -- or that is being read right now -- cannot be hard-deleted; the verdict lists the
// references and offers the switch-off instead. A source nothing points at goes, with its own revisions, cached
// feeds and saved credential; a reopened store shows the same answers. Never a feed address in the words.
[TestClass]
public sealed class SourceDeleteTests
{
    static readonly DateTime Now = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);
    static XmlSource Source(string name) => new()
    {
        Name = name, Location = "https://feeds.example.com/" + name.ToLowerInvariant() + ".xml?key=abc123", Enabled = true, AutoImport = true, IntervalMinutes = 30, ItemPath = "/p",
        Fields = new Dictionary<string, string> { ["Sku"] = "s", ["Name"] = "n", ["Cost"] = "c", ["Stock"] = "q" },
    };

    [TestMethod]
    public void TheVerdictBlocksAReferencedOrRunningSourceAndOffersTheSwitchOffInstead()
    {
        var s = Source("Tedarikçi A");
        var free = SourceDeleteGuard.Evaluate(s, new SourceReferences(0, 0, 0, 0, 0, false, 2, 1, true));
        Assert.IsTrue(free.Allowed); Assert.IsFalse(free.Referenced); StringAssert.Contains(free.Headline, "hiçbir yerde kullanılmıyor; silinebilir");
        StringAssert.Contains(free.Headline, "2 yapılandırma sürümü"); StringAssert.Contains(free.Headline, "1 önbellekli besleme"); StringAssert.Contains(free.Headline, "kayıtlı kimlik bilgisi"); Assert.AreEqual("", free.Alternative);

        var used = SourceDeleteGuard.Evaluate(s, new SourceReferences(3, 12, 3, 2, 1, false, 4, 0, false));
        Assert.IsFalse(used.Allowed); Assert.IsTrue(used.Referenced); Assert.IsFalse(used.RunningJob); Assert.AreEqual(21, used.References.Total);
        StringAssert.Contains(used.Headline, "kullanımda (21 referans)"); StringAssert.Contains(used.Headline, "silme engellendi");
        Assert.IsTrue(used.Lines.Any(l => l.Contains("3 ürün bu kaynağa bağlı")) && used.Lines.Any(l => l.Contains("12 alan değeri")) && used.Lines.Any(l => l.Contains("2 kayıt")) && used.Lines.Any(l => l.Contains("Veri kalitesi")), string.Join(" | ", used.Lines));
        StringAssert.Contains(used.Alternative, "Kaynak aktif"); StringAssert.Contains(used.Body, used.Alternative);

        var running = SourceDeleteGuard.Evaluate(s, new SourceReferences(0, 0, 0, 1, 0, true, 1, 0, false));
        Assert.IsFalse(running.Allowed); Assert.IsTrue(running.RunningJob); StringAssert.Contains(running.Headline, "şu anda okunuyor"); Assert.IsTrue(running.Lines.Any(l => l.Contains("bitmeden silinemez")));

        Assert.AreEqual("Warning", SourceDeleteGuard.ToAudit(s, used, deleted: false).Outcome); Assert.AreEqual("Info", SourceDeleteGuard.ToAudit(s, free, deleted: true).Outcome);
        Assert.AreEqual(SourceDeleteGuard.AuditAction, SourceDeleteGuard.ToAudit(s, free, true).Action); StringAssert.Contains(SourceDeleteGuard.ToAudit(s, free, true).Detail, "Tedarikçi A");
        var text = string.Join(" ", new[] { free, used, running }.SelectMany(v => v.Lines.Append(v.Headline).Append(v.Alternative)));
        Assert.IsFalse(text.Contains("example.com") || text.Contains("abc123") || text.Contains("key="), text);
    }

    [TestMethod]
    public void TheStoreRefusesASourceInUseDeletesAnUnusedOneWithItsOwnDataAndAnswersTheSameAfterARestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "srcdel-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CatalogStore(root); var runs = new XmlRunStore(root);
            var used = Source("Kullanılan"); var idle = Source("Boş"); var fresh = Source("Yeni");
            store.SaveSource(used); store.SaveSource(idle); store.SaveSource(fresh);

            // "used" carries three products through the real import (origins, sightings and a run record follow).
            var run = runs.Start(used.Id, "h1", TimeSpan.FromMinutes(5), used.ConfigRevision);
            store.Import(used, new[] { "S1", "S2", "S3" }.Select(k => new CatalogProduct { SourceId = used.Id, SourceKind = "xml", Sku = k, Name = "Ürün " + k, Price = 10, Currency = "TRY", Cost = 4, Stock = 3 }).ToList(), CancellationToken.None, new XmlImportContext { RunId = run, SourceRevision = used.ConfigRevision, ObservedAtUtc = Now });
            runs.Complete(run, new ImportSummary(3, 0, 0));
            var references = store.SourceReferences(used.Id, Now);
            Assert.AreEqual(3, references.Products); Assert.IsTrue(references.FieldOrigins >= 3, "every imported field carries the source"); Assert.AreEqual(3, references.Sightings); Assert.AreEqual(1, references.Runs); Assert.IsFalse(references.RunningJob);
            var refused = store.DeleteSource(used.Id, Now);
            Assert.IsFalse(refused.Allowed); StringAssert.Contains(refused.Headline, "kullanımda"); Assert.AreEqual(3, store.Sources().Count, "nothing was deleted");
            Assert.AreEqual(3, store.Products().Count(p => p.SourceId == used.Id));

            // A concurrent job: "idle" is being read right now -- refused until the run ends; a finished run is history and still blocks a hard delete.
            var busy = runs.Start(idle.Id, "h2", TimeSpan.FromMinutes(10), idle.ConfigRevision);
            var whileRunning = store.DeleteSource(idle.Id, Now);
            Assert.IsFalse(whileRunning.Allowed); Assert.IsTrue(whileRunning.RunningJob); Assert.AreEqual(3, store.Sources().Count);
            runs.Complete(busy, new ImportSummary(0, 0, 0));
            var afterRun = store.DeleteSource(idle.Id, Now);
            Assert.IsFalse(afterRun.Allowed); Assert.IsFalse(afterRun.RunningJob); Assert.AreEqual(1, afterRun.References.Runs); StringAssert.Contains(afterRun.Alternative, "Kaynak aktif");

            // "fresh" has only its own data: a revision, a cached feed and a saved credential -- all go with it.
            XmlAuthStore.Save(fresh.Id, new XmlAuth("user", "pass"), root);
            FeedCache.Store(root, fresh.Id, "<items/>", FeedCacheOutcome.Success, "", "", Now);
            var own = store.SourceReferences(fresh.Id, Now);
            Assert.AreEqual(0, own.Total); Assert.IsTrue(own.Revisions >= 1); Assert.AreEqual(1, own.CachedFeeds); Assert.IsTrue(own.CredentialSaved);
            var deleted = store.DeleteSource(fresh.Id, Now);
            Assert.IsTrue(deleted.Allowed); Assert.AreEqual(2, store.Sources().Count); Assert.IsNull(store.Sources().FirstOrDefault(x => x.Id == fresh.Id));
            Assert.AreEqual(0, store.SourceRevisions(fresh.Id).Count); Assert.AreEqual(CredentialPresence.None, XmlAuthStore.Presence(fresh.Id, root)); Assert.AreEqual(0, FeedCache.List(root, fresh.Id).Count);
            Assert.IsFalse(Directory.Exists(Path.Combine(FeedCache.Root(root), fresh.Id)));

            // Restart: the same answers, and no orphan left behind.
            SqliteConnection.ClearAllPools();
            var reopened = new CatalogStore(root);
            Assert.IsFalse(reopened.DeleteSource(used.Id, Now).Allowed); Assert.IsNull(reopened.Sources().FirstOrDefault(x => x.Id == fresh.Id)); Assert.AreEqual(3, reopened.Products().Count);
            var health = DatabaseHealth.Inspect(root);
            Assert.AreEqual(0, health.OrphanProductCount, health.Detail);
            Assert.ThrowsException<InvalidOperationException>(() => reopened.DeleteSource(fresh.Id, Now), "a source that is already gone");
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
