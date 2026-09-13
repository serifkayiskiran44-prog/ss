using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #917 (TAXONOMY: brand mapping review queue). A supplier's brand string that is neither a local brand's name nor an
// approved alias lands in the queue with its evidence and a suggestion carrying confidence and reason; nothing merges
// silently -- only an approved alias on an exact key is applied by the import; a rejected suggestion is a false
// positive remembered for that string; duplicate canonical brands are reported, never picked between; it all
// survives a restart.
[TestClass]
public sealed class BrandMappingTests
{
    static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
    static TaxonomyEntry Brand(TaxonomyStore taxonomy, string name) => taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = name, Value = name });
    static string K(string value) => BrandMappingStore.Key(value);

    [TestMethod]
    public void SuggestionsCarryConfidenceAliasesApplyOnlyWhenApprovedAndFalsePositivesAreRemembered()
    {
        var root = Path.Combine(Path.GetTempPath(), "brand-" + Guid.NewGuid().ToString("N"));
        try
        {
            var taxonomy = new TaxonomyStore(root); var brands = new BrandMappingStore(root);
            var acme = Brand(taxonomy, "Acme"); var nordic = Brand(taxonomy, "Nordic Tools"); var vintage = Brand(taxonomy, "Vintage & Co"); Brand(taxonomy, "Ikea");

            // Suggestions: the same name in another casing, a spelling a letter or two away, one name containing the other, or nothing -- with confidence and reason; never applied.
            var exact = brands.Suggest("ACME")!; Assert.AreEqual(BrandSuggestion.Exact, exact.Confidence); Assert.AreEqual(acme.Id, exact.BrandId);
            var typo = brands.Suggest("Acmee")!; Assert.AreEqual(BrandSuggestion.Typo, typo.Confidence); Assert.AreEqual("Acme", typo.BrandName); StringAssert.Contains(typo.Reason, "1 harf");
            var partial = brands.Suggest("Nordic Tools Ltd.")!; Assert.AreEqual(BrandSuggestion.Partial, partial.Confidence); Assert.AreEqual(nordic.Id, partial.BrandId);
            Assert.IsNull(brands.Suggest("Zeta")); Assert.IsNull(brands.Suggest("Ame"), "a short string is not a typo of anything"); Assert.IsNull(brands.Suggest(""));
            Assert.AreEqual(1, BrandMappingStore.Distance("acme", "acmee")); Assert.AreEqual(3, BrandMappingStore.Distance("kitten", "sitting"));
            Assert.IsNull(brands.Resolve("ACME"), "a suggestion is not an alias: nothing is applied by itself"); Assert.AreEqual("Acme", BrandMappingStore.Match(brands.KnownMap(), "acme"), "a brand's own name in another casing is the brand, not a merge");

            // Aliases: bound to one active brand, resolved in every casing; the same alias again updated in place; a brand's own or another brand's name refused; a conflict refused by name.
            var alias = brands.SaveAlias("ACME Ltd.", acme.Id, approved: true); Assert.AreEqual("Acme", alias.BrandName); Assert.AreEqual(BrandAliasView.ApprovedStatus, alias.Status);
            Assert.AreEqual("Acme", brands.Resolve("acme ltd.")); Assert.AreEqual("Acme", BrandMappingStore.Match(brands.KnownMap(), "Acme LTD."));
            brands.SaveAlias("acme ltd.", acme.Id, approved: false); Assert.AreEqual(1, brands.ListAliases().Count); Assert.IsNull(brands.Resolve("ACME Ltd."), "a pending alias is not applied");
            brands.SaveAlias("ACME Ltd.", acme.Id, approved: true);
            Assert.ThrowsException<InvalidOperationException>(() => brands.SaveAlias("acme", acme.Id, true), "its own name");
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => brands.SaveAlias("Nordic Tools", acme.Id, true)).Message, "başka bir markanın adı");
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => brands.SaveAlias("ACME LTD.", nordic.Id, true)).Message, "Acme");
            Assert.ThrowsException<InvalidOperationException>(() => brands.SaveAlias("x", "no-such", true)); Assert.AreEqual(1, brands.ListAliases().Count, "nothing was written by a refused save");

            // Duplicate canonicals: two active brands that fold to one key are reported, never chosen between -- the taxonomy store's own guard refuses an ASCII-case twin ("ACME" beside "Acme"), so the duplicates it lets through are the Unicode-cased ones the fold meets.
            Assert.AreEqual(0, brands.Duplicates().Count);
            Assert.ThrowsException<InvalidOperationException>(() => Brand(taxonomy, "ACME"), "the taxonomy store refuses an ASCII-case twin itself");
            var shadow = Brand(taxonomy, "İKEA"); var duplicate = brands.Duplicates().Single(); Assert.AreEqual(K("ikea"), duplicate.Key); CollectionAssert.AreEquivalent(new[] { "Ikea", "İKEA" }, duplicate.Names.ToList());
            shadow.Active = false; taxonomy.Save(shadow); Assert.AreEqual(0, brands.Duplicates().Count);

            // The queue: evidence per source and string, the suggestion stored; a repeat grows the count; approving is the operator's choice through the alias rules; rejecting remembers the false positive and the brand is not suggested again.
            Assert.AreEqual(2, brands.Record("src-a", [new("Acmee", "SKU-1"), new("acmee", "SKU-2"), new("Zeta", "SKU-3")], Now));
            var queued = brands.Find("src-a", K("acmee"))!; Assert.AreEqual(2, queued.Count); Assert.AreEqual("SKU-1", queued.SampleSku); Assert.AreEqual(BrandSuggestion.Typo, queued.Confidence); Assert.AreEqual("Acme", queued.SuggestedBrand); Assert.AreEqual(BrandReviewItem.Pending, queued.Status);
            Assert.AreEqual(0, brands.Find("src-a", K("zeta"))!.Confidence); Assert.AreEqual("", brands.Find("src-a", K("zeta"))!.SuggestedBrand);
            brands.Record("src-b", [new("Acmee", "B-1")], Now.AddHours(1)); Assert.AreEqual(1, brands.Find("src-b", K("acmee"))!.Count); Assert.AreEqual(2, brands.Find("src-a", K("acmee"))!.Count, "a source's evidence is its own");
            var approved = brands.Approve("src-a", K("acmee"), acme.Id); Assert.AreEqual(BrandReviewItem.Approved, approved.Status); Assert.AreEqual("Acme", brands.Resolve("ACMEE")); StringAssert.Contains(brands.ListAliases().Single(a => a.Key == K("acmee")).Source, "src-a");
            Assert.ThrowsException<InvalidOperationException>(() => brands.Approve("src-a", K("zeta"), "no-such"), "the alias rules apply"); Assert.AreEqual(BrandReviewItem.Pending, brands.Find("src-a", K("zeta"))!.Status);
            brands.Record("src-a", [new("Vintage & Co Ltd", "SKU-7")], Now.AddHours(2)); var falsePositive = brands.Find("src-a", K("vintage & co ltd"))!; Assert.AreEqual(vintage.Id, falsePositive.SuggestedBrandId, "a partial match was suggested"); Assert.AreEqual(BrandSuggestion.Partial, falsePositive.Confidence);
            var rejected = brands.Reject("src-a", K("vintage & co ltd")); Assert.AreEqual(BrandReviewItem.Rejected, rejected.Status); Assert.AreEqual(vintage.Id, rejected.RejectedBrandId); Assert.AreEqual("", rejected.SuggestedBrandId);
            brands.Record("src-a", [new("Vintage & Co Ltd", "SKU-8")], Now.AddHours(3)); var again = brands.Find("src-a", K("vintage & co ltd"))!; Assert.AreEqual(BrandReviewItem.Pending, again.Status); Assert.AreEqual("", again.SuggestedBrandId, "the rejected brand is not suggested again"); Assert.AreEqual(2, again.Count);
            Assert.IsNull(brands.Resolve("Vintage & Co Ltd"), "nothing merged silently");

            // Restart.
            SqliteConnection.ClearAllPools();
            var reopened = new BrandMappingStore(root);
            Assert.AreEqual(2, reopened.ListAliases().Count); Assert.AreEqual(4, reopened.List().Count); Assert.AreEqual("Acme", reopened.Resolve("acmee")); Assert.AreEqual(vintage.Id, reopened.Find("src-a", K("vintage & co ltd"))!.RejectedBrandId);
            Assert.IsTrue(reopened.RemoveAlias("ACMEE")); Assert.IsNull(reopened.Resolve("acmee"));
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void TheImportAppliesOnlyExactKnownBrandsAndQueuesTheRestWithSuggestions()
    {
        var root = Path.Combine(Path.GetTempPath(), "brand-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var taxonomy = new TaxonomyStore(root); var brands = new BrandMappingStore(root); var store = new CatalogStore(root); var runs = new XmlRunStore(root);
            var acme = Brand(taxonomy, "Acme");
            brands.SaveAlias("ACME Ltd.", acme.Id, approved: true);
            brands.SaveAlias("acme corp", acme.Id, approved: false);
            var a = new XmlSource { Name = "Tedarikçi A", Location = "https://feeds.example.com/a.xml", Enabled = true, IntervalMinutes = 30, ItemPath = "/p", Fields = new Dictionary<string, string> { ["Sku"] = "s", ["Name"] = "n", ["Cost"] = "c", ["Stock"] = "q", ["Brand"] = "b" } };
            store.SaveSource(a);
            void Import((string Sku, string Brand)[] rows, DateTime at)
            {
                var run = runs.Start(a.Id, Guid.NewGuid().ToString("N"), TimeSpan.FromMinutes(5), a.ConfigRevision);
                try
                {
                    store.Import(a, rows.Select(r => new CatalogProduct { SourceId = a.Id, SourceKind = "xml", Sku = r.Sku, Name = "Ürün " + r.Sku, Price = 10, Currency = "TRY", Cost = 4, Stock = 3, Brand = r.Brand }).ToList(), CancellationToken.None, new XmlImportContext { RunId = run, SourceRevision = a.ConfigRevision, ObservedAtUtc = at });
                    runs.Complete(run, new ImportSummary(0, 0, 0));
                }
                catch { runs.Fail(run, "test"); throw; }
            }

            // Exact: a brand's own name in another casing and an approved alias become the canonical name; a pending alias, a typo and an unknown string stay as written and are queued with their suggestion.
            Import([("SKU-1", "ACME"), ("SKU-2", "acme ltd."), ("SKU-3", "acme corp"), ("SKU-4", "Acmee"), ("SKU-5", "Zeta"), ("SKU-6", "")], Now);
            var products = store.Products();
            Assert.AreEqual("Acme", products.Single(p => p.Sku == "SKU-1").Brand); Assert.AreEqual("Acme", products.Single(p => p.Sku == "SKU-2").Brand);
            Assert.AreEqual("acme corp", products.Single(p => p.Sku == "SKU-3").Brand, "a pending alias is not applied"); Assert.AreEqual("Acmee", products.Single(p => p.Sku == "SKU-4").Brand, "a typo is a suggestion, never a silent merge"); Assert.AreEqual("Zeta", products.Single(p => p.Sku == "SKU-5").Brand);
            var pending = brands.List(BrandReviewItem.Pending, a.Id); Assert.AreEqual(3, pending.Count);
            var typo = pending.Single(i => i.Key == K("acmee")); Assert.AreEqual(BrandSuggestion.Typo, typo.Confidence); Assert.AreEqual("Acme", typo.SuggestedBrand); Assert.AreEqual("SKU-4", typo.SampleSku);
            Assert.AreEqual(BrandSuggestion.Partial, pending.Single(i => i.Key == K("acme corp")).Confidence, "a pending alias's string is queued with whatever the names suggest -- here a partial match");
            Assert.AreEqual(0, pending.Single(i => i.Key == K("zeta")).Confidence);

            // Approved from the queue: the next run applies it to a new product; the rest still waits.
            brands.Approve(a.Id, K("acmee"), acme.Id);
            Import([("SKU-1", "ACME"), ("SKU-2", "acme ltd."), ("SKU-3", "acme corp"), ("SKU-4", "Acmee"), ("SKU-5", "Zeta"), ("SKU-6", ""), ("SKU-7", "ACMEE")], Now.AddMinutes(10));
            Assert.AreEqual("Acme", store.Products().Single(p => p.Sku == "SKU-7").Brand);
            Assert.AreEqual(1, brands.Find(a.Id, K("acmee"))!.Count, "an approved string is known now and queues no more; its row keeps the evidence it had"); Assert.AreEqual(2, brands.List(BrandReviewItem.Pending, a.Id).Count);
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
