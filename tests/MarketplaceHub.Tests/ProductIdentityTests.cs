using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #902 (PRODUCT ID: the canonical identity contract). One internal id every module joins on; the SKU, the barcode,
// the source record and the channel listing ids are keys with stated roles, and one matching rule -- the import's,
// the preview's, the order lines' -- decides a duplicate SKU, a missing barcode or a conflicting pair the same way
// everywhere. A source remap moves the home and nothing else; a restart changes nothing.
[TestClass]
public sealed class ProductIdentityTests
{
    static readonly DateTime Now = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);
    static CatalogProduct P(string id, string sku, string barcode = "") => new() { Id = id, Sku = sku, Barcode = barcode, SourceId = "a", SourceKind = "xml", Name = "Ürün " + (sku.Length > 0 ? sku : barcode), Price = 10, Cost = 4, Stock = 1 };
    static XmlSource Source(string name) => new() { Name = name, Location = "https://feeds.example.com/" + name.ToLowerInvariant() + ".xml", Enabled = true, IntervalMinutes = 30, ItemPath = "/p", Fields = new Dictionary<string, string> { ["Sku"] = "s", ["Barcode"] = "b", ["Name"] = "n", ["Cost"] = "c", ["Stock"] = "q" } };

    [TestMethod]
    public void TheContractNamesOneCanonicalKeyAndOneMatchingRuleForEveryModule()
    {
        Assert.AreEqual(5, ProductIdentity.Contract.Count);
        Assert.AreEqual(ProductKeyRole.Canonical, ProductIdentity.Contract.Single(c => c.JoinsAcrossModules).Role, "exactly one key joins across modules");
        Assert.IsTrue(ProductIdentity.IsCanonical(ProductIdentity.NewCanonical())); Assert.IsFalse(ProductIdentity.IsCanonical("SKU-1")); Assert.IsFalse(ProductIdentity.IsCanonical(null)); Assert.IsFalse(ProductIdentity.IsCanonical(Guid.NewGuid().ToString()));

        var a = P("a1", "SKU-1", "8690000000017"); var b = P("b2", "SKU-2", "8690000000024"); var bare = P("c3", "SKU-3"); var pool = new[] { a, b, bare };
        // The SKU first; the barcode when the SKU matches nothing; a missing barcode on either side is fine.
        Assert.AreSame(a, ProductIdentity.Resolve(pool, "SKU-1", "8690000000017").Product);
        var byBarcode = ProductIdentity.Resolve(pool, "", "8690000000024"); Assert.AreSame(b, byBarcode.Product); Assert.AreEqual("barkod ile eşleşti", byBarcode.Reason);
        Assert.AreSame(bare, ProductIdentity.Resolve(pool, "SKU-3", "").Product); Assert.AreSame(bare, ProductIdentity.Resolve(pool, " SKU-3 ", null).Product);
        Assert.AreSame(a, ProductIdentity.Resolve(pool, "SKU-1", "").Product, "a row without a barcode still matches by its SKU");
        Assert.AreEqual(ProductIdentity.None, ProductIdentity.Resolve(pool, "SKU-9", "").Outcome); Assert.AreEqual(ProductIdentity.None, ProductIdentity.Resolve(pool, "", "").Outcome);

        // A barcode naming another product than the SKU is a conflict; two products with one SKU are ambiguous.
        var conflict = ProductIdentity.Resolve(pool, "SKU-1", "8690000000024");
        Assert.AreEqual(ProductIdentity.Conflict, conflict.Outcome); Assert.IsNull(conflict.Product); Assert.AreEqual(ProductIdentity.ConflictWords, conflict.Reason); Assert.AreEqual(2, conflict.Candidates.Count);
        var twin = P("d4", "SKU-1"); var duplicates = new[] { a, b, twin };
        var ambiguous = ProductIdentity.Resolve(duplicates, "SKU-1", "");
        Assert.AreEqual(ProductIdentity.Ambiguous, ambiguous.Outcome); Assert.AreEqual(ProductIdentity.AmbiguousWords, ambiguous.Reason); CollectionAssert.AreEquivalent(new[] { "a1", "d4" }, ambiguous.Candidates.Select(c => c.Id).ToArray());

        // The index form (the import's) decides exactly like the pool form.
        Assert.AreEqual(ProductIdentity.Conflict, ProductIdentity.Resolve(new[] { a }, new[] { b }, "SKU-1", "8690000000024").Outcome);
        Assert.AreSame(a, ProductIdentity.Resolve(new[] { a }, new[] { a }, "SKU-1", "8690000000017").Product);
        Assert.AreSame(b, ProductIdentity.Resolve(Array.Empty<CatalogProduct>(), new[] { b }, "", "8690000000024").Product);

        // The order-line rule: the SKU alone, exactly one product.
        Assert.AreSame(b, ProductIdentity.ResolveSku(pool, "SKU-2").Product);
        var none = ProductIdentity.ResolveSku(pool, "SKU-9"); Assert.AreEqual(ProductIdentity.None, none.Outcome); StringAssert.Contains(none.Reason, ProductIdentity.NoSingleWords);
        var many = ProductIdentity.ResolveSku(duplicates, "SKU-1"); Assert.AreEqual(ProductIdentity.Ambiguous, many.Outcome); StringAssert.Contains(many.Reason, "birden fazla ürün");
    }

    [TestMethod]
    public void TheStoreMintsACanonicalIdKeepsItAcrossASourceRemapAndDecidesDuplicatesTheSameWayEverywhere()
    {
        var root = Path.Combine(Path.GetTempPath(), "identity-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CatalogStore(root); var runs = new XmlRunStore(root);
            var a = Source("Tedarikçi A"); var b = Source("Tedarikçi B"); store.SaveSource(a); store.SaveSource(b);
            void Import(XmlSource s, (string Sku, string Barcode)[] rows, DateTime at)
            {
                var run = runs.Start(s.Id, Guid.NewGuid().ToString("N"), TimeSpan.FromMinutes(5), s.ConfigRevision);
                try
                {
                    store.Import(s, rows.Select(r => new CatalogProduct { SourceId = s.Id, SourceKind = "xml", Sku = r.Sku, Barcode = r.Barcode, Name = "Ürün " + (r.Sku.Length > 0 ? r.Sku : r.Barcode), Price = 10, Currency = "TRY", Cost = 4, Stock = 3 }).ToList(), CancellationToken.None, new XmlImportContext { RunId = run, SourceRevision = s.ConfigRevision, ObservedAtUtc = at });
                    runs.Complete(run, new ImportSummary(0, 0, 0));
                }
                catch { runs.Fail(run, "test"); throw; }
            }

            // Minted once: canonical ids, distinct, never derived from the SKU.
            Import(a, [("SKU-1", "8690000000017"), ("SKU-2", "")], Now);
            var one = store.Products().Single(p => p.Sku == "SKU-1"); var two = store.Products().Single(p => p.Sku == "SKU-2");
            Assert.IsTrue(ProductIdentity.IsCanonical(one.Id) && ProductIdentity.IsCanonical(two.Id)); Assert.AreNotEqual(one.Id, two.Id);

            // Duplicate SKU inside one feed is refused before anything is written (the feed keeps the source's count, so only the identity rule speaks).
            var duplicate = Assert.ThrowsException<InvalidOperationException>(() => Import(a, [("SKU-3", ""), ("SKU-3", "")], Now.AddMinutes(1)));
            StringAssert.Contains(duplicate.Message, "Yinelenen SKU"); Assert.AreEqual(2, store.Products().Count);

            // Missing barcode: a barcode-only row of the second feed meets SKU-1 by its barcode and a SKU-only row meets SKU-2 -- no twins.
            Import(b, [("", "8690000000017"), ("SKU-2", "")], Now.AddMinutes(5));
            Assert.AreEqual(2, store.Products().Count); Assert.AreEqual(a.Id, store.FindProduct(one.Id)!.SourceId, "a foreign feed never moves the home");
            Assert.IsTrue(store.Sightings(one.Id).ContainsKey(b.Id), "the second source's sighting is recorded under the canonical id");

            // A channel listing hangs off the canonical id; a source remap moves the home and nothing else.
            var channels = new ChannelProductsStore(root);
            channels.Save(new ChannelProductPlan { ChannelId = "trendyol", ShopId = "shop-1", ProductId = one.Id, ListingId = "L-1" });
            var remapped = store.RemapProductSource(one.Id, b.Id, "tedarikçi değişti");
            Assert.AreEqual(one.Id, remapped.Id); Assert.AreEqual(b.Id, remapped.SourceId); Assert.AreEqual("SKU-1", remapped.Sku); Assert.AreEqual("8690000000017", remapped.Barcode);
            Assert.AreEqual("L-1", channels.Find("trendyol", "shop-1", one.Id)!.ListingId, "the listing still finds the product by its id");
            Assert.IsTrue(store.Sightings(one.Id).ContainsKey(a.Id) && store.Sightings(one.Id).ContainsKey(b.Id), "sightings keep their key");
            Assert.IsNotNull(FieldProvenance.Of(store.FindProduct(one.Id)!, "Stock"), "field origins keep their key");
            var audit = new AuditStore(root).List(20).Single(e => e.Action == ProductIdentity.RemapAction);
            Assert.AreEqual(one.Id, audit.ProductId); StringAssert.Contains(audit.Detail, "Tedarikçi A"); StringAssert.Contains(audit.Detail, "Tedarikçi B"); StringAssert.Contains(audit.Detail, "tedarikçi değişti");
            Assert.AreEqual(b.Id, store.RemapProductSource(one.Id, b.Id).SourceId, "remapping to the same home is a no-op");
            Assert.AreEqual(1, new AuditStore(root).List(20).Count(e => e.Action == ProductIdentity.RemapAction), "a no-op writes no audit row");
            Assert.ThrowsException<InvalidOperationException>(() => store.RemapProductSource(one.Id, "missing-source"));

            // The keys themselves cannot be edited by hand: the store refuses a changed SKU on save.
            var edited = store.FindProduct(two.Id)!; edited.Sku = "SKU-2-renamed";
            var refused = Assert.ThrowsException<InvalidOperationException>(() => store.SaveProduct(edited)); StringAssert.Contains(refused.Message, "elle değiştirilemez");

            // Restart: the same ids, the same homes, the same listing.
            SqliteConnection.ClearAllPools();
            var reopened = new CatalogStore(root);
            Assert.AreEqual(b.Id, reopened.FindProduct(one.Id)!.SourceId); Assert.AreEqual(a.Id, reopened.FindProduct(two.Id)!.SourceId);
            Assert.AreEqual("L-1", new ChannelProductsStore(root).Find("trendyol", "shop-1", one.Id)!.ListingId);
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
