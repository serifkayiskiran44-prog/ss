using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #933 (STOCK: safety buffer profiles). The units held back from the available stock come from the most specific
// profile in force -- product over source over store -- with the shop policy's safety stock last; profiles of one
// scope never overlap; a zero buffer holds nothing back, a negative one is refused; the projection names the buffer
// and its origin, and says when the stock sits below the buffer; it all reads back after a restart.
[TestClass]
public sealed class SafetyBufferProfileTests
{
    static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
    static DateTime Utc(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void TheMostSpecificProfileInForceWinsScopesNeverOverlapAndBuffersAreNeverNegative()
    {
        var root = Path.Combine(Path.GetTempPath(), "buffer-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new SafetyBufferProfileStore(root);

            // Nothing saved: the shop policy's safety stock, said so.
            var policy = store.Resolve("p1", "src", "Etsy", "S1", 2, Now);
            Assert.AreEqual((2, SafetyBufferResolution.Policy), (policy.Buffer, policy.Origin)); Assert.IsNull(policy.Profile); StringAssert.Contains(policy.Words, "mağaza politikası");

            // Store, then source, then product profiles: each more specific one wins; a zero buffer is a buffer of nothing.
            var storeProfile = store.Save("store", "etsy/S1", 3, Utc(2026, 1, 1), null, "mağaza", Now);
            Assert.AreEqual((3, SafetyBufferResolution.Store), (store.Resolve("p1", "src", "etsy", "S1", 2, Now).Buffer, store.Resolve("p1", "src", "etsy", "S1", 2, Now).Origin));
            store.Save("source", "src", 4, Utc(2026, 1, 1), null, "kaynak", Now);
            Assert.AreEqual((4, SafetyBufferResolution.Source), (store.Resolve("p1", "src", "etsy", "S1", 2, Now).Buffer, store.Resolve("p1", "src", "etsy", "S1", 2, Now).Origin));
            var product = store.Save("Product", "p1", 0, Utc(2026, 1, 1), null, "ürün", Now);
            var resolved = store.Resolve("p1", "src", "etsy", "S1", 2, Now);
            Assert.AreEqual((0, SafetyBufferResolution.Product, product.Id), (resolved.Buffer, resolved.Origin, resolved.Profile!.Id)); StringAssert.Contains(resolved.Words, "güvenlik tamponu 0 · ürün profili");
            Assert.AreEqual(3, store.Resolve("p2", "other", "etsy", "S1", 2, Now).Buffer, "another product on the same store falls to the store profile");
            Assert.AreEqual(2, store.Resolve("p2", "other", "etsy", "S2", 2, Now).Buffer, "another store has no profile");

            // Effective dates: a profile from tomorrow is not in force today but is at a later moment; a closed period in the past is not.
            store.Save("product", "p3", 7, Now.AddDays(1), null, "", Now);
            Assert.AreEqual(4, store.Resolve("p3", "src", "etsy", "S1", 2, Now).Buffer); Assert.AreEqual(7, store.Resolve("p3", "src", "etsy", "S1", 2, Now.AddDays(2)).Buffer);
            store.Save("product", "p4", 9, Utc(2025, 1, 1), Utc(2025, 6, 1), "", Now);
            Assert.AreEqual(SafetyBufferResolution.Source, store.Resolve("p4", "src", "etsy", "S1", 2, Now).Origin);

            // Overlap refused within one scope and id; adjacent periods allowed; negative buffers and bad dates refused.
            var overlap = Assert.ThrowsException<InvalidOperationException>(() => store.Save("store", "etsy/S1", 5, Utc(2026, 6, 1), Utc(2026, 7, 1), "", Now));
            StringAssert.Contains(overlap.Message, "çakışıyor"); StringAssert.Contains(overlap.Message, $"{storeProfile.Id} numaralı profil");
            store.Save("product", "p4", 1, Utc(2025, 6, 1), Utc(2025, 9, 1), "", Now);
            Assert.ThrowsException<ArgumentException>(() => store.Save("product", "p5", -1, Utc(2026, 1, 1), null, "", Now));
            Assert.ThrowsException<ArgumentException>(() => store.Save("product", "p5", 1, Utc(2026, 2, 1), Utc(2026, 1, 1), "", Now));
            Assert.ThrowsException<ArgumentException>(() => store.Save("shop", "p5", 1, Utc(2026, 1, 1), null, "", Now));
            Assert.ThrowsException<ArgumentException>(() => store.Save("product", " ", 1, Utc(2026, 1, 1), null, "", Now));

            // Listing and a restart.
            Assert.AreEqual(6, store.List().Count); Assert.AreEqual(2, store.List("product", "p4").Count); Assert.AreEqual(1, store.List("store").Count);
            SqliteConnection.ClearAllPools();
            Assert.AreEqual(0, new SafetyBufferProfileStore(root).Resolve("p1", "src", "etsy", "S1", 2, Now).Buffer);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void TheProjectionUsesTheBufferNamesItsOriginAndSaysWhenTheStockSitsBelowIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "buffer-proj-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var catalog = new CatalogStore(root); var profiles = new SafetyBufferProfileStore(root);
            var source = new XmlSource { Id = "src", Name = "Src", ItemPath = "/p", Fields = new Dictionary<string, string> { ["Sku"] = "s", ["Name"] = "n" } }; catalog.SaveSource(source);
            catalog.Import(source, new[] { new CatalogProduct { SourceId = "src", SourceKind = "xml", Sku = "SKU-1", Name = "Bir", Price = 10, Currency = "TRY", Cost = 4, Stock = 10, Active = true } });
            var id = catalog.Products().Single().Id;
            catalog.SaveStockPolicy(new StockPolicy { Channel = "local", Shop = "s1", SafetyStock = 2, MaximumStock = null, Enabled = true });

            // The policy's safety stock, then a store, a source and a zero product profile -- the projection names each.
            var policy = catalog.ProjectStock("local", "s1", id, Now); Assert.AreEqual((8, 2, false), (policy.Available, policy.SafetyStock, policy.BelowBuffer)); StringAssert.Contains(policy.BufferWords, "mağaza politikası"); StringAssert.Contains(policy.Words, "güvenlik tamponu 2 · mağaza politikası");
            profiles.Save("store", "local/s1", 3, Utc(2026, 1, 1), null, "", Now);
            var storeBuffer = catalog.ProjectStock("local", "s1", id, Now); Assert.AreEqual((7, 3), (storeBuffer.Available, storeBuffer.SafetyStock)); StringAssert.Contains(storeBuffer.BufferWords, "mağaza profili");
            profiles.Save("source", "src", 4, Utc(2026, 1, 1), null, "", Now);
            Assert.AreEqual(6, catalog.ProjectStock("local", "s1", id, Now).Available); StringAssert.Contains(catalog.ProjectStock("local", "s1", id, Now).BufferWords, "kaynak profili");
            profiles.Save("product", id, 0, Utc(2026, 1, 1), Utc(2026, 9, 20), "kampanya: tampon yok", Now);
            var zero = catalog.ProjectStock("local", "s1", id, Now); Assert.AreEqual((10, 0), (zero.Available, zero.SafetyStock)); StringAssert.Contains(zero.BufferWords, "ürün profili");
            Assert.AreEqual(10, catalog.PreviewStock("local", "s1", id), "the old preview follows the projection");

            // Stock below the buffer: nothing available, said so, not hidden in a zero; the day before, the zero profile still holds (end exclusive); before any profile began, the shop policy's own safety stock held.
            profiles.Save("product", id, 12, Utc(2026, 9, 20), null, "", Now);
            var below = catalog.ProjectStock("local", "s1", id, Utc(2026, 9, 25)); Assert.AreEqual((0, 12, true), (below.Available, below.SafetyStock, below.BelowBuffer)); StringAssert.Contains(below.Words, "stok tamponun altında (10 < 12)");
            Assert.AreEqual(10, catalog.ProjectStock("local", "s1", id, Utc(2026, 9, 19)).Available, "the day before the 12-unit profile the zero one still holds (end exclusive)");
            Assert.AreEqual((8, 2), (catalog.ProjectStock("local", "s1", id, Utc(2025, 12, 31)).Available, catalog.ProjectStock("local", "s1", id, Utc(2025, 12, 31)).SafetyStock), "before any profile began, the policy's safety stock");
            Assert.IsFalse(catalog.ProjectStock("local", "s1", id, Utc(2026, 9, 19)).BelowBuffer);
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
