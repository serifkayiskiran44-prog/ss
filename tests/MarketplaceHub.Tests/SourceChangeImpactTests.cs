using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #899 (SOURCE CHANGE: impact preview before save). Before the XML source form saves, the operator sees what the
// change would touch -- which fields, how many linked products, which jobs -- a stale edit is refused with words, a
// large impact is named as such, a cancelled save leaves the store exactly as it was, and no value that could be a key
// ever enters the words.
[TestClass]
public sealed class SourceChangeImpactTests
{
    static readonly DateTime Now = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);
    static XmlSource Source(string location = "https://feeds.example.com/a.xml?key=abc123", int revision = 3) => new()
    {
        Id = "a", Name = "Tedarikçi A", Location = location, Enabled = true, AutoImport = true, IntervalMinutes = 30, ItemPath = "/p", ConfigRevision = revision,
        Fields = new Dictionary<string, string> { ["Sku"] = "s", ["Name"] = "n", ["Cost"] = "c", ["Stock"] = "q" },
    };
    static XmlSource Edited(Action<XmlSource> change) { var s = Source(); change(s); return s; }

    [TestMethod]
    public void ThePreviewSaysWhatASaveWouldTouchAndNeverShowsAKey()
    {
        var persisted = Source();

        // No impact: a label-only change, or no change at all.
        var label = SourceChangeImpact.Preview(persisted, Edited(s => s.Name = "Tedarikçi A (yeni)"), 30, null, Now);
        Assert.IsFalse(label.Stale); Assert.AreEqual(SourceChangeLevel.None, label.Level); Assert.IsFalse(label.RequiresConfirmation);
        CollectionAssert.AreEqual(new[] { SourceChangeImpact.Label }, label.Groups.ToArray()); StringAssert.Contains(label.Headline, "Etkilenen ürün veya iş yok");
        Assert.AreEqual("Değişiklik yok.", SourceChangeImpact.Preview(persisted, Source(), 30, null, Now).Headline);

        // Small impact: a new address touches every linked product and the scheduled import; the key never appears, before or after.
        var address = SourceChangeImpact.Preview(persisted, Edited(s => s.Location = "https://feeds.example.com/b.xml?key=zzz999"), 30, null, Now);
        Assert.AreEqual(SourceChangeLevel.Small, address.Level); Assert.IsTrue(address.RequiresConfirmation); Assert.AreEqual(30, address.AffectedProducts); Assert.AreEqual(1, address.AffectedJobs);
        CollectionAssert.AreEqual(new[] { SourceChangeImpact.Address }, address.Groups.ToArray()); StringAssert.Contains(address.Headline, "30 ürün ve 1 iş");
        Assert.IsFalse(address.Body.Contains("abc123") || address.Body.Contains("zzz999") || address.Body.Contains("key="), address.Body); StringAssert.Contains(address.Body, "feeds.example.com");
        Assert.IsTrue(address.Lines.Any(l => l.StartsWith("Adres:", StringComparison.Ordinal)) && address.Lines.Any(l => l.StartsWith("Etki:", StringComparison.Ordinal)), string.Join(" | ", address.Lines));

        // Large impact: a mapping change over many products; any meaningful change while an import is running.
        var mapping = SourceChangeImpact.Preview(persisted, Edited(s => s.Fields["Stock"] = "qty"), 120, null, Now);
        Assert.AreEqual(SourceChangeLevel.Large, mapping.Level); StringAssert.Contains(mapping.Headline, "büyük etki"); CollectionAssert.AreEqual(new[] { SourceChangeImpact.Mapping }, mapping.Groups.ToArray());
        var running = SourceChangeImpact.Preview(persisted, Edited(s => s.IntervalMinutes = 60), 5, new("Running", Now.AddMinutes(-3), null, Now.AddMinutes(7), ""), Now);
        Assert.AreEqual(SourceChangeLevel.Large, running.Level); Assert.IsTrue(running.RunningJob); StringAssert.Contains(running.Headline, "çalışan aktarım var");
        Assert.AreEqual(2, running.AffectedJobs); Assert.AreEqual(0, running.AffectedProducts, "a schedule change does not rewrite products");
        var expiredLease = SourceChangeImpact.Preview(persisted, Edited(s => s.IntervalMinutes = 60), 5, new("Running", Now.AddHours(-2), null, Now.AddMinutes(-50), ""), Now);
        Assert.IsFalse(expiredLease.RunningJob, "a run whose lease expired is not running"); Assert.AreEqual(SourceChangeLevel.Small, expiredLease.Level);
        var renamedWhileRunning = SourceChangeImpact.Preview(persisted, Edited(s => s.Name = "Yeni ad"), 5, new("Running", Now.AddMinutes(-3), null, Now.AddMinutes(7), ""), Now);
        Assert.AreEqual(SourceChangeLevel.None, renamedWhileRunning.Level, "a label does not touch a running import");

        // A schedule change with no scheduled job and no products touches nothing.
        var quiet = SourceChangeImpact.Preview(Source(), Edited(s => { s.AutoImport = false; s.IntervalMinutes = 60; }), 0, null, Now);
        Assert.AreEqual(SourceChangeLevel.None, quiet.Level); CollectionAssert.AreEqual(new[] { SourceChangeImpact.Schedule }, quiet.Groups.ToArray());

        // A stale edit is refused with words, whatever it changes.
        var stale = SourceChangeImpact.Preview(Source(revision: 4), Edited(s => s.Location = "https://feeds.example.com/c.xml"), 30, null, Now);
        Assert.IsTrue(stale.Stale); Assert.IsFalse(stale.RequiresConfirmation); StringAssert.Contains(stale.Headline, "siz düzenlerken değişti"); StringAssert.Contains(stale.Headline, "rev. 4"); StringAssert.Contains(stale.Headline, "yeniden seçin");

        // A new source has nothing to touch yet.
        var fresh = SourceChangeImpact.Preview(null, Source(revision: 0), 0, null, Now);
        Assert.AreEqual(SourceChangeLevel.None, fresh.Level); Assert.IsFalse(fresh.Stale); Assert.IsFalse(fresh.Body.Contains("abc123"));
    }

    [TestMethod]
    public void AConcurrentEditIsBlockedAndACancelledSaveLeavesTheStoreUntouched()
    {
        var root = Path.Combine(Path.GetTempPath(), "impact-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CatalogStore(root); var runs = new XmlRunStore(root);
            var s = Source(revision: 0); store.SaveSource(s);
            // Sixty products linked to the source through the real import, so an address change is a large impact.
            var run = runs.Start(s.Id, "h1", TimeSpan.FromMinutes(5), s.ConfigRevision);
            store.Import(s, Enumerable.Range(1, 60).Select(i => new CatalogProduct { SourceId = s.Id, SourceKind = "xml", Sku = $"SKU-{i:D3}", Name = "Ürün " + i, Price = 10, Currency = "TRY", Cost = 4, Stock = 3 }).ToList(), CancellationToken.None, new XmlImportContext { RunId = run, SourceRevision = s.ConfigRevision, ObservedAtUtc = Now });
            runs.Complete(run, new ImportSummary(60, 0, 0));

            // Two editors load the same record; the first saves; the second's edit is stale and refused, by the preview and by the store.
            var first = store.Sources().Single(); var second = store.Sources().Single();
            first.Priority = 120; store.SaveSource(first);
            var stale = SourceChangeImpact.Preview(store.Sources().Single(), second, 60, null, Now);
            Assert.IsTrue(stale.Stale); Assert.ThrowsException<SourceRevisionConflictException>(() => store.SaveSource(second));
            Assert.AreEqual(120, store.Sources().Single().Priority, "the first save stands");

            // A cancelled save: the preview asks (large impact); when the answer is no, nothing is written.
            var edit = store.Sources().Single(); var revision = edit.ConfigRevision; edit.Location = "https://feeds.example.com/moved.xml?key=new777";
            var preview = SourceChangeImpact.Preview(store.Sources().Single(), edit, store.Products().Count(p => p.SourceId == edit.Id), null, Now);
            Assert.AreEqual(SourceChangeLevel.Large, preview.Level); Assert.IsTrue(preview.RequiresConfirmation); Assert.AreEqual(60, preview.AffectedProducts); Assert.IsFalse(preview.Body.Contains("new777"), preview.Body);
            var untouched = store.Sources().Single();
            Assert.AreEqual(revision, untouched.ConfigRevision); StringAssert.Contains(untouched.Location, "a.xml");

            // Confirmed: saved as a new revision, and the next preview is quiet.
            store.SaveSource(edit); Assert.AreEqual(revision + 1, store.Sources().Single().ConfigRevision);
            Assert.AreEqual("Değişiklik yok.", SourceChangeImpact.Preview(store.Sources().Single(), store.Sources().Single(), 60, null, Now).Headline);
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
