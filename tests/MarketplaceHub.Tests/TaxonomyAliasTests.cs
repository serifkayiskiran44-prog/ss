using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #912 (TAXONOMY: category alias dictionary). A supplier's spellings of one local category are aliases keyed by the
// display fold (Turkish casing met), each naming exactly one active category; the same alias again is the same
// alias, for another category a conflict; another category's own name cannot be an alias; only an approved alias
// is applied, by the import, on an exact match -- a pending one is a suggestion; it all survives a restart.
[TestClass]
public sealed class TaxonomyAliasTests
{
    static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void AliasesFoldTurkishCasingRefuseDuplicatesAndConflictsAndApplyOnlyWhenApproved()
    {
        var root = Path.Combine(Path.GetTempPath(), "alias-" + Guid.NewGuid().ToString("N"));
        try
        {
            var taxonomy = new TaxonomyStore(root); var aliases = new TaxonomyAliasStore(root);
            var electronics = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "Elektronik", Value = "" });
            var computers = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "Bilgisayar", Value = "" });
            var brand = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Brand, Name = "Acme", Value = "Acme" });

            // The key folds Turkish casing: dotted and dotless i meet, whitespace collapses, other letters stay significant.
            Assert.AreEqual(TaxonomyAliasStore.Key("ELEKTRONİK  ürünleri"), TaxonomyAliasStore.Key("elektronık ürünleri")); Assert.AreEqual(TaxonomyAliasStore.Key("Elektronik Ürünleri"), TaxonomyAliasStore.Key("elektronik ürünleri"));
            Assert.AreNotEqual(TaxonomyAliasStore.Key("çocuk"), TaxonomyAliasStore.Key("cocuk"), "a diacritic other than the i's is not folded away");

            // An approved alias resolves in every spelling; a pending one is a suggestion and resolves nowhere.
            var saved = aliases.Save("ELEKTRONİK ürünleri", electronics.Id, approved: true, source: "manual");
            Assert.AreEqual(TaxonomyAliasView.ApprovedStatus, saved.Status); Assert.AreEqual("Elektronik", saved.CategoryName); Assert.AreEqual("ELEKTRONİK ürünleri", saved.Alias);
            Assert.AreEqual("Elektronik", aliases.Resolve("elektronık ürünlerİ")!.CategoryName); Assert.AreEqual(electronics.Id, aliases.Resolve("Elektronik Ürünleri")!.LocalId);
            Assert.IsNull(aliases.Resolve("Elektronik ürünleri ve aksesuar"), "exact key match only; a longer text is not an alias");
            aliases.Save("bilgisayarlar", computers.Id, approved: false);
            Assert.IsNull(aliases.Resolve("Bilgisayarlar"), "a pending alias is not applied"); Assert.AreEqual(TaxonomyAliasView.Pending, aliases.List().Single(a => a.Alias == "bilgisayarlar").Status);

            // Duplicate: the same alias for the same category is the same alias -- one row, its approval updated.
            var again = aliases.Save("elektronik ürünleri", electronics.Id, approved: false); Assert.AreEqual(1, aliases.List().Count(a => a.Key == saved.Key)); Assert.IsFalse(again.Approved); Assert.IsNull(aliases.Resolve("ELEKTRONİK ürünleri"));
            aliases.Save("Elektronik ürünleri", electronics.Id, approved: true); Assert.IsNotNull(aliases.Resolve("ELEKTRONİK ürünleri"));

            // Conflict: the same alias for another category is refused and names the holder; another category's own name cannot be an alias; the category's own name is pointless; a brand or a missing id is not a target.
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => aliases.Save("Elektronik Ürünleri", computers.Id, true)).Message, "Elektronik");
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => aliases.Save("bilgisayar", electronics.Id, true)).Message, "başka bir kategorinin adı");
            Assert.ThrowsException<InvalidOperationException>(() => aliases.Save("ELEKTRONİK", electronics.Id, true));
            Assert.ThrowsException<InvalidOperationException>(() => aliases.Save("acme ürünleri", brand.Id, true));
            Assert.ThrowsException<InvalidOperationException>(() => aliases.Save("yok", "no-such-id", true));
            Assert.ThrowsException<InvalidOperationException>(() => aliases.Save("   ", electronics.Id, true));
            Assert.AreEqual(2, aliases.List().Count, "nothing was written by a refused save");

            // The approved map is what the import applies: exact keys only.
            var map = aliases.ApprovedMap(); Assert.AreEqual(1, map.Count);
            Assert.AreEqual("Elektronik", TaxonomyAliasStore.Match(map, "  elektronik   ÜRÜNLERİ ")); Assert.IsNull(TaxonomyAliasStore.Match(map, "bilgisayarlar")); Assert.IsNull(TaxonomyAliasStore.Match(map, "")); Assert.IsNull(TaxonomyAliasStore.Match(new Dictionary<string, string>(), "elektronik ürünleri"));

            // Restart: the dictionary reads back; an alias whose category went inactive is an orphan and applies no more; removal by any spelling.
            electronics.Active = false; taxonomy.Save(electronics);
            SqliteConnection.ClearAllPools();
            var reopened = new TaxonomyAliasStore(root);
            Assert.AreEqual(2, reopened.List().Count); Assert.AreEqual(TaxonomyAliasView.Orphan, reopened.List().Single(a => a.Key == saved.Key).Status); Assert.IsNull(reopened.Resolve("elektronik ürünleri")); Assert.AreEqual(0, reopened.ApprovedMap().Count);
            Assert.IsTrue(reopened.Remove("ELEKTRONIK ÜRÜNLERİ")); Assert.IsFalse(reopened.Remove("ELEKTRONIK ÜRÜNLERİ")); Assert.AreEqual(1, reopened.List().Count);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void TheImportAppliesOnlyAnApprovedExactAliasToANewProductsCategory()
    {
        var root = Path.Combine(Path.GetTempPath(), "alias-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var taxonomy = new TaxonomyStore(root); var aliases = new TaxonomyAliasStore(root); var store = new CatalogStore(root); var runs = new XmlRunStore(root);
            var electronics = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "Elektronik", Value = "" });
            var toys = taxonomy.Save(new TaxonomyEntry { Kind = TaxonomyKind.Category, Name = "Oyuncak", Value = "" });
            aliases.Save("ELEKTRONİK ürünleri", electronics.Id, approved: true, source: "manual");
            aliases.Save("oyuncaklar", toys.Id, approved: false, source: "manual");
            var a = new XmlSource { Name = "Tedarikçi A", Location = "https://feeds.example.com/a.xml", Enabled = true, IntervalMinutes = 30, ItemPath = "/p", Fields = new Dictionary<string, string> { ["Sku"] = "s", ["Name"] = "n", ["Cost"] = "c", ["Stock"] = "q", ["Category"] = "k" } };
            store.SaveSource(a);
            void Import((string Sku, string Category)[] rows, DateTime at)
            {
                var run = runs.Start(a.Id, Guid.NewGuid().ToString("N"), TimeSpan.FromMinutes(5), a.ConfigRevision);
                try
                {
                    store.Import(a, rows.Select(r => new CatalogProduct { SourceId = a.Id, SourceKind = "xml", Sku = r.Sku, Name = "Ürün " + r.Sku, Price = 10, Currency = "TRY", Cost = 4, Stock = 3, Category = r.Category }).ToList(), CancellationToken.None, new XmlImportContext { RunId = run, SourceRevision = a.ConfigRevision, ObservedAtUtc = at });
                    runs.Complete(run, new ImportSummary(0, 0, 0));
                }
                catch { runs.Fail(run, "test"); throw; }
            }

            // An approved alias in any casing becomes the canonical name; a pending alias and an unknown value stay as the feed wrote them.
            Import([("SKU-1", "elektronık ÜRÜNLERİ"), ("SKU-2", "Oyuncaklar"), ("SKU-3", "Bahçe"), ("SKU-5", "Bahçe"), ("SKU-6", "Bahçe")], Now);
            Assert.AreEqual("Elektronik", store.Products().Single(p => p.Sku == "SKU-1").Category);
            Assert.AreEqual("Oyuncaklar", store.Products().Single(p => p.Sku == "SKU-2").Category, "a pending alias is not applied");
            Assert.AreEqual("Bahçe", store.Products().Single(p => p.Sku == "SKU-3").Category, "no alias, no change");

            // The next run of the feed leaves an existing product's category where the import already leaves it (the feed writes a category only to a new product).
            aliases.Save("oyuncaklar", toys.Id, approved: true);
            Import([("SKU-1", "elektronık ÜRÜNLERİ"), ("SKU-2", "Oyuncaklar"), ("SKU-3", "Bahçe"), ("SKU-5", "Bahçe"), ("SKU-6", "Bahçe"), ("SKU-4", "OYUNCAKLAR")], Now.AddMinutes(10)); // six rows after five: under the dropship count gate
            Assert.AreEqual("Oyuncaklar", store.Products().Single(p => p.Sku == "SKU-2").Category); Assert.AreEqual("Oyuncak", store.Products().Single(p => p.Sku == "SKU-4").Category, "a new product takes the approved alias");
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
