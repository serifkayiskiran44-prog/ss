using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #908 (PRODUCT DIMENSIONS: consistency and the desi input). A box's three canonical sides stand or fall together --
// one missing, zero or negative is refused, never guessed; the desi (L x W x H / 3000) is derived from the box when
// the operator entered none and follows the box; a desi the operator typed is theirs, kept through box changes,
// stamped manual and compared with the box. No carrier, no rate: only the input a rate table needs, with the reason
// when there is none.
[TestClass]
public sealed class ProductDimensionsTests
{
    static readonly DateTime Now = new(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
    static XmlSource Source(string name, string culture) => new() { Name = name, Location = "https://feeds.example.com/" + name.ToLowerInvariant() + ".xml", Enabled = true, IntervalMinutes = 30, ItemPath = "/p", NumberCultureName = culture, Fields = new Dictionary<string, string> { ["Sku"] = "s", ["Name"] = "n", ["Cost"] = "c", ["Stock"] = "q", ["Weight"] = "w", ["Dimensions"] = "d" } };
    static CatalogProduct Boxed(string sku, string dimensions, string culture) { var p = new CatalogProduct { Sku = sku, Name = "Kutu", Currency = "TRY", DimensionsText = dimensions }; ProductUnits.Apply(p, culture); return p; }

    [TestMethod]
    public void ABoxStandsOrFallsTogetherAndTheDesiFollowsIt()
    {
        // Missing one dimension: the text stays as given, nothing canonical, not blocking; a stored record with a side missing is inconsistent (blocking) and names the side.
        var twoSides = ProductUnits.ParseDimensions("20 x 30 cm", "tr-TR"); Assert.IsFalse(twoSides.IsKnown); Assert.IsFalse(twoSides.Blocking); StringAssert.Contains(twoSides.Diagnostic, "üç boyut");
        var partial = new CatalogProduct { Sku = "P", Name = "Kutu", Currency = "TRY", LengthCm = 20, WidthCm = 30 };
        var missing = ProductDimensions.Findings(partial).Single(); Assert.IsTrue(missing.Blocking); StringAssert.Contains(missing.Message, "yükseklik");
        Assert.IsNull(ProductDimensions.DesiOf(partial)); Assert.AreEqual(DesiInput.Inconsistent, ProductDimensions.Resolve(partial).Status);
        Assert.IsTrue(ProductValidation.Evaluate(partial).Findings.Any(f => f.Field == "Boyut" && f.Severity == ProductValidation.Blocking), "the validation owner carries the rule");

        // Zero and negative are refused at the text and on the record; the desi too.
        Assert.IsTrue(ProductUnits.ParseDimensions("0 x 30 x 40 cm", "en-US").Blocking);
        Assert.IsTrue(ProductUnits.ParseDimensions("20 x -30 x 40 cm", "en-US").Blocking);
        var zeroSide = new CatalogProduct { Sku = "Z", Name = "Kutu", Currency = "TRY", LengthCm = 20, WidthCm = 30, HeightCm = 0 };
        StringAssert.Contains(ProductDimensions.Findings(zeroSide).Single(f => f.Blocking).Message, "yükseklik"); Assert.IsNull(ProductDimensions.DesiOf(zeroSide));
        Assert.IsTrue(ProductDimensions.Findings(new CatalogProduct { Sku = "D", Name = "Kutu", Currency = "TRY", Desi = 0 }).Single().Blocking);
        Assert.IsTrue(ProductValidation.Evaluate(new CatalogProduct { Sku = "D", Name = "Kutu", Currency = "TRY", Desi = -1 }).HasBlocking);

        // Unit conversion: the desi is computed on canonical centimetres whatever unit was written.
        Assert.AreEqual(8m, ProductDimensions.DesiOf(Boxed("B", "200 x 300 x 400 mm", "en-US")));
        Assert.AreEqual(8m, ProductDimensions.DesiOf(Boxed("M", "0,2 x 0,3 x 0,4 m", "tr-TR")));

        // Large values: exact decimal arithmetic up to the parse limit, refused beyond it.
        Assert.AreEqual(333333.33m, ProductDimensions.DesiOf(Boxed("L", "1000 x 1000 x 1000 cm", "en-US")));
        var beyond = new CatalogProduct { Sku = "X", Name = "Kutu", Currency = "TRY", DimensionsText = "100000000 x 1 x 1 cm" };
        Assert.IsTrue(ProductUnits.Apply(beyond, "en-US").Single().Blocking); Assert.IsNull(ProductDimensions.DesiOf(beyond));

        // The desi input: derived from the box when the operator entered none, and it follows the box -- set with it, cleared with it.
        var product = Boxed("S", "20 x 30 x 40 cm", "en-US");
        Assert.IsTrue(ProductDimensions.Refresh(product, Now)); Assert.AreEqual(8m, product.Desi); Assert.IsTrue(ProductDimensions.IsDerived(product));
        Assert.AreEqual(FieldProvenance.DerivedKind, FieldProvenance.Of(product, "Desi")!.Kind);
        StringAssert.Contains(FieldProvenance.Describe(FieldProvenance.Of(product, "Desi"), _ => null, Now), "boyuttan hesaplandı");
        var ready = ProductDimensions.Resolve(product); Assert.AreEqual(DesiInput.Ready, ready.Status); Assert.AreEqual(8m, ready.Desi); Assert.AreEqual(FieldProvenance.DerivedKind, ready.Origin); StringAssert.Contains(ready.Words, "boyuttan");
        product.DimensionsText = "10 x 10 x 30 cm"; ProductUnits.Apply(product, "en-US"); Assert.IsTrue(ProductDimensions.Refresh(product, Now.AddMinutes(1))); Assert.AreEqual(1m, product.Desi);
        product.DimensionsText = ""; ProductUnits.Apply(product, "en-US"); ProductDimensions.Refresh(product, Now.AddMinutes(2)); Assert.IsNull(product.Desi); Assert.IsNull(FieldProvenance.Of(product, "Desi"));
        Assert.AreEqual(DesiInput.NeedsDimensions, ProductDimensions.Resolve(product).Status);

        // The operator's own desi: theirs (manual), kept, compared with the box -- a warning when they disagree, none when the gap is rounding.
        var before = Boxed("O", "20 x 30 x 40 cm", "en-US"); ProductDimensions.Refresh(before, Now);
        var after = Boxed("O", "20 x 30 x 40 cm", "en-US"); after.Desi = 2.5m; after.FieldOrigins = new Dictionary<string, FieldOrigin>(before.FieldOrigins!);
        FieldProvenance.StampManual(before, after, Now.AddMinutes(3)); ProductDimensions.Apply(before, after, Now.AddMinutes(3));
        Assert.AreEqual(2.5m, after.Desi); Assert.AreEqual(FieldProvenance.ManualKind, FieldProvenance.Of(after, "Desi")!.Kind);
        var mismatch = ProductDimensions.Findings(after).Single(); Assert.IsFalse(mismatch.Blocking); StringAssert.Contains(mismatch.Message, "uymuyor");
        var resolved = ProductDimensions.Resolve(after); Assert.AreEqual(2.5m, resolved.Desi); Assert.AreEqual(FieldProvenance.ManualKind, resolved.Origin); StringAssert.Contains(resolved.Words, "elle");
        Assert.IsTrue(ProductValidation.Evaluate(after).Findings.Any(f => f.Field == "Desi" && f.Severity == ProductValidation.Warning));
        Assert.AreEqual(0, ProductDimensions.Findings(new CatalogProduct { Sku = "C", Name = "Kutu", Currency = "TRY", LengthCm = 20, WidthCm = 30, HeightCm = 40, Desi = 8.3m }).Count, "a small gap is rounding, not a disagreement");

        // Cleared by the operator: derived from the box again, and the drawer says so.
        var cleared = Boxed("O", "20 x 30 x 40 cm", "en-US"); cleared.FieldOrigins = new Dictionary<string, FieldOrigin>(after.FieldOrigins!);
        FieldProvenance.StampManual(after, cleared, Now.AddMinutes(4)); ProductDimensions.Apply(after, cleared, Now.AddMinutes(4));
        Assert.AreEqual(8m, cleared.Desi); Assert.IsTrue(ProductDimensions.IsDerived(cleared));
        StringAssert.Contains(ProductQuickInspect.Build(cleared, Array.Empty<SyncJob>(), Now).Rows.Single(r => r.Label == "Desi (kargo)").Value, "boyuttan");
    }

    [TestMethod]
    public void TheStoreDerivesTheDesiFromTheFeedsBoxAndKeepsTheOperatorsOwn()
    {
        var root = Path.Combine(Path.GetTempPath(), "dims-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CatalogStore(root); var runs = new XmlRunStore(root);
            var a = Source("Tedarikçi A", "tr-TR"); store.SaveSource(a);
            void Import((string Sku, string Weight, string Dimensions)[] rows, DateTime at)
            {
                var run = runs.Start(a.Id, Guid.NewGuid().ToString("N"), TimeSpan.FromMinutes(5), a.ConfigRevision);
                try
                {
                    store.Import(a, rows.Select(r => new CatalogProduct { SourceId = a.Id, SourceKind = "xml", Sku = r.Sku, Name = "Kutu " + r.Sku, Price = 10, Currency = "TRY", Cost = 4, Stock = 3, WeightText = r.Weight, DimensionsText = r.Dimensions }).ToList(), CancellationToken.None, new XmlImportContext { RunId = run, SourceRevision = a.ConfigRevision, ObservedAtUtc = at });
                    runs.Complete(run, new ImportSummary(0, 0, 0));
                }
                catch { runs.Fail(run, "test"); throw; }
            }

            // The feed's box (read the feed's way: a Turkish comma) gives the desi; the box carries the feed's origin, the desi a derived one; a product without a box has no desi and says why.
            Import([("SKU-1", "1,5 kg", "0,2 x 0,3 x 0,4 m"), ("SKU-2", "", "")], Now);
            var one = store.Products().Single(p => p.Sku == "SKU-1"); var two = store.Products().Single(p => p.Sku == "SKU-2");
            Assert.AreEqual(20m, one.LengthCm); Assert.AreEqual(1.5m, one.WeightKg);
            Assert.AreEqual(8m, one.Desi); Assert.IsTrue(ProductDimensions.IsDerived(one)); Assert.AreEqual(Now, FieldProvenance.Of(one, "Desi")!.ObservedUtc);
            Assert.AreEqual(FieldProvenance.FeedKind, FieldProvenance.Of(one, "DimensionsText")!.Kind); Assert.AreEqual(a.Id, FieldProvenance.Of(one, "DimensionsText")!.SourceId);
            Assert.IsNull(two.Desi); Assert.AreEqual(DesiInput.NeedsDimensions, ProductDimensions.Resolve(two).Status);

            // The feed changes the box: the derived desi follows.
            Import([("SKU-1", "1,5 kg", "100 x 100 x 300 mm"), ("SKU-2", "", "")], Now.AddMinutes(10));
            var moved = store.FindProduct(one.Id)!; Assert.AreEqual(1m, moved.Desi); Assert.AreEqual(Now.AddMinutes(10), FieldProvenance.Of(moved, "Desi")!.ObservedUtc);

            // The operator types their own desi: manual, kept when the feed changes the box again, with a warning that names the disagreement.
            // The texts the operator did not touch keep their stored values -- the feed's "1,5 kg" is not re-read with the operator's culture.
            var edit = store.FindProduct(one.Id)!; edit.Desi = 2.5m; store.SaveProduct(edit);
            var typed = store.FindProduct(one.Id)!; Assert.AreEqual(2.5m, typed.Desi); Assert.AreEqual(FieldProvenance.ManualKind, FieldProvenance.Of(typed, "Desi")!.Kind);
            Assert.AreEqual(1.5m, typed.WeightKg, "an unchanged text is not re-read with the operator's culture"); Assert.AreEqual("1,5 kg", typed.WeightText); Assert.AreEqual(10m, typed.LengthCm);
            Import([("SKU-1", "1,5 kg", "20 x 30 x 40 cm"), ("SKU-2", "", "")], Now.AddMinutes(20));
            var kept = store.FindProduct(one.Id)!; Assert.AreEqual(2.5m, kept.Desi); Assert.AreEqual(20m, kept.LengthCm);
            Assert.IsTrue(ProductValidation.Evaluate(kept).Findings.Any(f => f.Field == "Desi" && f.Severity == ProductValidation.Warning && f.Message.Contains("uymuyor")));
            Assert.AreEqual(FieldProvenance.ManualKind, ProductDimensions.Resolve(kept).Origin);

            // Cleared by the operator: derived from the current box again.
            var clear = store.FindProduct(one.Id)!; clear.Desi = null; store.SaveProduct(clear);
            var derived = store.FindProduct(one.Id)!; Assert.AreEqual(8m, derived.Desi); Assert.IsTrue(ProductDimensions.IsDerived(derived));

            // Zero and negative are refused before anything is written -- the operator's desi, the operator's box, and a feed row alike.
            var bad = store.FindProduct(one.Id)!; bad.Desi = 0;
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => store.SaveProduct(bad)).Message, "Desi");
            bad = store.FindProduct(one.Id)!; bad.DimensionsText = "0 x 30 x 40 cm";
            StringAssert.Contains(Assert.ThrowsException<InvalidOperationException>(() => store.SaveProduct(bad)).Message, "pozitif");
            Assert.ThrowsException<InvalidOperationException>(() => Import([("SKU-1", "1,5 kg", "20 x 30 x 0 cm"), ("SKU-2", "", "")], Now.AddMinutes(30)));
            var untouched = store.FindProduct(one.Id)!; Assert.AreEqual(8m, untouched.Desi); Assert.AreEqual(20m, untouched.LengthCm); Assert.AreEqual("20 x 30 x 40 cm", untouched.DimensionsText);
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
