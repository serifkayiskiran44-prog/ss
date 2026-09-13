using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #909 (PRODUCT TAX: the tax class as typed metadata). A product's tax class is one of the catalogue's codes; it
// resolves to MISSING, KNOWN or UNKNOWN and never blurs the three; it reaches a price only as the VAT percentage the
// existing money gate already takes (a known class supplies it, a missing one leaves the store rule's, an unknown
// one supplies nothing and the preview refuses); every change is kept with its moment, origin and the rate of its
// day, and all of it survives a restart.
[TestClass]
public sealed class ProductTaxClassTests
{
    static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
    static XmlSource Source(string name) => new() { Name = name, Location = "https://feeds.example.com/" + name.ToLowerInvariant() + ".xml", Enabled = true, IntervalMinutes = 30, ItemPath = "/p", Fields = new Dictionary<string, string> { ["Sku"] = "s", ["Name"] = "n", ["Cost"] = "c", ["Stock"] = "q", ["TaxClass"] = "t" } };

    [TestMethod]
    public void TheClassResolvesToMissingKnownOrUnknownAndReachesPricingOnlyAsTheVatRate()
    {
        // The catalogue: five VAT bands with canonical codes; the editor offers "not specified" first.
        CollectionAssert.AreEqual(new[] { "standard", "reduced", "super-reduced", "zero", "exempt" }, ProductTaxClass.Catalog.Select(e => e.Code).ToList());
        Assert.AreEqual(10m, ProductTaxClass.Find(" Reduced ")!.RatePercent); Assert.IsTrue(ProductTaxClass.Find("exempt")!.Exempt); Assert.IsNull(ProductTaxClass.Find("weird")); Assert.IsNull(ProductTaxClass.Find(""));
        Assert.AreEqual("", ProductTaxClass.Choices[0].Code); Assert.AreEqual(ProductTaxClass.Catalog.Count + 1, ProductTaxClass.Choices.Count);

        // Missing, known, unknown -- three states, never confused.
        var missing = new CatalogProduct { Sku = "M", Name = "Ürün", Currency = "TRY" };
        var known = new CatalogProduct { Sku = "K", Name = "Ürün", Currency = "TRY", TaxClass = "Reduced" };
        var unknown = new CatalogProduct { Sku = "U", Name = "Ürün", Currency = "TRY", TaxClass = "weird" };
        var none = ProductTaxClass.Resolve(missing); Assert.AreEqual(TaxClassResolution.Missing, none.State); Assert.IsNull(none.RatePercent); StringAssert.Contains(none.Words, "belirtilmedi");
        var resolved = ProductTaxClass.Resolve(known); Assert.AreEqual(TaxClassResolution.Known, resolved.State); Assert.AreEqual("reduced", resolved.Code); Assert.AreEqual(10m, resolved.RatePercent); StringAssert.Contains(resolved.Words, "İndirimli");
        var odd = ProductTaxClass.Resolve(unknown); Assert.AreEqual(TaxClassResolution.Unknown, odd.State); Assert.IsNull(odd.RatePercent); StringAssert.Contains(odd.Words, "tanınmıyor");

        // The only link to pricing: the VAT percentage the existing money gate takes.
        Assert.AreEqual(10m, ProductTaxClass.RateFor(known, 20m), "a known class beats the store rule's rate");
        Assert.AreEqual(20m, ProductTaxClass.RateFor(missing, 20m)); Assert.IsNull(ProductTaxClass.RateFor(missing, null), "a missing class leaves the rule's rate, present or not");
        Assert.IsNull(ProductTaxClass.RateFor(unknown, 20m), "an unknown class supplies nothing, so the gate blocks instead of guessing");

        // Applied on a write: canonical code, the VAT rate from the class, one history entry per change with the moment, the origin and the rate of its day.
        Assert.IsTrue(ProductTaxClass.Apply(known, FieldProvenance.ManualKind, Now)); Assert.AreEqual("reduced", known.TaxClass); Assert.AreEqual(10m, known.VatRate);
        Assert.AreEqual(1, known.TaxClassHistory.Count); Assert.AreEqual(Now, known.TaxClassHistory[0].ChangedUtc); Assert.AreEqual(FieldProvenance.ManualKind, known.TaxClassHistory[0].Origin); Assert.AreEqual(10m, known.TaxClassHistory[0].RatePercent);
        Assert.IsFalse(ProductTaxClass.Apply(known, FieldProvenance.ManualKind, Now.AddMinutes(1)), "no change, no entry"); Assert.AreEqual(1, known.TaxClassHistory.Count);
        known.TaxClass = "standard"; Assert.IsTrue(ProductTaxClass.Apply(known, FieldProvenance.FeedKind, Now.AddMinutes(2))); Assert.AreEqual(20m, known.VatRate); Assert.AreEqual(2, known.TaxClassHistory.Count); Assert.AreEqual(10m, known.TaxClassHistory[0].RatePercent, "the old entry keeps the rate of its day");
        known.TaxClass = "weird"; ProductTaxClass.Apply(known, FieldProvenance.FeedKind, Now.AddMinutes(3)); Assert.AreEqual(20m, known.VatRate, "an unknown class changes no rate"); Assert.IsNull(known.TaxClassHistory[^1].RatePercent); Assert.AreEqual(3, known.TaxClassHistory.Count);
        Assert.IsFalse(ProductTaxClass.Apply(missing, FieldProvenance.ManualKind, Now)); Assert.AreEqual(0, missing.TaxClassHistory.Count, "never specified is not a change");

        // Validation and the drawer: unknown is flagged, not specified is the ordinary state, a rate that drifted from its class is flagged.
        Assert.IsTrue(ProductValidation.Evaluate(unknown).Findings.Any(f => f.Field == "Vergi sınıfı" && f.Severity == ProductValidation.Warning));
        Assert.IsFalse(ProductValidation.Evaluate(missing).Findings.Any(f => f.Field == "Vergi sınıfı"));
        var drift = new CatalogProduct { Sku = "D", Name = "Ürün", Currency = "TRY", TaxClass = "reduced", VatRate = 20 };
        StringAssert.Contains(ProductValidation.Evaluate(drift).Findings.Single(f => f.Field == "Vergi sınıfı").Message, "uyuşmuyor");
        var row = ProductQuickInspect.Build(known, Array.Empty<SyncJob>(), Now.AddMinutes(10)).Rows.Single(r => r.Label == "Vergi sınıfı");
        Assert.AreEqual("Fiyat", row.Section); StringAssert.Contains(row.Value, "tanınmıyor"); StringAssert.Contains(row.Value, "kaynaktan"); StringAssert.Contains(row.Value, "3 kayıt");
    }

    [TestMethod]
    public void TheStoreKeepsTheClassItsHistoryAndItsOriginAcrossFeedsSavesAndARestartAndTheGateUsesIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "tax-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CatalogStore(root); var runs = new XmlRunStore(root);
            var a = Source("Tedarikçi A"); store.SaveSource(a);
            void Import((string Sku, string TaxClass)[] rows, DateTime at)
            {
                var run = runs.Start(a.Id, Guid.NewGuid().ToString("N"), TimeSpan.FromMinutes(5), a.ConfigRevision);
                try
                {
                    store.Import(a, rows.Select(r => new CatalogProduct { SourceId = a.Id, SourceKind = "xml", Sku = r.Sku, Name = "Ürün " + r.Sku, Price = 10, Currency = "TRY", Cost = 40, Stock = 3, TaxClass = r.TaxClass }).ToList(), CancellationToken.None, new XmlImportContext { RunId = run, SourceRevision = a.ConfigRevision, ObservedAtUtc = at });
                    runs.Complete(run, new ImportSummary(0, 0, 0));
                }
                catch { runs.Fail(run, "test"); throw; }
            }

            // The feed's class: canonical on the record, the VAT rate from it, one history entry from the feed, the feed as the field's origin; no class is no class.
            Import([("SKU-1", "reduced"), ("SKU-2", ""), ("SKU-3", "")], Now);
            var one = store.Products().Single(p => p.Sku == "SKU-1"); var two = store.Products().Single(p => p.Sku == "SKU-2"); var three = store.Products().Single(p => p.Sku == "SKU-3");
            Assert.AreEqual("reduced", one.TaxClass); Assert.AreEqual(10m, one.VatRate); Assert.AreEqual(1, one.TaxClassHistory.Count); Assert.AreEqual(FieldProvenance.FeedKind, one.TaxClassHistory[0].Origin); Assert.AreEqual(Now, one.TaxClassHistory[0].ChangedUtc);
            Assert.AreEqual(FieldProvenance.FeedKind, FieldProvenance.Of(one, "TaxClass")!.Kind); Assert.AreEqual(a.Id, FieldProvenance.Of(one, "TaxClass")!.SourceId);
            Assert.AreEqual("", two.TaxClass); Assert.AreEqual(TaxClassResolution.Missing, ProductTaxClass.Resolve(two).State); Assert.AreEqual(0, two.TaxClassHistory.Count);

            // The operator changes the class: the rate follows, the history keeps both entries, the origin is theirs.
            var edit = store.FindProduct(one.Id)!; edit.TaxClass = "standard"; store.SaveProduct(edit);
            var changed = store.FindProduct(one.Id)!; Assert.AreEqual(20m, changed.VatRate); Assert.AreEqual(2, changed.TaxClassHistory.Count); Assert.AreEqual(FieldProvenance.ManualKind, changed.TaxClassHistory[1].Origin); Assert.AreEqual(FieldProvenance.ManualKind, FieldProvenance.Of(changed, "TaxClass")!.Kind);

            // Restart: the class, its history and the rate of its day are read back.
            SqliteConnection.ClearAllPools();
            var reopened = new CatalogStore(root); var back = reopened.FindProduct(one.Id)!;
            Assert.AreEqual("standard", back.TaxClass); Assert.AreEqual(2, back.TaxClassHistory.Count); Assert.AreEqual(10m, back.TaxClassHistory[0].RatePercent); Assert.AreEqual(20m, back.TaxClassHistory[1].RatePercent);

            // A feed sends a code the catalogue does not know: kept as given, no rate guessed, flagged.
            Import([("SKU-1", "standard"), ("SKU-2", "weird"), ("SKU-3", "")], Now.AddMinutes(10));
            var odd = reopened.FindProduct(two.Id)!; Assert.AreEqual("weird", odd.TaxClass); Assert.AreEqual(20m, odd.VatRate); Assert.AreEqual(TaxClassResolution.Unknown, ProductTaxClass.Resolve(odd).State); Assert.IsNull(odd.TaxClassHistory.Single().RatePercent);
            Assert.IsTrue(ProductValidation.Evaluate(odd).Findings.Any(f => f.Field == "Vergi sınıfı" && f.Severity == ProductValidation.Warning));

            // The gate: a known class supplies the VAT rate the rule lacks; a missing class leaves the rule's (absent: blocked as before); an unknown class refuses with its own words.
            reopened.SavePricePolicy(new PricePolicy { Channel = "etsy", Shop = "s1", Formula = "x*2", Currency = "TRY", TryPerUnit = 1, Enabled = true, CommissionPercent = 10m, EstimatedShippingTry = 5m, TransactionCostTry = 1m, VatRatePercent = null, VatIncludedInSale = true });
            Assert.AreEqual(80m, reopened.PreviewPrice("etsy", "s1", one.Id).Price);
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => reopened.PreviewPrice("etsy", "s1", two.Id)).Message, "tanınmıyor");
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => reopened.PreviewPrice("etsy", "s1", three.Id)).Message, "BlockedMissingInput");
            var policy = reopened.GetPricePolicy("etsy", "s1")!; policy.VatRatePercent = 20m; reopened.SavePricePolicy(policy);
            Assert.AreEqual(80m, reopened.PreviewPrice("etsy", "s1", three.Id).Price, "with the rule's rate present the missing class uses it");
            Assert.ThrowsException<InvalidOperationException>(() => reopened.PreviewPrice("etsy", "s1", two.Id), "the unknown class still refuses");
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
