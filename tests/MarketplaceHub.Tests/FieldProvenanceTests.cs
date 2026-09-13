using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #895 (SOURCE PROVENANCE: field-level source evidence). Every field a feed writes carries its source, the
// source's configuration revision, the import run and the moment; a field the operator writes carries "manual";
// two feeds writing the same product leave each field with its own origin; the origins survive a restart; a
// deleted source is still named by its id while the words say it is gone; nothing holds a value or an address.
[TestClass]
public sealed class FieldProvenanceTests
{
    static readonly DateTime Now = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void StampsAndWordsFollowTheOriginNeverTheValue()
    {
        var product = new CatalogProduct { Sku = "A", Name = "Kupa", Price = 10, Stock = 3 };
        FieldProvenance.StampFeed(product, new[] { "Price", "Stock" }, "src-1", 4, "run-abcdef123456", Now.AddHours(-2));
        var price = FieldProvenance.Of(product, "Price")!;
        Assert.AreEqual(FieldProvenance.FeedKind, price.Kind); Assert.AreEqual(4, price.SourceRevision); Assert.AreEqual("run-abcdef123456", price.RunId);
        Assert.IsNull(FieldProvenance.Of(product, "Name"), "an untouched field has no origin yet");
        var source = new XmlSource { Id = "src-1", Name = "Tedarikçi A", Location = "https://feed.example.com/a.xml?token=x" };
        var words = FieldProvenance.Describe(price, id => id == "src-1" ? source : null, Now);
        Assert.AreEqual("Tedarikçi A · rev. 4 · çalıştırma run-abcd · 2 sa önce", words);
        Assert.IsFalse(words.Contains("token") || words.Contains("example.com"), "no address, no credential");
        Assert.AreEqual("kaynak silinmiş · rev. 4 · çalıştırma run-abcd · 2 sa önce", FieldProvenance.Describe(price, _ => null, Now), "a deleted source is named as gone");
        Assert.AreEqual("kaydedilmedi", FieldProvenance.Describe(null, _ => null, Now));

        // A manual save stamps only the fields that changed.
        var before = new CatalogProduct { Sku = "A", Name = "Kupa", Price = 10, Stock = 3, FieldOrigins = new Dictionary<string, FieldOrigin>(product.FieldOrigins!) };
        var after = new CatalogProduct { Sku = "A", Name = "Kupa (elle)", Price = 10, Stock = 3 };
        var changed = FieldProvenance.StampManual(before, after, Now);
        CollectionAssert.AreEqual(new[] { "Name" }, changed.ToList());
        Assert.AreEqual(FieldProvenance.ManualKind, FieldProvenance.Of(after, "Name")!.Kind);
        Assert.AreEqual(FieldProvenance.FeedKind, FieldProvenance.Of(after, "Price")!.Kind, "the untouched field keeps its feed origin");
        Assert.AreEqual("elle · az önce", FieldProvenance.Describe(FieldProvenance.Of(after, "Name"), _ => null, Now));
    }

    [TestMethod]
    public void TheStoreWritesOriginsWithTheValuesAcrossFeedsManualEditsAndARestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "prov-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CatalogStore(root);
            var a = new XmlSource { Id = "src-a", Name = "Tedarikçi A", ItemPath = "/p", Fields = new Dictionary<string, string> { ["Sku"] = "s", ["Name"] = "n" }, UpdateName = true };
            var b = new XmlSource { Id = "src-b", Name = "Tedarikçi B", ItemPath = "/p", Fields = new Dictionary<string, string> { ["Sku"] = "s", ["Name"] = "n" } };
            store.SaveSource(a); store.SaveSource(b);
            var runs = new XmlRunStore(root);
            var runA = runs.Start("src-a", "h1", TimeSpan.FromMinutes(5), a.ConfigRevision);
            store.Import(a, new[] { new CatalogProduct { SourceId = "src-a", SourceKind = "xml", Sku = "A", Name = "Kupa", Price = 10, Currency = "TRY", Cost = 4, Stock = 3 } }, CancellationToken.None, new XmlImportContext { RunId = runA, SourceRevision = a.ConfigRevision, ObservedAtUtc = Now });
            runs.Complete(runA, new ImportSummary(1, 0, 0));
            var product = store.Products().Single();
            foreach (var field in new[] { "Price", "Stock", "Cost", "Name" })
            {
                var origin = FieldProvenance.Of(product, field)!;
                Assert.AreEqual("src-a", origin.SourceId, field); Assert.AreEqual(a.ConfigRevision, origin.SourceRevision, field); Assert.AreEqual(runA, origin.RunId, field); Assert.AreEqual(Now, origin.ObservedUtc, field);
            }
            Assert.AreEqual(runA, FieldProvenance.Of(product, "Description")!.RunId, "a new product takes every field from the feed");

            // The operator overrides the price and locks it: the price is manual, the stock stays the feed's.
            product.Price = 12; product.LockPrice = true; product.PriceSource = "manual";
            store.SaveProduct(product);
            var edited = store.Products().Single();
            Assert.AreEqual(FieldProvenance.ManualKind, FieldProvenance.Of(edited, "Price")!.Kind); Assert.AreEqual(FieldProvenance.FeedKind, FieldProvenance.Of(edited, "Stock")!.Kind);

            // Another run of the same feed writes the stock again: the stock's origin moves to the new run, the manual price stays manual.
            var runA2 = runs.Start("src-a", "h2", TimeSpan.FromMinutes(5), a.ConfigRevision);
            store.Import(a, new[] { new CatalogProduct { SourceId = "src-a", SourceKind = "xml", Sku = "A", Name = "Kupa", Price = 10, Currency = "TRY", Cost = 4, Stock = 7 } }, CancellationToken.None, new XmlImportContext { RunId = runA2, SourceRevision = a.ConfigRevision, ObservedAtUtc = Now.AddHours(1) });
            runs.Complete(runA2, new ImportSummary(0, 1, 0));
            var again = store.Products().Single();
            Assert.AreEqual(runA2, FieldProvenance.Of(again, "Stock")!.RunId); Assert.AreEqual(FieldProvenance.ManualKind, FieldProvenance.Of(again, "Price")!.Kind, "a locked field is not written, so its origin does not move");
            Assert.AreEqual(runA, FieldProvenance.Of(again, "Description")!.RunId, "a field the source does not rewrite keeps its first origin");

            // Restart: a fresh store reads the same origins.
            SqliteConnection.ClearAllPools();
            var reopened = new CatalogStore(root).Products().Single();
            Assert.AreEqual(runA2, FieldProvenance.Of(reopened, "Stock")!.RunId); Assert.AreEqual("src-a", FieldProvenance.Of(reopened, "Stock")!.SourceId);

            // The view: the stock names the source with its revision and run, the price is the operator's; with the source gone the words say so.
            var view = ProductProvenance.Build(reopened, a, Now.AddHours(2));
            StringAssert.Contains(Row(view, "Stok").Detail, "rev. " + a.ConfigRevision); StringAssert.Contains(Row(view, "Stok").Detail, "çalıştırma " + runA2[..8]);
            Assert.IsTrue(Row(view, "Fiyat").IsOperatorOwned); StringAssert.Contains(Row(view, "Fiyat").Detail, "elle");
            var gone = ProductProvenance.Build(reopened, null, Now.AddHours(2));
            StringAssert.Contains(Row(gone, "Stok").Detail, "kaynak silinmiş");
            Assert.IsFalse(string.Join(" ", view.Rows.Select(r => r.Origin + r.Detail)).Contains("example.com"));
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

    static ProductProvenanceRow Row(ProductProvenanceView view, string field) => view.Rows.Single(r => r.Field == field);
}
