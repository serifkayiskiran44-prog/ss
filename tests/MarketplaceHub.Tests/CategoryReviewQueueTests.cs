using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #913 (TAXONOMY: unmapped category review queue). A category value a feed writes that is neither a local category's
// name nor an approved alias lands in the queue with its evidence -- source, count, first/last seen, a sample SKU --
// one row per source and value; a repeated value grows its count; approving binds it as an approved alias through
// the dictionary so the next run applies it; rejecting keeps it out of the pending list until it is seen again;
// bulk approve and reject act only on the previewed items and never without the preview's confirmation; it all
// survives a restart.
[TestClass]
public sealed class CategoryReviewQueueTests
{
    static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
    static string K(string value) => TaxonomyAliasStore.Key(value);

    [TestMethod]
    public void TheQueueKeepsEvidencePerSourceGrowsOnRepeatsResolvesThroughTheDictionaryAndRefusesBlindBulkActions()
    {
        var root = Path.Combine(Path.GetTempPath(), "review-" + Guid.NewGuid().ToString("N"));
        try
        {
            var taxonomy = new TaxonomyStore(root); var aliases = new TaxonomyAliasStore(root); var queue = new CategoryReviewQueue(root);
            var electronics = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "Elektronik", Value = "" });
            var garden = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "Bahçe ürünleri", Value = "" });
            var toys = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "Oyuncak", Value = "" });
            aliases.Save("elektronik ürünleri", electronics.Id, approved: true);
            aliases.Save("oyuncaklar", garden.Id, approved: true);

            // What counts as mapped: a local category's name or an approved alias, by key.
            var known = queue.KnownMap();
            Assert.IsTrue(known.ContainsKey(K("ELEKTRONİK"))); Assert.IsTrue(known.ContainsKey(K("Elektronik Ürünleri"))); Assert.IsFalse(known.ContainsKey(K("Bahçe")));

            // Recording: one row per source and key, the count and the sample from the sightings; a repeat grows the count and moves the last-seen, never the first-seen.
            Assert.AreEqual(2, queue.Record("src-a", [new("Bahçe", "SKU-1"), new("bahçe", "SKU-2"), new("Mutfak", "SKU-3"), new("", "SKU-4")], Now));
            var pending = queue.List(CategoryReviewItem.Pending); Assert.AreEqual(2, pending.Count);
            var bahce = pending.Single(i => i.Key == K("bahçe")); Assert.AreEqual(2, bahce.Count); Assert.AreEqual("SKU-1", bahce.SampleSku); Assert.AreEqual("Bahçe", bahce.Value); Assert.AreEqual(Now, bahce.FirstSeenUtc); Assert.AreEqual("src-a", bahce.SourceId);
            queue.Record("src-a", [new("BAHÇE", "SKU-9")], Now.AddHours(1));
            bahce = queue.Find("src-a", K("bahçe"))!; Assert.AreEqual(3, bahce.Count); Assert.AreEqual(Now, bahce.FirstSeenUtc); Assert.AreEqual(Now.AddHours(1), bahce.LastSeenUtc); Assert.AreEqual("SKU-1", bahce.SampleSku, "the first sample stays");
            Assert.AreEqual(2, queue.List().Count, "a repeat is not a second row");

            // Source isolation: another source's same spelling is its own row with its own evidence.
            queue.Record("src-b", [new("Bahçe", "B-1")], Now.AddHours(2));
            Assert.AreEqual(1, queue.List(null, "src-b").Count); Assert.AreEqual(1, queue.Find("src-b", K("bahçe"))!.Count); Assert.AreEqual(3, queue.Find("src-a", K("bahçe"))!.Count);

            // Approving binds the value as an approved alias of the chosen category through the dictionary (its rules apply) and marks the row; the dictionary now resolves it.
            var approved = queue.Approve("src-a", K("bahçe"), garden.Id, Now.AddHours(3));
            Assert.AreEqual(CategoryReviewItem.Approved, approved.Status); Assert.AreEqual("Bahçe ürünleri", approved.ResolvedName); Assert.AreEqual(Now.AddHours(3), approved.ResolvedUtc);
            Assert.AreEqual("Bahçe ürünleri", aliases.Resolve("BAHÇE")!.CategoryName); Assert.IsTrue(queue.KnownMap().ContainsKey(K("bahçe")));
            StringAssert.Contains(aliases.List().Single(a => a.Key == K("bahçe")).Source, "src-a");
            Assert.ThrowsException<InvalidOperationException>(() => queue.Approve("src-a", K("yok"), garden.Id, Now), "not in the queue");
            Assert.ThrowsException<InvalidOperationException>(() => queue.Approve("src-a", K("mutfak"), "no-such-category", Now), "the dictionary's rules apply");
            Assert.AreEqual(CategoryReviewItem.Pending, queue.Find("src-a", K("mutfak"))!.Status, "a refused approval marks nothing");

            // Rejecting keeps the value out of the pending list until it is seen again.
            Assert.AreEqual(CategoryReviewItem.Rejected, queue.Reject("src-a", K("mutfak"), Now.AddHours(4)).Status);
            Assert.AreEqual(1, queue.List(CategoryReviewItem.Pending).Count, "only src-b's value is pending");
            queue.Record("src-a", [new("Mutfak", "SKU-30")], Now.AddHours(5));
            Assert.AreEqual(CategoryReviewItem.Pending, queue.Find("src-a", K("mutfak"))!.Status); Assert.AreEqual(2, queue.Find("src-a", K("mutfak"))!.Count);

            // Bulk: nothing without the preview's confirmation; exactly the previewed items; a conflict is refused and the queue is left untouched; approving a value that is already this category's alias is the same alias.
            var toApprove = queue.List(CategoryReviewItem.Pending);
            Assert.AreEqual(2, toApprove.Count);
            Assert.ThrowsException<InvalidOperationException>(() => queue.ApproveBulk(toApprove, toys.Id, confirmed: false, Now));
            Assert.ThrowsException<InvalidOperationException>(() => queue.RejectBulk(toApprove, confirmed: false, Now));
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => queue.ApproveBulk(toApprove, toys.Id, confirmed: true, Now.AddHours(6))).Message, "Bahçe ürünleri", "src-b's 'Bahçe' is already the garden category's alias -- a conflict for the toys category");
            Assert.IsTrue(queue.List(CategoryReviewItem.Pending).All(i => i.Status == CategoryReviewItem.Pending), "the refused bulk marked nothing");
            Assert.IsNull(aliases.Resolve("mutfak"), "the refused bulk wrote no alias either -- every item is checked before the first write");
            Assert.AreEqual(2, queue.ApproveBulk(toApprove, garden.Id, confirmed: true, Now.AddHours(6)));
            Assert.AreEqual(0, queue.List(CategoryReviewItem.Pending).Count); Assert.AreEqual("Bahçe ürünleri", aliases.Resolve("mutfak")!.CategoryName);
            queue.Record("src-b", [new("Kırtasiye", "B-7"), new("Ofis", "B-8")], Now.AddHours(7));
            Assert.AreEqual(2, queue.RejectBulk(queue.List(CategoryReviewItem.Pending), confirmed: true, Now.AddHours(8))); Assert.AreEqual(0, queue.List(CategoryReviewItem.Pending).Count);

            // Restart: evidence, states and resolutions read back.
            SqliteConnection.ClearAllPools();
            var reopened = new CategoryReviewQueue(root);
            Assert.AreEqual(5, reopened.List().Count); Assert.AreEqual(3, reopened.Find("src-a", K("bahçe"))!.Count); Assert.AreEqual("Bahçe ürünleri", reopened.Find("src-b", K("bahçe"))!.ResolvedName); Assert.AreEqual(CategoryReviewItem.Rejected, reopened.Find("src-b", K("ofis"))!.Status);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void TheImportQueuesOnlyUnmappedValuesWithTheirEvidenceAndStopsOnceTheyAreApproved()
    {
        var root = Path.Combine(Path.GetTempPath(), "review-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var taxonomy = new TaxonomyStore(root); var aliases = new TaxonomyAliasStore(root); var queue = new CategoryReviewQueue(root); var store = new CatalogStore(root); var runs = new XmlRunStore(root);
            var electronics = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "Elektronik", Value = "" });
            var garden = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "Bahçe ürünleri", Value = "" });
            aliases.Save("elektronik ürünleri", electronics.Id, approved: true);
            XmlSource Source(string name) => new() { Name = name, Location = "https://feeds.example.com/" + name.ToLowerInvariant() + ".xml", Enabled = true, IntervalMinutes = 30, ItemPath = "/p", Fields = new Dictionary<string, string> { ["Sku"] = "s", ["Name"] = "n", ["Cost"] = "c", ["Stock"] = "q", ["Category"] = "k" } };
            var a = Source("A"); var b = Source("B"); store.SaveSource(a); store.SaveSource(b);
            void Import(XmlSource s, (string Sku, string Category)[] rows, DateTime at)
            {
                var run = runs.Start(s.Id, Guid.NewGuid().ToString("N"), TimeSpan.FromMinutes(5), s.ConfigRevision);
                try
                {
                    store.Import(s, rows.Select(r => new CatalogProduct { SourceId = s.Id, SourceKind = "xml", Sku = r.Sku, Name = "Ürün " + r.Sku, Price = 10, Currency = "TRY", Cost = 4, Stock = 3, Category = r.Category }).ToList(), CancellationToken.None, new XmlImportContext { RunId = run, SourceRevision = s.ConfigRevision, ObservedAtUtc = at });
                    runs.Complete(run, new ImportSummary(0, 0, 0));
                }
                catch { runs.Fail(run, "test"); throw; }
            }

            // A known name and an approved alias are not queued; an unmapped value is, once per key, with its count and sample; an empty value is nothing.
            Import(a, [("SKU-1", "ELEKTRONİK ürünleri"), ("SKU-2", "elektronik"), ("SKU-3", "Bahçe"), ("SKU-4", "bahçe"), ("SKU-5", "")], Now);
            var pending = queue.List(CategoryReviewItem.Pending); Assert.AreEqual(1, pending.Count);
            Assert.AreEqual(a.Id, pending[0].SourceId); Assert.AreEqual(2, pending[0].Count); Assert.AreEqual("SKU-3", pending[0].SampleSku); Assert.AreEqual("Bahçe", pending[0].Value); Assert.AreEqual(Now, pending[0].FirstSeenUtc);
            Assert.AreEqual("Elektronik", store.Products().Single(p => p.Sku == "SKU-1").Category, "the alias still applies"); Assert.AreEqual("Bahçe", store.Products().Single(p => p.Sku == "SKU-3").Category, "an unmapped value stays as written");

            // Another source's same value is its own row; a repeat run of the first source grows its evidence, not the other's.
            Import(b, [("B-1", "Bahçe")], Now.AddMinutes(5));
            Import(a, [("SKU-1", "ELEKTRONİK ürünleri"), ("SKU-2", "elektronik"), ("SKU-3", "Bahçe"), ("SKU-4", "bahçe"), ("SKU-5", "")], Now.AddMinutes(10));
            Assert.AreEqual(4, queue.Find(a.Id, K("bahçe"))!.Count); Assert.AreEqual(1, queue.Find(b.Id, K("bahçe"))!.Count); Assert.AreEqual(Now.AddMinutes(10), queue.Find(a.Id, K("bahçe"))!.LastSeenUtc);

            // Approved from the queue: the next run applies the alias to a new product and queues the value no more.
            queue.Approve(a.Id, K("bahçe"), garden.Id, Now.AddMinutes(15));
            Import(a, [("SKU-1", "ELEKTRONİK ürünleri"), ("SKU-2", "elektronik"), ("SKU-3", "Bahçe"), ("SKU-4", "bahçe"), ("SKU-5", ""), ("SKU-6", "BAHÇE")], Now.AddMinutes(20));
            Assert.AreEqual("Bahçe ürünleri", store.Products().Single(p => p.Sku == "SKU-6").Category);
            Assert.AreEqual(4, queue.Find(a.Id, K("bahçe"))!.Count, "a mapped value is not evidence any more"); Assert.AreEqual(CategoryReviewItem.Approved, queue.Find(a.Id, K("bahçe"))!.Status);
            Assert.AreEqual(1, queue.List(CategoryReviewItem.Pending).Count, "source B's row is still its own to review");
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
