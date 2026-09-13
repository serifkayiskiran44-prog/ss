using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #907 (PRODUCT UNITS: weight and dimension units). A feed's "1,5 kg" or "20 x 30 x 40 cm" is kept as given and,
// beside it, as canonical kilograms / centimetres when it can be read with the source's number culture; a value
// without a unit is ambiguous and stays text only; an unsupported unit stays text only; a negative or out-of-range
// value is refused. Nothing is guessed.
[TestClass]
public sealed class ProductUnitsTests
{
    static readonly DateTime Now = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);
    static XmlSource Source(string name, string culture) => new() { Name = name, Location = "https://feeds.example.com/" + name.ToLowerInvariant() + ".xml", Enabled = true, IntervalMinutes = 30, ItemPath = "/p", NumberCultureName = culture, Fields = new Dictionary<string, string> { ["Sku"] = "s", ["Name"] = "n", ["Cost"] = "c", ["Stock"] = "q", ["Weight"] = "w", ["Dimensions"] = "d" } };

    [TestMethod]
    public void QuantitiesConvertByTheirUnitReadNumbersByCultureAndRefuseNegativeOverflowAndGuessing()
    {
        // Conversions to the canonical unit.
        Assert.AreEqual(1.5m, ProductUnits.Parse("1500 g", UnitKind.Weight, "tr-TR").Canonical);
        Assert.AreEqual(1.5m, ProductUnits.Parse("1,5 kg", UnitKind.Weight, "tr-TR").Canonical);
        Assert.AreEqual(1.5m, ProductUnits.Parse("1.5kg", UnitKind.Weight, "en-US").Canonical);
        Assert.AreEqual(2000m, ProductUnits.Parse("2 t", UnitKind.Weight, "en-US").Canonical);
        Assert.AreEqual(0.25m, ProductUnits.Parse("250 GR", UnitKind.Weight, "en-US").Canonical, "units are read regardless of case");
        Assert.AreEqual(15m, ProductUnits.Parse("150 mm", UnitKind.Length, "tr-TR").Canonical);
        Assert.AreEqual(30m, ProductUnits.Parse("0,3 m", UnitKind.Length, "tr-TR").Canonical);
        Assert.AreEqual("kg", ProductUnits.Parse("1,5 kg", UnitKind.Weight, "tr-TR").Unit); Assert.AreEqual("1,5 kg", ProductUnits.Parse(" 1,5 kg ", UnitKind.Weight, "tr-TR").Original, "the text is kept as given, trimmed at the ends");

        // Decimal culture: the same text means different numbers in different cultures, and is read the source's way.
        Assert.AreEqual(1.5m, ProductUnits.Parse("1,500 g", UnitKind.Weight, "en-US").Canonical, "en-US: a thousands separator");
        Assert.AreEqual(0.0015m, ProductUnits.Parse("1,500 g", UnitKind.Weight, "tr-TR").Canonical, "tr-TR: a decimal separator");
        Assert.AreEqual(1.5m, ProductUnits.Parse("1 500 g", UnitKind.Weight, "tr-TR").Canonical, "a space between groups");

        // Negative and overflow are refused (blocking); nothing is stored as canonical.
        var negative = ProductUnits.Parse("-2 kg", UnitKind.Weight, "en-US"); Assert.IsNull(negative.Canonical); Assert.IsTrue(negative.Blocking); StringAssert.Contains(negative.Diagnostic, "negatif");
        var huge = ProductUnits.Parse("999999999999999999999999999999999 kg", UnitKind.Weight, "en-US"); Assert.IsNull(huge.Canonical); Assert.IsTrue(huge.Blocking); StringAssert.Contains(huge.Diagnostic, "aralık dışı");
        var tooHeavy = ProductUnits.Parse("200000 kg", UnitKind.Weight, "en-US"); Assert.IsTrue(tooHeavy.Blocking); StringAssert.Contains(tooHeavy.Diagnostic, "aralık dışı");

        // A missing unit is ambiguous, an unsupported unit is not guessed, an unreadable text is named -- none of them blocking, all keep the original.
        var bare = ProductUnits.Parse("2", UnitKind.Weight, "tr-TR"); Assert.IsNull(bare.Canonical); Assert.IsFalse(bare.Blocking); StringAssert.Contains(bare.Diagnostic, "birim yok"); StringAssert.Contains(bare.Diagnostic, "kg mı g mı"); Assert.AreEqual("2", bare.Original);
        var pounds = ProductUnits.Parse("3 lb", UnitKind.Weight, "en-US"); Assert.IsNull(pounds.Canonical); StringAssert.Contains(pounds.Diagnostic, "desteklenmeyen birim: lb");
        var words = ProductUnits.Parse("ağır", UnitKind.Weight, "tr-TR"); Assert.IsNull(words.Canonical); StringAssert.Contains(words.Diagnostic, "okunamadı");
        var empty = ProductUnits.Parse("", UnitKind.Weight, "tr-TR"); Assert.IsNull(empty.Canonical); Assert.AreEqual("", empty.Diagnostic);

        // Dimensions: three lengths, a unit written once or per side; the box gives its desi.
        var box = ProductUnits.ParseDimensions("20 x 30 x 40 cm", "tr-TR");
        Assert.IsTrue(box.IsKnown); Assert.AreEqual(20m, box.LengthCm); Assert.AreEqual(30m, box.WidthCm); Assert.AreEqual(40m, box.HeightCm); Assert.AreEqual(8m, box.Desi);
        var millimetres = ProductUnits.ParseDimensions("200x300x400 mm", "en-US"); Assert.AreEqual(20m, millimetres.LengthCm); Assert.AreEqual(40m, millimetres.HeightCm);
        Assert.AreEqual(30m, ProductUnits.ParseDimensions("20 cm x 30 cm x 40 cm", "en-US").WidthCm);
        StringAssert.Contains(ProductUnits.ParseDimensions("20x30", "en-US").Diagnostic, "üç boyut");
        var unitless = ProductUnits.ParseDimensions("20x30x40", "en-US"); Assert.IsFalse(unitless.IsKnown); Assert.IsFalse(unitless.Blocking); StringAssert.Contains(unitless.Diagnostic, "birim yok"); Assert.AreEqual("20x30x40", unitless.Original);
        Assert.IsTrue(ProductUnits.ParseDimensions("-1 x 2 x 3 cm", "en-US").Blocking);

        // Applied to a product: canonical values beside the originals, findings for what could not be read; the stored-record view warns without re-reading numbers.
        var product = new CatalogProduct { Sku = "SKU-1", Name = "Kupa", Currency = "TRY", WeightText = "1,5 kg", DimensionsText = "20x30x40" };
        var findings = ProductUnits.Apply(product, "tr-TR");
        Assert.AreEqual(1.5m, product.WeightKg); Assert.IsNull(product.LengthCm); Assert.AreEqual("1,5 kg", product.WeightText);
        Assert.AreEqual(1, findings.Count); Assert.AreEqual("Boyut", findings[0].Field); Assert.IsFalse(findings[0].Blocking);
        var stored = ProductUnits.Findings(product); Assert.AreEqual(1, stored.Count); StringAssert.Contains(stored[0].Message, "cm'ye çevrilmedi");
        var validation = ProductValidation.Evaluate(product).Findings;
        Assert.IsTrue(validation.Any(f => f.Field == "Boyut" && f.Severity == ProductValidation.Warning)); Assert.IsFalse(validation.Any(f => f.Field == "Ağırlık"));
        StringAssert.Contains(ProductUnits.DescribeWeight(product), "kg (kaynak: 1,5 kg)"); StringAssert.Contains(ProductUnits.DescribeDimensions(product), "cm'ye çevrilmedi");
    }

    [TestMethod]
    public void TheImportAndTheSaveStoreCanonicalValuesBesideTheOriginalsAndRefuseABadValue()
    {
        var root = Path.Combine(Path.GetTempPath(), "units-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CatalogStore(root); var runs = new XmlRunStore(root);
            var a = Source("Tedarikçi A", "tr-TR"); var b = Source("Tedarikçi B", "en-US"); store.SaveSource(a); store.SaveSource(b);
            void Import(XmlSource s, (string Sku, string Weight, string Dimensions)[] rows, DateTime at)
            {
                var run = runs.Start(s.Id, Guid.NewGuid().ToString("N"), TimeSpan.FromMinutes(5), s.ConfigRevision);
                try
                {
                    store.Import(s, rows.Select(r => new CatalogProduct { SourceId = s.Id, SourceKind = "xml", Sku = r.Sku, Name = "Ürün " + r.Sku, Price = 10, Currency = "TRY", Cost = 4, Stock = 3, WeightText = r.Weight, DimensionsText = r.Dimensions }).ToList(), CancellationToken.None, new XmlImportContext { RunId = run, SourceRevision = s.ConfigRevision, ObservedAtUtc = at });
                    runs.Complete(run, new ImportSummary(0, 0, 0));
                }
                catch { runs.Fail(run, "test"); throw; }
            }

            // A Turkish feed: the comma is the decimal separator; a unit-less value stays text only.
            Import(a, [("SKU-1", "1,5 kg", "20 x 30 x 40 cm"), ("SKU-2", "2", "")], Now);
            var one = store.Products().Single(p => p.Sku == "SKU-1"); var two = store.Products().Single(p => p.Sku == "SKU-2");
            Assert.AreEqual(1.5m, one.WeightKg); Assert.AreEqual("1,5 kg", one.WeightText); Assert.AreEqual(20m, one.LengthCm); Assert.AreEqual(40m, one.HeightCm); Assert.AreEqual("20 x 30 x 40 cm", one.DimensionsText);
            Assert.IsNull(two.WeightKg); Assert.AreEqual("2", two.WeightText);
            Assert.IsTrue(ProductValidation.Evaluate(two).Findings.Any(f => f.Field == "Ağırlık" && f.Severity == ProductValidation.Warning));

            // A negative weight refuses the import before anything is written.
            var refused = Assert.ThrowsException<InvalidOperationException>(() => Import(b, [("SKU-3", "-2 kg", "")], Now.AddMinutes(5)));
            StringAssert.Contains(refused.Message, "negatif"); Assert.AreEqual(2, store.Products().Count);

            // The feed updates the mapped fields on the next run, read the source's way again.
            Import(a, [("SKU-1", "1600 g", "200 x 300 x 400 mm"), ("SKU-2", "2 kg", "")], Now.AddMinutes(10));
            var again = store.FindProduct(one.Id)!; Assert.AreEqual(1.6m, again.WeightKg); Assert.AreEqual(20m, again.LengthCm); Assert.AreEqual("1600 g", again.WeightText);
            Assert.AreEqual(2m, store.FindProduct(two.Id)!.WeightKg);

            // The operator's own text is read the operator's way and refused when it cannot stand.
            var edit = store.FindProduct(two.Id)!; edit.WeightText = "750 g"; store.SaveProduct(edit);
            Assert.AreEqual(0.75m, store.FindProduct(two.Id)!.WeightKg);
            var bad = store.FindProduct(two.Id)!; bad.WeightText = "-1 kg";
            Assert.ThrowsException<InvalidOperationException>(() => store.SaveProduct(bad)); Assert.AreEqual(0.75m, store.FindProduct(two.Id)!.WeightKg);
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
