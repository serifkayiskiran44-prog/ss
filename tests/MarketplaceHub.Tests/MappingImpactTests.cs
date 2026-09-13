using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #919 (TAXONOMY: mapping impact preview). Before an external key is bound to a local category, brand or attribute for
// a channel and shop, the preview counts the products whose readiness on that channel changes and their plans, says
// when the key is being moved, and binds to the channel scope's taxonomy snapshot version; applying refuses when the
// version moved since the preview (the stale guard); a preview or a cancel writes nothing; it all holds after a restart.
[TestClass]
public sealed class MappingImpactTests
{
    static TaxonomyEntry Entry(TaxonomyStore taxonomy, TaxonomyKind kind, string name) => taxonomy.Save(new TaxonomyEntry { Kind = kind, Name = name, Value = kind == TaxonomyKind.Brand ? name : "" });
    static CatalogProduct P(string sku, string category, string brand = "", string attributes = "") => new() { SourceId = "fixture", Sku = sku, Name = "Ürün " + sku, Price = 10, Currency = "TRY", Cost = 4, Stock = 1, Category = category, Brand = brand, AttributesText = attributes };

    [TestMethod]
    public void ThePreviewCountsWhatABindingTouchesByKindAndAsksOnlyWhenItTouchesSomething()
    {
        var root = Path.Combine(Path.GetTempPath(), "impact-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CatalogStore(root); var taxonomy = new TaxonomyStore(root); var aliases = new TaxonomyAliasStore(root); var plans = new ChannelProductsStore(root);
            var electronics = Entry(taxonomy, TaxonomyKind.Category, "Elektronik"); var garden = Entry(taxonomy, TaxonomyKind.Category, "Bahçe"); var acme = Entry(taxonomy, TaxonomyKind.Brand, "Acme"); var colour = Entry(taxonomy, TaxonomyKind.Attribute, "Renk");
            aliases.Save("elektronik ürünleri", electronics.Id, approved: true);
            var rows = new[] { P("SKU-1", "Elektronik", "Acme", "Renk=Kırmızı"), P("SKU-2", "ELEKTRONİK ürünleri", "acme"), P("SKU-3", "Bahçe", "Zeta", "Beden=M"), P("SKU-4", "", "", "Renk=Mavi") };
            store.Import(new XmlSource { Id = "fixture", Name = "Fixture" }, rows);
            var products = store.Products(); string Id(string sku) => products.Single(p => p.Sku == sku).Id;
            plans.Save(new ChannelProductPlan { ChannelId = "etsy", ShopId = "S1", ProductId = Id("SKU-1"), Currency = "USD", PlannedPrice = 10, PlannedStock = 1 });
            plans.Save(new ChannelProductPlan { ChannelId = "etsy", ShopId = "S2", ProductId = Id("SKU-2"), Currency = "USD", PlannedPrice = 10, PlannedStock = 1 });
            var planList = plans.List();

            // Category: the products that resolve to the local category by name or approved alias; the plans on that channel and shop; samples; the snapshot version it binds to.
            var preview = MappingImpact.Preview(root, TaxonomyKind.Category, "e-123", electronics.Id, "Etsy", "S1", products, planList);
            Assert.AreEqual(MappingImpactLevel.Small, preview.Level); Assert.IsTrue(preview.RequiresConfirmation); Assert.AreEqual(2, preview.AffectedProducts); Assert.AreEqual(1, preview.AffectedPlans, "only S1's plan; S2 is another shop");
            CollectionAssert.AreEquivalent(new[] { "SKU-1", "SKU-2" }, preview.SampleSkus.ToList()); Assert.AreEqual("etsy", preview.Marketplace); Assert.IsFalse(preview.Rebinds); StringAssert.Contains(preview.Headline, "2 ürün"); Assert.IsTrue(preview.SnapshotVersion >= 1); Assert.AreEqual(TaxonomySnapshotStore.ChannelScope("etsy", "S1"), preview.Scope);
            Assert.IsTrue(preview.Lines.Any(l => l.Contains("1 kanal planı"))); StringAssert.Contains(preview.Body, "sürüm");
            // Zero impact: nothing to ask.
            var none = MappingImpact.Preview(root, TaxonomyKind.Category, "e-9", garden.Id, "etsy", "S9", products, planList);
            Assert.AreEqual(1, none.AffectedProducts); Assert.AreEqual(0, none.AffectedPlans);
            var unknown = MappingImpact.Preview(root, TaxonomyKind.Category, "e-0", "no-such-id", "etsy", "S1", products, planList);
            Assert.AreEqual(MappingImpactLevel.None, unknown.Level); Assert.IsFalse(unknown.RequiresConfirmation); StringAssert.Contains(unknown.Headline, "hiçbir ürünü etkilemiyor");
            // Brand and attribute: by name or approved brand alias; by the attribute's presence in the product's text.
            Assert.AreEqual(2, MappingImpact.Preview(root, TaxonomyKind.Brand, "b-1", acme.Id, "etsy", "S1", products, planList).AffectedProducts);
            Assert.AreEqual(2, MappingImpact.Preview(root, TaxonomyKind.Attribute, "a-1", colour.Id, "etsy", "S1", products, planList).AffectedProducts);
            // Rebinding: an external key already bound elsewhere is said.
            taxonomy.Map(TaxonomyKind.Category, "e-123", garden.Id, "etsy", "S1");
            Assert.IsTrue(MappingImpact.Preview(root, TaxonomyKind.Category, "e-123", electronics.Id, "etsy", "S1", products, planList).Rebinds);
            // Large impact: fifty products or more.
            var many = Enumerable.Range(0, 60).Select(i => { var p = P($"BULK-{i}", "Elektronik"); p.SourceId = "fixture-2"; return p; }).ToArray();
            store.Import(new XmlSource { Id = "fixture-2", Name = "Fixture 2" }, many); // a second source's first run owns nothing, so the dropship count gate does not trip
            var large = MappingImpact.Preview(root, TaxonomyKind.Category, "e-123", electronics.Id, "etsy", "S1", store.Products(), planList);
            Assert.AreEqual(MappingImpactLevel.Large, large.Level); Assert.AreEqual(62, large.AffectedProducts); StringAssert.Contains(large.Headline, "büyük etki"); Assert.AreEqual(MappingImpact.SampleLimit, large.SampleSkus.Count);
            // A preview writes nothing: no mapping, and the version confirmed, not moved.
            Assert.IsFalse(taxonomy.Mappings(TaxonomyKind.Category).Any(m => m.LocalId == electronics.Id && m.ExternalKey == "e-123"));
            Assert.AreEqual(large.SnapshotVersion, MappingImpact.Preview(root, TaxonomyKind.Category, "e-123", electronics.Id, "etsy", "S1", store.Products(), planList).SnapshotVersion);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void ApplyingHonoursTheStaleGuardWritesThroughTheOwnerAndSurvivesARestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "impact-apply-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CatalogStore(root); var taxonomy = new TaxonomyStore(root); var plans = new ChannelProductsStore(root); var snapshots = new TaxonomySnapshotStore(root);
            var electronics = Entry(taxonomy, TaxonomyKind.Category, "Elektronik"); var garden = Entry(taxonomy, TaxonomyKind.Category, "Bahçe");
            store.Import(new XmlSource { Id = "fixture", Name = "Fixture" }, new[] { P("SKU-1", "Elektronik"), P("SKU-2", "Bahçe") });
            var products = store.Products();

            // Stale: the taxonomy changed after the preview (a mapping written by someone else) -> apply refuses with both versions and writes nothing.
            var preview = MappingImpact.Preview(root, TaxonomyKind.Category, "e-123", electronics.Id, "etsy", "S1", products, plans.List());
            taxonomy.Map(TaxonomyKind.Category, "e-777", garden.Id, "etsy", "S1");
            var refused = Assert.ThrowsException<InvalidOperationException>(() => MappingImpact.Apply(root, preview));
            StringAssert.Contains(refused.Message, "yeniden önizleyin"); StringAssert.Contains(refused.Message, preview.SnapshotVersion.ToString());
            Assert.IsFalse(taxonomy.Mappings(TaxonomyKind.Category).Any(m => m.ExternalKey == "e-123"), "the stale preview mapped nothing");

            // Current: a fresh preview applies through the owner and the scope's version moves on to the new content.
            var fresh = MappingImpact.Preview(root, TaxonomyKind.Category, "e-123", electronics.Id, "etsy", "S1", products, plans.List());
            var after = MappingImpact.Apply(root, fresh);
            Assert.IsTrue(taxonomy.Mappings(TaxonomyKind.Category).Any(m => m.ExternalKey == "e-123" && m.LocalId == electronics.Id && m.Marketplace == "etsy" && m.ShopId == "S1"));
            Assert.AreEqual(fresh.SnapshotVersion + 1, after.Version); Assert.AreEqual(after.Version, snapshots.Current(fresh.Scope)!.Version);
            Assert.ThrowsException<InvalidOperationException>(() => MappingImpact.Apply(root, fresh), "the same preview is stale once applied: the content moved");

            // Cancel is the operator's: a preview never applied leaves no trace. Restart: the guard reads the persisted version, so a preview taken before the restart is judged against the same content.
            var pending = MappingImpact.Preview(root, TaxonomyKind.Category, "e-555", garden.Id, "etsy", "S1", products, plans.List());
            SqliteConnection.ClearAllPools();
            Assert.AreEqual(pending.SnapshotVersion, new TaxonomySnapshotStore(root).Current(pending.Scope)!.Version);
            MappingImpact.Apply(root, pending);
            Assert.IsTrue(new TaxonomyStore(root).Mappings(TaxonomyKind.Category).Any(m => m.ExternalKey == "e-555"));
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
