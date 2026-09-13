using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #868 (DESIGN: Turkish casing-safe UI search). One fold for every display search: Unicode-normalized, lower-cased
// by Turkish rules (İ is i, I is ı) and tolerant of an ASCII keyboard (the dotted and dotless i meet), so "istanbul",
// "ISTANBUL", "İSTANBUL" and "ıstanbul" all find "İstanbul Çorap"; other diacritics stay significant; an empty query
// matches everything. The product page's SQL search, the in-memory product filter, the report cards and the column
// chooser go through it. Identity keys (SKU, barcode, external keys) are not touched. A search never leaves an
// audit trail (a query can be personal data).
[TestClass]
public sealed class UiSearchTests
{
    [TestMethod]
    public void TheIFamilyFoldsBothWaysUnicodeIsNormalizedAndAnEmptyQueryMatchesEverything()
    {
        foreach (var (text, query) in new[]
        {
            ("İSTANBUL", "istanbul"), ("İstanbul", "ISTANBUL"), ("istanbul", "İSTANBUL"), ("ıstanbul", "İstanbul"), ("Istanbul", "ıstanbul"),
            ("DİYARBAKIR", "diyarbakır"), ("Diyarbakır", "DIYARBAKIR"), ("iSTANbul", "İstanbul"), ("ILIK çay", "ılık"), ("ılık çay", "ilik"), ("Şişli İş Merkezi", "şişli iş"),
        })
            Assert.IsTrue(UiSearch.Matches(text, query), $"'{query}' in '{text}'");

        // Other diacritics stay significant; a different word is not found.
        Assert.IsFalse(UiSearch.Matches("çocuk", "cocuk")); Assert.IsFalse(UiSearch.Matches("şişe", "sise")); Assert.IsFalse(UiSearch.Matches("İstanbul", "ankara"));

        // Unicode: a decomposed İ (I with a combining dot) and a decomposed ğ are the same letters; an emoji does not break the fold.
        Assert.IsTrue(UiSearch.Matches("İstanbul", "İstanbul")); Assert.IsTrue(UiSearch.Matches("İstanbul", "İstanbul")); Assert.IsTrue(UiSearch.Matches("Dağlı", "dağlı"));
        Assert.IsTrue(UiSearch.Matches("🍎 Elma İzmir", "elma iz")); Assert.IsTrue(UiSearch.Matches("İzmir 🍎", "🍎"));

        // An empty or blank query matches everything; nothing but the empty query matches an empty text.
        Assert.IsTrue(UiSearch.Matches("anything", "")); Assert.IsTrue(UiSearch.Matches("anything", "   ")); Assert.IsTrue(UiSearch.Matches(null, "")); Assert.IsTrue(UiSearch.Matches("", null));
        Assert.IsFalse(UiSearch.Matches(null, "a")); Assert.IsFalse(UiSearch.Matches("", "a"));

        // The fold is one function both sides share.
        Assert.AreEqual(UiSearch.Fold("İSTANBUL"), UiSearch.Fold("istanbul")); Assert.AreEqual(UiSearch.Fold("ISTANBUL"), UiSearch.Fold("ıstanbul")); Assert.AreEqual("", UiSearch.Fold(null)); Assert.AreEqual("istanbul", UiSearch.Fold("İSTANBUL"));
    }

    [TestMethod]
    public void TheProductStoreTheReportCardsAndTheColumnChooserSearchTheSameWayAndLeaveNoAuditTrail()
    {
        var root = Path.Combine(Path.GetTempPath(), "ui-search-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CatalogStore(root); var source = new XmlSource { Id = "feed-1", Name = "Fixture feed" };
            store.Import(source, new[]
            {
                new CatalogProduct { SourceId = source.Id, Sku = "SKU-İ1", Name = "İstanbul Çorap", Price = 10, Stock = 30, Currency = "TRY" },
                new CatalogProduct { SourceId = source.Id, Sku = "SKU-I2", Name = "ISTANBUL Terlik", Price = 10, Stock = 30, Currency = "TRY" },
                new CatalogProduct { SourceId = source.Id, Sku = "SKU-3", Name = "ılık Çay", Price = 10, Stock = 30, Currency = "TRY" },
                new CatalogProduct { SourceId = source.Id, Sku = "SKU-4", Name = "Ankara Simit", Price = 10, Stock = 30, Currency = "TRY" },
            });

            // The product page's SQL search: every spelling of the i-family finds both İstanbul products; the dotless word is found dotted and undotted; the SKU still finds itself.
            foreach (var query in new[] { "istanbul", "ISTANBUL", "İSTANBUL", "ıstanbul" })
                Assert.AreEqual(2, store.Search(query).Total, $"SQL search '{query}'");
            Assert.AreEqual(1, store.Search("İstanbul Ç").Total, "a longer query narrows as it should"); Assert.AreEqual(1, store.Search("istanbul t").Total);
            Assert.AreEqual(1, store.Search("ilik").Total); Assert.AreEqual(1, store.Search("ILIK").Total); Assert.AreEqual(4, store.Search("").Total); Assert.AreEqual(0, store.Search("izmir").Total);
            Assert.AreEqual(1, store.Search("sku-i1").Total); Assert.AreEqual(1, store.Search("SKU-İ1").Total); Assert.AreEqual(0, store.Search("100%").Total, "a LIKE wildcard in the query stays a literal");
            // The in-memory product filter agrees.
            Assert.AreEqual(2, store.Products("istanbul").Count); Assert.AreEqual(2, store.Products("ISTANBUL").Count); Assert.AreEqual(1, store.Products("ılık").Count); Assert.AreEqual(4, store.Products("").Count);
            // Identity keys are untouched: the exact SKU is the key, as before.
            Assert.AreEqual("SKU-İ1", store.Products("SKU-İ1").Single().Sku);

            // The report cards and the column chooser fold the same way.
            var now = DateTime.UtcNow;
            var lower = ReportCatalog.Build(ReportCatalog.Definitions, null, null, null, "sipariş", now).Cards.Count; var upper = ReportCatalog.Build(ReportCatalog.Definitions, null, null, null, "SİPARİŞ", now).Cards.Count; var ascii = ReportCatalog.Build(ReportCatalog.Definitions, null, null, null, "SIPARIS", now).Cards.Count;
            Assert.IsTrue(lower > 0, "some report speaks of orders"); Assert.AreEqual(lower, upper); Assert.IsTrue(ascii <= lower, "an ASCII S is not a Ş");
            var layout = ReportColumns.Resolve(ReportColumns.OrdersSchema, null, true);
            Assert.IsTrue(ReportColumns.Grouped(layout, "sipariş").Count > 0); Assert.AreEqual(ReportColumns.Grouped(layout, "sipariş").Count, ReportColumns.Grouped(layout, "SİPARİŞ").Count);

            // No search leaves a trail: an address typed into a search is in no audit event.
            store.Search("ali@example.com"); store.Products("ali@example.com"); ReportCatalog.Build(ReportCatalog.Definitions, null, null, null, "ali@example.com", now);
            Assert.IsFalse(new AuditStore(root).List(500, null).Any(e => $"{e.Action} {e.Detail} {e.Outcome}".Contains("ali@example.com")), "a query is never logged");
        }
        finally
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
                catch (IOException) { Thread.Sleep(300); }
                catch (UnauthorizedAccessException) { Thread.Sleep(300); }
            }
        }
    }
}
