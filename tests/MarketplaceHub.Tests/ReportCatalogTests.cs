using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #846 (DESIGN: Report catalog card layout). A card says purpose, scope, last run, saved filters and outputs; a
// long title is trimmed on the card and kept whole for the tooltip; a store-scoped report is hidden when no store
// is offered; zero and fifty definitions; a search; runs for keys outside the catalog are ignored; the run store
// keeps the latest per report and never a path or a secret.
[TestClass]
public sealed class ReportCatalogTests
{
    static readonly DateTime Now = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);
    static ReportDefinition Def(string key, ReportScope scope = ReportScope.Global, string? title = null) => new(key, title ?? "Rapor " + key, "Amaç " + key, "Kaynak", scope, new[] { "Ekranda" }, "dashboard");

    [TestMethod]
    public void CardsDescribeRunsScopeFiltersOutputsAndTrimLongTitles()
    {
        var never = ReportCatalog.Describe(Def("a"), null, 0, new[] { "etsy|S1" }, Now);
        Assert.AreEqual("Hiç çalıştırılmadı", never.LastRunText); Assert.AreEqual(ReportRunState.Never, never.LastRunState); Assert.AreEqual("Kayıtlı filtre yok", never.SavedFiltersText); Assert.AreEqual("Çıktı: Ekranda", never.OutputsText);
        Assert.IsTrue(never.Visible); Assert.AreEqual("Kapsam: tüm veri · Kaynak", never.ScopeText); Assert.IsFalse(never.TitleTrimmed); Assert.AreEqual("Rapor a", never.DisplayTitle);

        var ran = ReportCatalog.Describe(Def("b", ReportScope.Store), new ReportRun("b", "etsy|S1", Now.AddMinutes(-5), Now.AddMinutes(-3), ReportRunState.Succeeded, 1234, ""), 2, new[] { "etsy|S1", "ebay|E1" }, Now);
        Assert.AreEqual($"Son çalıştırma: 3 dk önce · başarılı · {1234:N0} satır", ran.LastRunText); Assert.AreEqual(ReportRunState.Succeeded, ran.LastRunState);
        Assert.AreEqual("2 kayıtlı filtre", ran.SavedFiltersText); Assert.AreEqual("Kapsam: 2 sunulan mağaza · Kaynak", ran.ScopeText); Assert.IsTrue(ran.Visible);

        var failed = ReportCatalog.Describe(Def("c"), new ReportRun("c", "", Now.AddHours(-3), Now.AddHours(-2), ReportRunState.Failed, 0, ""), 0, null, Now);
        Assert.AreEqual("Son çalıştırma: 2 sa önce · başarısız", failed.LastRunText, "A failed run carries no row count.");
        var cancelled = ReportCatalog.Describe(Def("c"), new ReportRun("c", "", Now.AddDays(-2), Now.AddDays(-1), ReportRunState.Cancelled, 5, ""), 0, null, Now);
        Assert.AreEqual("Son çalıştırma: 1 gün önce · iptal edildi", cancelled.LastRunText);

        Assert.IsFalse(ReportCatalog.Describe(Def("d", ReportScope.Store), null, 0, Array.Empty<string>(), Now).Visible, "No offered store, no store-scoped card.");
        var unknownStores = ReportCatalog.Describe(Def("d", ReportScope.Store), null, 0, null, Now);
        Assert.IsTrue(unknownStores.Visible); Assert.AreEqual("Kapsam: tüm mağazalar · Kaynak", unknownStores.ScopeText);
        Assert.IsTrue(ReportCatalog.Describe(Def("e", ReportScope.Global), null, 0, Array.Empty<string>(), Now).Visible, "A global report never depends on a store.");

        var longTitle = ReportCatalog.Describe(Def("f", title: new string('R', 80)), null, 0, null, Now);
        Assert.IsTrue(longTitle.TitleTrimmed); Assert.AreEqual(ReportCatalog.TitleLimit, longTitle.DisplayTitle.Length); StringAssert.EndsWith(longTitle.DisplayTitle, "…");
        Assert.AreEqual(80, longTitle.Title.Length); StringAssert.StartsWith(longTitle.AccessibleName, longTitle.Title, "The accessible name carries the whole title.");
        StringAssert.Contains(longTitle.AccessibleName, "Hiç çalıştırılmadı");
    }

    [TestMethod]
    public void BuildHandlesZeroAndFiftyReportsSearchesAndIgnoresRunsOutsideTheCatalog()
    {
        var none = ReportCatalog.Build(Array.Empty<ReportDefinition>(), null, null, null, "", Now);
        Assert.IsTrue(none.IsEmpty); Assert.AreEqual("Gösterilecek rapor yok.", none.EmptyText); Assert.AreEqual(0, none.Total);

        var fifty = Enumerable.Range(0, 50).Select(i => Def($"r-{i:D2}", i % 2 == 0 ? ReportScope.Global : ReportScope.Store)).ToList();
        var run = new ReportRun("r-01", "", Now.AddMinutes(-2), Now.AddMinutes(-1), ReportRunState.Succeeded, 7, "");
        var all = ReportCatalog.Build(fifty, new Dictionary<string, ReportRun> { ["r-01"] = run, ["ghost"] = run with { ReportKey = "ghost" } }, new Dictionary<string, int> { ["r-02"] = 3, ["ghost"] = 9 }, new[] { "etsy|S1" }, null, Now);
        Assert.AreEqual(50, all.Cards.Count); Assert.AreEqual(0, all.Hidden); Assert.AreEqual(50, all.Total); Assert.IsFalse(all.IsEmpty);
        Assert.IsFalse(all.Cards.Any(c => c.Key == "ghost"), "A run or filter for a key outside the catalog never becomes a card.");
        StringAssert.Contains(all.Cards.Single(c => c.Key == "r-01").LastRunText, "1 dk önce · başarılı · 7 satır"); Assert.AreEqual("3 kayıtlı filtre", all.Cards.Single(c => c.Key == "r-02").SavedFiltersText);
        CollectionAssert.AreEqual(fifty.Select(d => d.Key).ToList(), all.Cards.Select(c => c.Key).ToList(), "Catalog order is kept.");

        var noStores = ReportCatalog.Build(fifty, null, null, Array.Empty<string>(), null, Now);
        Assert.AreEqual(25, noStores.Cards.Count); Assert.AreEqual(25, noStores.Hidden); Assert.IsTrue(noStores.Cards.All(c => c.ScopeText.StartsWith("Kapsam: tüm veri", StringComparison.Ordinal)));
        var onlyStores = ReportCatalog.Build(fifty.Where(d => d.Scope == ReportScope.Store), null, null, Array.Empty<string>(), null, Now);
        Assert.IsTrue(onlyStores.IsEmpty); StringAssert.Contains(onlyStores.EmptyText, "mağaza kapsamlı raporlar gizli");

        var query = ReportCatalog.Build(fifty, null, null, null, " r-4 ", Now);
        Assert.AreEqual(10, query.Cards.Count, "r-40 … r-49 match the trimmed query."); Assert.AreEqual(0, query.Hidden);
        var nothing = ReportCatalog.Build(fifty, null, null, null, "böyle-bir-rapor-yok", Now);
        Assert.IsTrue(nothing.IsEmpty); StringAssert.Contains(nothing.EmptyText, "Aramaya uyan rapor yok");

        Assert.AreEqual(ReportCatalog.Definitions.Count, ReportCatalog.Definitions.Select(d => d.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count(), "Catalog keys are unique.");
        Assert.IsTrue(ReportCatalog.Definitions.All(d => d.Outputs.Count > 0 && d.Purpose.Length > 0 && d.Sources.Length > 0 && d.Route.Length > 0 && d.Title.Length <= ReportCatalog.TitleLimit));
        Assert.IsNotNull(ReportCatalog.Find(" Products-XLSX ")); Assert.IsNull(ReportCatalog.Find("ghost"));
    }

    [TestMethod]
    public void TheRunStoreKeepsTheLatestPerReportAndNeverAPathOrASecret()
    {
        var root = Path.Combine(Path.GetTempPath(), "report-runs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ReportRunStore(root);
            Assert.AreEqual(0, store.Latest().Count);
            var first = store.Record("Products-XLSX", DateTime.UtcNow.AddSeconds(-10), ReportRunState.Succeeded, 12, @"C:\Users\ali\Desktop\urunler.xlsx");
            Assert.AreEqual("products-xlsx", first.ReportKey); Assert.AreEqual("urunler.xlsx", first.Note, "A note keeps the file name, never the location.");
            var second = store.Record("products-xlsx", DateTime.UtcNow.AddSeconds(-5), ReportRunState.Failed, -3, "Yazılamadı token=abc123 password=hunter2");
            Assert.AreEqual(0, second.RowCount); Assert.IsFalse(second.Note.Contains("abc123") || second.Note.Contains("hunter2"), second.Note);
            store.Record("support-package", DateTime.UtcNow.AddSeconds(-1), ReportRunState.Cancelled, 0, storeKey: "etsy|S1");

            var latest = store.Latest();
            Assert.AreEqual(2, latest.Count); Assert.AreEqual(ReportRunState.Failed, latest["products-xlsx"].State, "The newest run wins."); Assert.AreEqual("etsy|S1", latest["support-package"].StoreKey);
            Assert.AreEqual(2, store.Recent("products-xlsx").Count); Assert.AreEqual(ReportRunState.Failed, store.Recent("products-xlsx", 1)[0].State); Assert.AreEqual(0, store.Recent("ghost").Count);

            Assert.ThrowsException<ArgumentException>(() => store.Record("../evil", DateTime.UtcNow, ReportRunState.Succeeded, 0));
            Assert.ThrowsException<ArgumentException>(() => store.Record("", DateTime.UtcNow, ReportRunState.Succeeded, 0));
            Assert.ThrowsException<ArgumentException>(() => store.Record("x", DateTime.UtcNow, ReportRunState.Never, 0));
            Assert.AreEqual("", ReportRunStore.SafeNote("   ")); Assert.AreEqual("rapor.csv", ReportRunStore.SafeNote("/tmp/out/rapor.csv"));
        }
        finally
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
                catch (IOException) { Thread.Sleep(200); }
                catch (UnauthorizedAccessException) { Thread.Sleep(200); }
            }
        }
    }
}
