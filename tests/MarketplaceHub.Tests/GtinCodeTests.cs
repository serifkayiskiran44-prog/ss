using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #906 (PRODUCT GTIN: format and check-digit validation). A GTIN is 8, 12, 13 or 14 digits with a correct mod-10
// check digit; anything else is a custom barcode -- allowed as a barcode, refused in the GTIN field. Nothing is
// normalised silently: leading zeros stay, inner separators are not stripped, a wrong check digit is named, not fixed.
[TestClass]
public sealed class GtinCodeTests
{
    static readonly DateTime Now = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);
    static XmlSource Source(string name) => new() { Name = name, Location = "https://feeds.example.com/" + name.ToLowerInvariant() + ".xml", Enabled = true, IntervalMinutes = 30, ItemPath = "/p", Fields = new Dictionary<string, string> { ["Sku"] = "s", ["Name"] = "n", ["Cost"] = "c", ["Stock"] = "q", ["Gtin"] = "g" } };

    [TestMethod]
    public void EverySupportedLengthIsCheckedLeadingZerosStayAndCustomCodesAreTold()
    {
        foreach (var (code, kind, label) in new[] { ("96385074", BarcodeKind.Gtin8, "GTIN-8"), ("036000291452", BarcodeKind.Gtin12, "GTIN-12"), ("4006381333931", BarcodeKind.Gtin13, "GTIN-13"), ("00012345678905", BarcodeKind.Gtin14, "GTIN-14") })
        {
            var verdict = GtinCode.Inspect(code);
            Assert.AreEqual(kind, verdict.Kind, code); Assert.IsTrue(verdict.IsGtin && verdict.ChecksumValid, code); StringAssert.Contains(verdict.Words, label); StringAssert.Contains(verdict.Words, "sağlama doğru");
            Assert.AreEqual(code, verdict.Raw, "the value is never rewritten"); Assert.AreEqual(14, verdict.Canonical14.Length); Assert.IsTrue(GtinCode.IsValidGtin(code));
        }
        foreach (var (code, expected) in new[] { ("96385075", 4), ("036000291453", 2), ("4006381333930", 1), ("00012345678906", 5) })
        {
            var verdict = GtinCode.Inspect(code);
            Assert.AreEqual(BarcodeKind.InvalidGtin, verdict.Kind, code); Assert.IsTrue(verdict.IsGtin); Assert.IsFalse(verdict.ChecksumValid);
            StringAssert.Contains(verdict.Words, $"beklenen {expected}"); StringAssert.Contains(verdict.Words, "değer değiştirilmedi"); Assert.AreEqual(code, verdict.Raw); Assert.AreEqual("", verdict.Canonical14); Assert.IsFalse(GtinCode.IsValidGtin(code));
        }

        // Leading zeros stay as given; the 14-digit comparison form equates the padded and the unpadded code.
        Assert.AreEqual("00012345678905", GtinCode.Inspect("00012345678905").Raw);
        Assert.AreEqual(GtinCode.Inspect("00012345678905").Canonical14, GtinCode.Inspect("012345678905").Canonical14);

        // Custom codes: letters or dashes, an unsupported digit count, an inner space (never stripped); the ends are trimmed; empty is empty.
        var custom = GtinCode.Inspect("ABC-123"); Assert.AreEqual(BarcodeKind.Custom, custom.Kind); Assert.IsFalse(custom.IsGtin); StringAssert.Contains(custom.Words, "GTIN değil");
        var eleven = GtinCode.Inspect("12345678901"); Assert.AreEqual(BarcodeKind.Custom, eleven.Kind); StringAssert.Contains(eleven.Words, "11 hane"); StringAssert.Contains(eleven.Words, "GTIN uzunluğu değil");
        Assert.AreEqual(BarcodeKind.Custom, GtinCode.Inspect("4006 381333931").Kind, "an inner space is not stripped");
        Assert.AreEqual(BarcodeKind.Gtin8, GtinCode.Inspect(" 96385074 ").Kind); Assert.AreEqual("96385074", GtinCode.Inspect(" 96385074 ").Raw);
        Assert.AreEqual(BarcodeKind.Empty, GtinCode.Inspect(null).Kind); Assert.AreEqual(BarcodeKind.Empty, GtinCode.Inspect("  ").Kind);
        Assert.AreEqual(1, GtinCode.CheckDigit("400638133393")); Assert.AreEqual(4, GtinCode.CheckDigit("9638507"));
        Assert.ThrowsException<ArgumentException>(() => GtinCode.CheckDigit("40A6"));

        // Validation: an invalid or custom value in the GTIN field is refused; a barcode may be custom, but a GTIN-looking one with a wrong check digit is flagged.
        static IReadOnlyList<ProductValidationFinding> Findings(string gtin, string barcode) => ProductValidation.Evaluate(new CatalogProduct { Sku = "SKU-1", Name = "Kupa", Currency = "TRY", Gtin = gtin, Barcode = barcode }).Findings;
        Assert.IsTrue(Findings("4006381333930", "").Any(f => f.Field == "GTIN" && f.Severity == ProductValidation.Blocking && f.Message.Contains("sağlama hanesi")));
        Assert.IsTrue(Findings("ABC", "").Any(f => f.Field == "GTIN" && f.Severity == ProductValidation.Blocking && f.Message.Contains("GTIN değil")));
        Assert.IsFalse(Findings("4006381333931", "").Any(f => f.Field == "GTIN"));
        Assert.IsFalse(Findings("", "").Any(f => f.Field == "GTIN"));
        Assert.IsTrue(Findings("", "4006381333930").Any(f => f.Field == "Barkod" && f.Severity == ProductValidation.Warning && f.Message.Contains("GTIN gibi")));
        Assert.IsFalse(Findings("", "ABC-123").Any(f => f.Field == "Barkod"));
    }

    [TestMethod]
    public void TheStoreKeepsAValidGtinExactlyAndRefusesAnInvalidOneWithoutTouchingIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "gtin-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new CatalogStore(root); var runs = new XmlRunStore(root);
            var a = Source("Tedarikçi A"); var b = Source("Tedarikçi B"); store.SaveSource(a); store.SaveSource(b);
            void Import(XmlSource s, (string Sku, string Gtin, string Barcode)[] rows, DateTime at)
            {
                var run = runs.Start(s.Id, Guid.NewGuid().ToString("N"), TimeSpan.FromMinutes(5), s.ConfigRevision);
                try
                {
                    store.Import(s, rows.Select(r => new CatalogProduct { SourceId = s.Id, SourceKind = "xml", Sku = r.Sku, Gtin = r.Gtin, Barcode = r.Barcode, Name = "Ürün " + r.Sku, Price = 10, Currency = "TRY", Cost = 4, Stock = 3 }).ToList(), CancellationToken.None, new XmlImportContext { RunId = run, SourceRevision = s.ConfigRevision, ObservedAtUtc = at });
                    runs.Complete(run, new ImportSummary(0, 0, 0));
                }
                catch { runs.Fail(run, "test"); throw; }
            }

            // Valid GTINs are stored exactly as given, leading zeros included; a custom barcode is fine.
            Import(a, [("SKU-1", "4006381333931", "4006381333931"), ("SKU-2", "00012345678905", "ABC-123")], Now);
            Assert.AreEqual("00012345678905", store.Products().Single(p => p.Sku == "SKU-2").Gtin); Assert.AreEqual("ABC-123", store.Products().Single(p => p.Sku == "SKU-2").Barcode);
            Assert.AreEqual("4006381333931", store.Products().Single(p => p.Sku == "SKU-1").Gtin);

            // An invalid GTIN in a feed refuses the import before anything is written -- nothing is corrected.
            var refused = Assert.ThrowsException<InvalidOperationException>(() => Import(b, [("SKU-3", "4006381333930", "")], Now.AddMinutes(5)));
            StringAssert.Contains(refused.Message, "sağlama hanesi"); Assert.AreEqual(2, store.Products().Count);

            // The operator cannot save an invalid GTIN either; the stored value stays.
            var edit = store.FindProduct(store.Products().Single(p => p.Sku == "SKU-1").Id)!; edit.Gtin = "96385075";
            var saveRefused = Assert.ThrowsException<InvalidOperationException>(() => store.SaveProduct(edit)); StringAssert.Contains(saveRefused.Message, "beklenen 4");
            Assert.AreEqual("4006381333931", store.FindProduct(edit.Id)!.Gtin);
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
