using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #916 (TAXONOMY: enum attribute value mapping). A supplier's spelling of an allowed attribute value is an alias keyed by
// the display fold, naming exactly one allowed value of that attribute; only an approved alias is applied, by the
// coverage of #915, on an exact key; a value that resolves to nothing lands in the unmapped queue with its evidence;
// approving a queued value writes the alias; an alias whose canonical value was removed from the allowed values is
// stale (an orphan) and applies no more; it all survives a restart. No XML variant mapping. (A casing variant such as
// "Kirmizi" is not an alias: the fold already meets the dotted and the dotless i, so it is the allowed value itself.)
[TestClass]
public sealed class AttributeValueMappingTests
{
    static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
    static TaxonomyEntry Entry(TaxonomyStore taxonomy, TaxonomyKind kind, string name, string value = "") => taxonomy.Save(new TaxonomyEntry { Kind = kind, Name = name, Value = value });

    [TestMethod]
    public void AliasesNameOneAllowedValueApplyOnlyWhenApprovedQueueTheUnknownAndGoStaleWithARemovedValue()
    {
        var root = Path.Combine(Path.GetTempPath(), "enum-" + Guid.NewGuid().ToString("N"));
        try
        {
            var taxonomy = new TaxonomyStore(root); var mapping = new AttributeValueMappingStore(root);
            var red = Entry(taxonomy, TaxonomyKind.Attribute, "Renk", "Kırmızı"); Entry(taxonomy, TaxonomyKind.Attribute, "Renk", "Mavi"); Entry(taxonomy, TaxonomyKind.Attribute, "Beden", "M");
            CollectionAssert.AreEquivalent(new[] { "Kırmızı", "Mavi" }, mapping.AllowedValues("renk").ToList());
            Assert.ThrowsException<InvalidOperationException>(() => mapping.SaveAlias("Renk", "Kirmizi", "Kırmızı", true), "a casing variant is the allowed value itself under the fold, not an alias");

            // Alias: a supplier's word bound to one allowed value; resolved in every casing; owned by its attribute; the same alias again updated in place; conflicts and pointless aliases refused.
            var saved = mapping.SaveAlias("Renk", "red", "kırmızı", approved: true, source: "manual");
            Assert.AreEqual("Kırmızı", saved.CanonicalValue); Assert.AreEqual(AttributeValueAliasView.ApprovedStatus, saved.Status); Assert.AreEqual("red", saved.Alias);
            Assert.AreEqual("Kırmızı", mapping.Resolve("renk", "RED")); Assert.AreEqual("Kırmızı", mapping.Resolve("Renk", " red "));
            Assert.IsNull(mapping.Resolve("Beden", "red"), "an alias belongs to its attribute");
            mapping.SaveAlias("Renk", "kızıl", "Kırmızı", approved: false); Assert.IsNull(mapping.Resolve("Renk", "Kızıl"), "a pending alias is not applied"); Assert.AreEqual(AttributeValueAliasView.Pending, mapping.ListAliases("Renk").Single(a => a.Alias == "kızıl").Status);
            mapping.SaveAlias("Renk", "KIZIL", "Kırmızı", approved: true); Assert.AreEqual(2, mapping.ListAliases("Renk").Count, "the same alias again is the same row"); Assert.AreEqual("Kırmızı", mapping.Resolve("Renk", "kızıl"));
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => mapping.SaveAlias("Renk", "kızıl", "Mavi", true)).Message, "Kırmızı");
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => mapping.SaveAlias("Renk", "blue", "Yeşil", true)).Message, "izinli değeri değil");
            Assert.ThrowsException<InvalidOperationException>(() => mapping.SaveAlias("Renk", "MAVİ", "Kırmızı", true), "an allowed value cannot be an alias of another");
            Assert.ThrowsException<InvalidOperationException>(() => mapping.SaveAlias("", "x", "Kırmızı", true));
            Assert.AreEqual(2, mapping.ListAliases().Count, "nothing was written by a refused save");
            var map = mapping.ApprovedMap(); Assert.AreEqual("Kırmızı", map[AttributeValueMappingStore.MapKey(AttributeValueMappingStore.Key("Renk"), AttributeValueMappingStore.Key("red"))]);

            // Unknown value: queued with evidence, a repeat growing the count; approving from the queue writes the alias through the alias rules; rejecting keeps it out until seen again.
            Assert.AreEqual(1, mapping.Record([new("Renk", "Bordo", "SKU-1"), new("renk", "BORDO", "SKU-2"), new("Renk", "", "SKU-3")], Now));
            var queued = mapping.ListQueue(AttributeValueQueueItem.Pending).Single(); Assert.AreEqual(2, queued.Count); Assert.AreEqual("SKU-1", queued.SampleSku); Assert.AreEqual("Bordo", queued.Value); Assert.AreEqual(Now, queued.FirstSeenUtc);
            mapping.Record([new("Renk", "bordo", "SKU-9")], Now.AddHours(1)); Assert.AreEqual(3, mapping.FindQueued("Renk", "Bordo")!.Count); Assert.AreEqual(Now.AddHours(1), mapping.FindQueued("Renk", "Bordo")!.LastSeenUtc);
            Assert.ThrowsException<InvalidOperationException>(() => mapping.ApproveQueued("Renk", "Bordo", "Yeşil"), "the alias rules apply"); Assert.AreEqual(AttributeValueQueueItem.Pending, mapping.FindQueued("Renk", "Bordo")!.Status);
            var approved = mapping.ApproveQueued("Renk", "Bordo", "Kırmızı"); Assert.AreEqual(AttributeValueQueueItem.Approved, approved.Status); Assert.AreEqual("Kırmızı", mapping.Resolve("Renk", "BORDO")); Assert.AreEqual("review", mapping.ListAliases("Renk").Single(a => a.AliasKey == AttributeValueMappingStore.Key("bordo")).Source);
            mapping.Record([new("Renk", "Lacivert", "SKU-4")], Now.AddHours(2)); Assert.AreEqual(AttributeValueQueueItem.Rejected, mapping.RejectQueued("Renk", "Lacivert").Status); Assert.AreEqual(0, mapping.ListQueue(AttributeValueQueueItem.Pending).Count);
            mapping.Record([new("Renk", "lacivert", "SKU-5")], Now.AddHours(3)); Assert.AreEqual(AttributeValueQueueItem.Pending, mapping.FindQueued("Renk", "Lacivert")!.Status);

            // Removed enum: the canonical value deactivated -> its aliases are stale (orphans) and apply no more; restored -> they apply again.
            red.Active = false; taxonomy.Save(red);
            Assert.IsNull(mapping.Resolve("Renk", "red")); Assert.AreEqual(3, mapping.Stale().Count); Assert.IsTrue(mapping.Stale().All(a => a.Status == AttributeValueAliasView.Orphan)); Assert.AreEqual(0, mapping.ApprovedMap().Count);
            red.Active = true; taxonomy.Save(red); Assert.AreEqual("Kırmızı", mapping.Resolve("Renk", "red")); Assert.AreEqual(0, mapping.Stale().Count);

            // Restart and removal.
            SqliteConnection.ClearAllPools();
            var reopened = new AttributeValueMappingStore(root);
            Assert.AreEqual(3, reopened.ListAliases().Count); Assert.AreEqual(2, reopened.ListQueue().Count); Assert.AreEqual("Kırmızı", reopened.Resolve("Renk", "kızıl"));
            Assert.IsTrue(reopened.RemoveAlias("renk", "RED")); Assert.IsFalse(reopened.RemoveAlias("renk", "RED")); Assert.IsNull(reopened.Resolve("Renk", "red"));
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void TheCoverageAppliesApprovedAliasesAndTheQualityScanQueuesTheRest()
    {
        var root = Path.Combine(Path.GetTempPath(), "enum-scan-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CatalogStore(root); var taxonomy = new TaxonomyStore(root); var rules = new CategoryAttributeRuleStore(root); var mapping = new AttributeValueMappingStore(root);
            var clothing = Entry(taxonomy, TaxonomyKind.Category, "Giyim");
            foreach (var colour in new[] { "Kırmızı", "Mavi" }) Entry(taxonomy, TaxonomyKind.Attribute, "Renk", colour);
            rules.Save(clothing.Id, "Renk", required: true);
            mapping.SaveAlias("Renk", "red", "Kırmızı", approved: true);
            mapping.SaveAlias("Renk", "blue", "Mavi", approved: false);

            // The coverage: an approved alias makes the value valid; a pending alias does not; an unknown value is invalid.
            var snapshot = rules.Snapshot();
            Assert.AreEqual(AttributeCoverage.Complete, snapshot.Evaluate("Giyim", "Renk=RED").Status);
            Assert.AreEqual(AttributeCoverage.Incomplete, snapshot.Evaluate("Giyim", "Renk=blue").Status);
            Assert.AreEqual(AttributeCoverage.Incomplete, snapshot.Evaluate("Giyim", "Renk=Bordo").Status);

            // The quality scan: the invalid values of the catalogue land in the unmapped queue with their evidence; the approved alias keeps its product out of it.
            static CatalogProduct P(string sku, string attributes) => new() { SourceId = "fixture", Sku = sku, Name = "Ürün " + sku, Price = 10, Currency = "TRY", Cost = 4, Stock = 1, Category = "Giyim", AttributesText = attributes };
            store.Import(new XmlSource { Id = "fixture", Name = "Fixture" }, new[] { P("SKU-1", "Renk=red"), P("SKU-2", "Renk=Bordo"), P("SKU-3", "Renk=bordo"), P("SKU-4", "Renk=blue") });
            var scan = new DataQualityService(root).Scan().Where(i => i.Type == "InvalidAttributeValue").ToList();
            Assert.AreEqual(3, scan.Count); Assert.IsFalse(scan.Any(i => i.Sku == "SKU-1"));
            var bordo = mapping.FindQueued("Renk", "Bordo")!; Assert.AreEqual(2, bordo.Count); Assert.AreEqual("SKU-2", bordo.SampleSku); Assert.AreEqual(1, mapping.FindQueued("Renk", "blue")!.Count);

            // Approved from the queue: the next scan is clean for that value and the pending alias's product still waits.
            mapping.ApproveQueued("Renk", "Bordo", "Kırmızı");
            var again = new DataQualityService(root).Scan().Where(i => i.Type == "InvalidAttributeValue").ToList();
            Assert.AreEqual(1, again.Count); Assert.AreEqual("SKU-4", again[0].Sku);
            Assert.AreEqual(AttributeValueQueueItem.Approved, mapping.FindQueued("Renk", "Bordo")!.Status);
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
