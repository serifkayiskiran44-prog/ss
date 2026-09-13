using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #921 (TAXONOMY: mapping coverage report). Per feed source, per channel shop and per channel, every product is
// counted into mapped / unmapped / stale for its category, brand and attributes; a row is empty, partial, stale or
// full; every figure drills down to the real products behind it, unmapped first; a channel is the sum of its shops.
[TestClass]
public sealed class MappingCoverageTests
{
    static CatalogProduct P(string sku, string category, string brand, string attributes) => new() { SourceId = "fixture", Sku = sku, Name = "Ürün " + sku, Price = 10, Currency = "TRY", Cost = 4, Stock = 1, Category = category, Brand = brand, AttributesText = attributes };
    static MarketplaceConnection Conn(string channel, string shop) => new(channel + ":" + shop, channel, shop, channel + " " + shop, true, "", null, "");

    /// <summary>Seven products in two sources, one saved source without products; Elektronik and Bahçe mapped on etsy/S1 with the Acme brand and the Renk=Kırmızı value; Elektronik alone on etsy/S2 by a mapping 200 days old; nothing on ebay/E1; Mutfak deactivated under SKU-5.</summary>
    static (CatalogStore Store, TaxonomyStore Taxonomy, DateTime Now) Seed(string root)
    {
        var store = new CatalogStore(root); var taxonomy = new TaxonomyStore(root); var rules = new CategoryAttributeRuleStore(root); var now = DateTime.UtcNow;
        TaxonomyEntry Save(TaxonomyKind kind, string name, string value = "") => taxonomy.Save(new TaxonomyEntry { Kind = kind, Name = name, Value = value });
        var elektronik = Save(TaxonomyKind.Category, "Elektronik"); var bahce = Save(TaxonomyKind.Category, "Bahçe"); var mutfak = Save(TaxonomyKind.Category, "Mutfak");
        var acme = Save(TaxonomyKind.Brand, "Acme"); var red = Save(TaxonomyKind.Attribute, "Renk", "Kırmızı"); Save(TaxonomyKind.Attribute, "Renk", "Mavi");
        rules.Save(elektronik.Id, "Renk", true);
        var fixture = new XmlSource { Id = "fixture", Name = "Fixture" }; var fixture2 = new XmlSource { Id = "fixture-2", Name = "Fixture 2" };
        store.SaveSource(fixture); store.SaveSource(fixture2); store.SaveSource(new XmlSource { Id = "empty", Name = "Boş kaynak" });
        store.Import(fixture, new[] { P("SKU-1", "Elektronik", "Acme", "Renk=Kırmızı"), P("SKU-2", "Bahçe", "", ""), P("SKU-3", "Yok Böyle", "Nadir", "Renk=Mavi"), P("SKU-4", "", "Acme", ""), P("SKU-5", "Mutfak", "acme", "Renk=Yeşil") });
        var second = new[] { P("F2-1", "Elektronik", "Acme", "Renk=Kırmızı"), P("F2-2", "Elektronik", "Acme", "Renk=Kırmızı") }; foreach (var p in second) p.SourceId = "fixture-2";
        store.Import(fixture2, second);
        mutfak.Active = false; taxonomy.Save(mutfak);
        taxonomy.Map(TaxonomyKind.Category, "e-elek", elektronik.Id, "etsy", "S1"); taxonomy.Map(TaxonomyKind.Category, "e-bahce", bahce.Id, "etsy", "S1");
        taxonomy.Map(TaxonomyKind.Brand, "b-acme", acme.Id, "etsy", "S1"); taxonomy.Map(TaxonomyKind.Attribute, "a-red", red.Id, "etsy", "S1");
        taxonomy.Map(TaxonomyKind.Category, "e2-elek", elektronik.Id, "etsy", "S2");
        using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString()))
        {
            c.Open();
            using (var cmd = c.CreateCommand()) { cmd.CommandText = "UPDATE TaxonomyEntries SET UpdatedUtc=$t WHERE Id=$id"; cmd.Parameters.AddWithValue("$t", now.AddDays(-300).ToString("O", CultureInfo.InvariantCulture)); cmd.Parameters.AddWithValue("$id", elektronik.Id); Assert.AreEqual(1, cmd.ExecuteNonQuery()); }
            using (var cmd = c.CreateCommand()) { cmd.CommandText = "UPDATE TaxonomyMappings SET UpdatedUtc=$t WHERE Marketplace='etsy' AND ShopId='S2' AND ExternalKey='e2-elek'"; cmd.Parameters.AddWithValue("$t", now.AddDays(-200).ToString("O", CultureInfo.InvariantCulture)); Assert.AreEqual(1, cmd.ExecuteNonQuery()); }
        }
        return (store, taxonomy, now);
    }

    static void AssertRow(MappingCoverageReport report, string scope, string scopeId, TaxonomyKind kind, int mapped, int unmapped, int stale, string state)
    {
        var row = report.Find(scope, scopeId, kind); Assert.IsNotNull(row, $"{scope}/{scopeId}/{kind}");
        Assert.AreEqual((mapped, unmapped, stale, mapped + unmapped + stale, state), (row!.Mapped, row.Unmapped, row.Stale, row.Total, row.State), $"{scope}/{scopeId}/{kind}");
    }

    [TestMethod]
    public void TheReportCountsEverySourceShopAndChannelIntoMappedUnmappedAndStaleWithEmptyPartialStaleAndFullStates()
    {
        var root = Path.Combine(Path.GetTempPath(), "coverage-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var (store, _, now) = Seed(root); var sources = store.Sources(); var products = store.Products();
            var report = MappingCoverage.Build(root, sources, products, new[] { Conn("etsy", "S1"), Conn("etsy", "S2"), Conn("ebay", "E1") }, now);

            // The dictionary side, per source: partial for the mixed source, full for the clean one, empty for the source without products.
            AssertRow(report, MappingCoverageRow.SourceScope, "fixture", TaxonomyKind.Category, 2, 2, 1, MappingCoverageRow.Partial);
            AssertRow(report, MappingCoverageRow.SourceScope, "fixture", TaxonomyKind.Brand, 3, 2, 0, MappingCoverageRow.Partial);
            AssertRow(report, MappingCoverageRow.SourceScope, "fixture", TaxonomyKind.Attribute, 3, 2, 0, MappingCoverageRow.Partial);
            foreach (var kind in new[] { TaxonomyKind.Category, TaxonomyKind.Brand, TaxonomyKind.Attribute }) { AssertRow(report, MappingCoverageRow.SourceScope, "fixture-2", kind, 2, 0, 0, MappingCoverageRow.Full); AssertRow(report, MappingCoverageRow.SourceScope, "empty", kind, 0, 0, 0, MappingCoverageRow.Empty); }
            Assert.AreEqual(40, report.Find(MappingCoverageRow.SourceScope, "fixture", TaxonomyKind.Category)!.Percent);
            Assert.AreEqual("Boş kaynak", report.Find(MappingCoverageRow.SourceScope, "empty", TaxonomyKind.Category)!.ScopeName);

            // The channel side, per shop (multi-store: two shops of one channel differ) and per channel (the sum of its shops).
            AssertRow(report, MappingCoverageRow.ShopScope, "etsy/S1", TaxonomyKind.Category, 4, 2, 1, MappingCoverageRow.Partial);
            AssertRow(report, MappingCoverageRow.ShopScope, "etsy/S1", TaxonomyKind.Brand, 5, 2, 0, MappingCoverageRow.Partial);
            AssertRow(report, MappingCoverageRow.ShopScope, "etsy/S1", TaxonomyKind.Attribute, 5, 2, 0, MappingCoverageRow.Partial);
            AssertRow(report, MappingCoverageRow.ShopScope, "etsy/S2", TaxonomyKind.Category, 0, 3, 4, MappingCoverageRow.Partial);
            AssertRow(report, MappingCoverageRow.ShopScope, "etsy/S2", TaxonomyKind.Brand, 0, 7, 0, MappingCoverageRow.Partial);
            AssertRow(report, MappingCoverageRow.ShopScope, "etsy/S2", TaxonomyKind.Attribute, 2, 5, 0, MappingCoverageRow.Partial);
            AssertRow(report, MappingCoverageRow.ShopScope, "ebay/E1", TaxonomyKind.Category, 0, 6, 1, MappingCoverageRow.Partial);
            AssertRow(report, MappingCoverageRow.ChannelScope, "etsy", TaxonomyKind.Category, 4, 5, 5, MappingCoverageRow.Partial);
            AssertRow(report, MappingCoverageRow.ChannelScope, "ebay", TaxonomyKind.Category, 0, 6, 1, MappingCoverageRow.Partial);
            Assert.AreEqual("Etsy", report.Find(MappingCoverageRow.ChannelScope, "etsy", TaxonomyKind.Category)!.ScopeName);
            Assert.AreEqual(3 * 3 + 2 * 3 + 3 * 3, report.Rows.Count, "three sources, two channels and three shops, three kinds each");

            // Full and stale: the clean source alone on the two shops -- everything mapped on S1, everything mapped but unconfirmed for 200 days on S2, the channel stale as their sum.
            var clean = MappingCoverage.Build(root, sources, products.Where(p => p.SourceId == "fixture-2").ToList(), new[] { Conn("etsy", "S1"), Conn("etsy", "S2") }, now);
            AssertRow(clean, MappingCoverageRow.ShopScope, "etsy/S1", TaxonomyKind.Category, 2, 0, 0, MappingCoverageRow.Full);
            AssertRow(clean, MappingCoverageRow.ShopScope, "etsy/S1", TaxonomyKind.Attribute, 2, 0, 0, MappingCoverageRow.Full);
            AssertRow(clean, MappingCoverageRow.ShopScope, "etsy/S2", TaxonomyKind.Category, 0, 0, 2, MappingCoverageRow.StaleState);
            AssertRow(clean, MappingCoverageRow.ShopScope, "etsy/S2", TaxonomyKind.Brand, 0, 2, 0, MappingCoverageRow.Partial);
            AssertRow(clean, MappingCoverageRow.ChannelScope, "etsy", TaxonomyKind.Category, 2, 0, 2, MappingCoverageRow.StaleState);
            AssertRow(clean, MappingCoverageRow.SourceScope, "fixture", TaxonomyKind.Category, 0, 0, 0, MappingCoverageRow.Empty);

            // Empty: no products at all -> every row empty; nothing at all -> no rows, and the summary says so.
            var empty = MappingCoverage.Build(root, sources, Array.Empty<CatalogProduct>(), new[] { Conn("etsy", "S1") }, now);
            Assert.AreEqual(3 * 3 + 3 + 3, empty.Rows.Count); Assert.IsTrue(empty.Rows.All(r => r.State == MappingCoverageRow.Empty && r.Total == 0));
            StringAssert.Contains(MappingCoverage.Describe(empty.Rows[0]), "kayıt yok");
            var nothing = MappingCoverage.Build(root, Array.Empty<XmlSource>(), Array.Empty<CatalogProduct>(), Array.Empty<MarketplaceConnection>(), now);
            Assert.AreEqual(0, nothing.Rows.Count); StringAssert.Contains(MappingCoverage.Summarize(nothing), "boş");
            StringAssert.Contains(MappingCoverage.Summarize(report), "satır");
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void EveryFigureDrillsDownToTheRealProductsUnmappedFirstAndAChannelDrillsIntoItsShops()
    {
        var root = Path.Combine(Path.GetTempPath(), "coverage-drill-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var (store, _, now) = Seed(root); var products = store.Products();
            var report = MappingCoverage.Build(root, store.Sources(), products, new[] { Conn("etsy", "S1"), Conn("etsy", "S2") }, now);

            // A source row: the real products, unmapped first, then stale, then mapped -- each with its product id, its value and the reason.
            var fixtureCategory = report.Find(MappingCoverageRow.SourceScope, "fixture", TaxonomyKind.Category)!;
            var drill = report.Drill(fixtureCategory);
            CollectionAssert.AreEqual(new[] { "SKU-3", "SKU-4", "SKU-5", "SKU-1", "SKU-2" }, drill.Select(r => r.Sku).ToArray());
            Assert.AreEqual(products.Single(p => p.Sku == "SKU-1").Id, drill.Single(r => r.Sku == "SKU-1").ProductId, "the record names the real product");
            StringAssert.Contains(drill.Single(r => r.Sku == "SKU-4").Words, "kategori girilmemiş"); StringAssert.Contains(drill.Single(r => r.Sku == "SKU-3").Words, "kategori sözlükte yok");
            StringAssert.Contains(drill.Single(r => r.Sku == "SKU-5").Words, "kategori pasif: Mutfak"); Assert.AreEqual("Elektronik", drill.Single(r => r.Sku == "SKU-1").Words);
            var unmappedOnly = report.Drill(fixtureCategory, MappingCoverage.Unmapped); Assert.AreEqual(2, unmappedOnly.Count); Assert.IsTrue(unmappedOnly.All(r => r.Bucket == MappingCoverage.Unmapped));
            var described = MappingCoverage.Describe(fixtureCategory); StringAssert.Contains(described, "kaynak Fixture"); StringAssert.Contains(described, "5 kayıttan 2 eşli"); StringAssert.Contains(described, "kısmi");

            // A shop row: the channel's own reasons -- no mapping, a mapping nobody confirmed for 200 days, the attribute values behind a product.
            var s2 = report.Drill(report.Find(MappingCoverageRow.ShopScope, "etsy/S2", TaxonomyKind.Category)!);
            Assert.AreEqual(MappingCoverage.Stale, s2.Single(r => r.Sku == "SKU-1").Bucket); StringAssert.Contains(s2.Single(r => r.Sku == "SKU-1").Words, "gündür doğrulanmadı");
            StringAssert.Contains(s2.Single(r => r.Sku == "SKU-2").Words, "kanal eşlemesi yok: Bahçe");
            var s1Attributes = report.Drill(report.Find(MappingCoverageRow.ShopScope, "etsy/S1", TaxonomyKind.Attribute)!);
            StringAssert.Contains(s1Attributes.Single(r => r.Sku == "SKU-1").Words, "Renk=Kırmızı → a-red"); StringAssert.Contains(s1Attributes.Single(r => r.Sku == "SKU-2").Words, "eşlenecek özellik değeri yok");
            StringAssert.Contains(s1Attributes.Single(r => r.Sku == "SKU-4").Words, "kategori girilmemiş");
            var s2Attributes = report.Drill(report.Find(MappingCoverageRow.ShopScope, "etsy/S2", TaxonomyKind.Attribute)!);
            Assert.AreEqual(MappingCoverage.Unmapped, s2Attributes.Single(r => r.Sku == "SKU-1").Bucket); StringAssert.Contains(s2Attributes.Single(r => r.Sku == "SKU-1").Words, "kanal eşlemesi yok: Renk=Kırmızı");

            // A channel row drills into every shop of the channel, still unmapped first; a row for nothing has no records.
            var channel = report.Drill(report.Find(MappingCoverageRow.ChannelScope, "etsy", TaxonomyKind.Category)!);
            Assert.AreEqual(14, channel.Count); Assert.IsTrue(channel.All(r => r.Channel == "etsy" && r.Scope == MappingCoverageRow.ShopScope));
            CollectionAssert.AreEquivalent(new[] { "etsy/S1", "etsy/S2" }, channel.Select(r => r.ScopeId).Distinct().ToArray());
            Assert.AreEqual(MappingCoverage.Unmapped, channel[0].Bucket); Assert.AreEqual(MappingCoverage.Mapped, channel[^1].Bucket);
            Assert.AreEqual(0, report.Drill(report.Find(MappingCoverageRow.SourceScope, "empty", TaxonomyKind.Brand)!).Count);
            Assert.IsNull(report.Find(MappingCoverageRow.ShopScope, "etsy/S9", TaxonomyKind.Category));
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
