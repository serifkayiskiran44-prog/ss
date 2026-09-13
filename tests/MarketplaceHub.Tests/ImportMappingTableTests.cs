using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #824 (DESIGN: Import mapping table readability). Every target field shows required/type/sample/status; a
// missing required field, a duplicate target and an unknown path are named with a reason; samples are masked and
// short; 100+ fields compose; and the summary counts match the rows.
[TestClass]
public sealed class ImportMappingTableTests
{
    static readonly string[] Known = { "sku", "title", "price", "stock", "img", "desc", "ean" };
    static string? Sample(string path) => path switch { "sku" => "ABC-1", "title" => "Kupa", "price" => "12,50", "desc" => "Müşteri ali@example.com için token=abc123 " + new string('x', 200), "img" => "{\"url\":\"a\",\"b\":\"c\"}", _ => null };

    [TestMethod]
    public void RequiredTypeSampleAndStatusAreReadableOnEveryRow()
    {
        var view = ImportMappingTable.Compose(new[] { new MappingRowInput("Sku", "SKU", "sku"), new MappingRowInput("Price", "Satış", "price"), new MappingRowInput("Brand", "Marka", "") }, Known, Sample);

        var sku = view.Rows.Single(r => r.Key == "Sku");
        Assert.IsTrue(sku.Required); Assert.AreEqual("metin", sku.TypeLabel); Assert.AreEqual("ABC-1", sku.Sample); Assert.AreEqual(MappingRowStatus.Mapped, sku.Status);
        var price = view.Rows.Single(r => r.Key == "Price");
        Assert.IsFalse(price.Required); Assert.AreEqual("ondalık sayı", price.TypeLabel); Assert.AreEqual("12,50", price.Sample);
        var brand = view.Rows.Single(r => r.Key == "Brand");
        Assert.AreEqual(MappingRowStatus.Optional, brand.Status); Assert.IsFalse(brand.IsProblem); Assert.AreEqual("", brand.Sample);
        Assert.IsTrue(view.Rows.All(r => r.StatusLabel.Length > 0 && r.TypeLabel.Length > 0));
        StringAssert.Contains(view.Summary, "hazır");
        Assert.IsFalse(view.HasBlocking);
    }

    [TestMethod]
    public void AMissingRequiredFieldIsNamedWithItsReasonAndIsTheFirstProblem()
    {
        var view = ImportMappingTable.Compose(new[] { new MappingRowInput("Brand", "Marka", ""), new MappingRowInput("Sku", "SKU", ""), new MappingRowInput("Name", "Başlık", "title") }, Known, Sample);

        var sku = view.Rows.Single(r => r.Key == "Sku");
        Assert.AreEqual(MappingRowStatus.MissingRequired, sku.Status);
        StringAssert.Contains(sku.Reason, "Zorunlu");
        Assert.AreEqual(1, view.MissingRequired);
        Assert.AreEqual("Sku", view.FirstProblemKey, "The optional empty brand is not a problem; the missing SKU is.");
        Assert.IsTrue(view.HasBlocking);
        StringAssert.Contains(view.Summary, "1 zorunlu eksik");
    }

    [TestMethod]
    public void SkuOrBarcodeIsOneRequirementNotTwo()
    {
        Assert.AreEqual("✱ zorunlu (veya barkod)", ImportMappingTable.RequiredLabel("Sku"));
        Assert.AreEqual("✱ zorunlu (veya SKU)", ImportMappingTable.RequiredLabel("Barcode"));
        Assert.AreEqual("✱ zorunlu", ImportMappingTable.RequiredLabel("Name"));
        Assert.AreEqual("", ImportMappingTable.RequiredLabel("Brand"));

        var barcodeOnly = ImportMappingTable.Compose(new[] { new MappingRowInput("Sku", "SKU", ""), new MappingRowInput("Barcode", "Barkod", "ean"), new MappingRowInput("Name", "Başlık", "title") }, Known, Sample);
        var sku = barcodeOnly.Rows.Single(r => r.Key == "Sku");
        Assert.IsTrue(sku.Required, "SKU is part of a requirement…");
        Assert.AreEqual(MappingRowStatus.Optional, sku.Status, "…but the barcode satisfies it, so an empty SKU is not a problem.");
        StringAssert.Contains(sku.Reason, "boş kalabilir");
        Assert.IsFalse(barcodeOnly.HasBlocking);

        var neither = ImportMappingTable.Compose(new[] { new MappingRowInput("Sku", "SKU", ""), new MappingRowInput("Barcode", "Barkod", ""), new MappingRowInput("Name", "Başlık", "title") }, Known, Sample);
        Assert.AreEqual(2, neither.MissingRequired, "Neither mapped: both members are flagged, the same way the snapshot reports the group.");
        Assert.AreEqual("Sku", neither.FirstProblemKey);
    }

    [TestMethod]
    public void OneXmlPathMappedOntoTwoTargetsIsAConflictOnBoth()
    {
        var view = ImportMappingTable.Compose(new[] { new MappingRowInput("Sku", "SKU", "sku"), new MappingRowInput("Barcode", "Barkod", "sku"), new MappingRowInput("Name", "Başlık", "title") }, Known, Sample);

        Assert.AreEqual(MappingRowStatus.DuplicateTarget, view.Rows.Single(r => r.Key == "Sku").Status);
        Assert.AreEqual(MappingRowStatus.DuplicateTarget, view.Rows.Single(r => r.Key == "Barcode").Status);
        StringAssert.Contains(view.Rows.Single(r => r.Key == "Barcode").Reason, "'sku'");
        Assert.AreEqual(2, view.Duplicates);
        Assert.AreEqual(MappingRowStatus.Mapped, view.Rows.Single(r => r.Key == "Name").Status);
        StringAssert.Contains(view.Summary, "2 çakışma");

        var images = ImportMappingTable.Compose(new[] { new MappingRowInput("ImageUrls", "Görseller", "img|img2"), new MappingRowInput("Sku", "SKU", "sku"), new MappingRowInput("Name", "Başlık", "title") }, Known.Concat(new[] { "img2" }).ToArray(), Sample);
        Assert.AreEqual(MappingRowStatus.Mapped, images.Rows.Single(r => r.Key == "ImageUrls").Status, "Several image paths on one field are not a conflict.");
    }

    [TestMethod]
    public void APathTheScanDidNotFindIsFlaggedButNotWhenNoScanRanYet()
    {
        var scanned = ImportMappingTable.Compose(new[] { new MappingRowInput("Sku", "SKU", "sku"), new MappingRowInput("Name", "Başlık", "headline") }, Known, Sample);
        Assert.AreEqual(MappingRowStatus.UnknownPath, scanned.Rows.Single(r => r.Key == "Name").Status);
        StringAssert.Contains(scanned.Rows.Single(r => r.Key == "Name").Reason, "'headline'");
        Assert.AreEqual(1, scanned.Unknown);

        var unscanned = ImportMappingTable.Compose(new[] { new MappingRowInput("Sku", "SKU", "sku"), new MappingRowInput("Name", "Başlık", "headline") }, Array.Empty<string>(), _ => null);
        Assert.AreEqual(MappingRowStatus.Mapped, unscanned.Rows.Single(r => r.Key == "Name").Status, "Before a scan there is no list to be absent from.");
    }

    [TestMethod]
    public void SamplesAreMaskedShortAndNeverARawBody()
    {
        var view = ImportMappingTable.Compose(new[] { new MappingRowInput("Description", "Açıklama", "desc"), new MappingRowInput("ImageUrls", "Görseller", "img"), new MappingRowInput("Sku", "SKU", "sku"), new MappingRowInput("Name", "Başlık", "title") }, Known, Sample);

        var description = view.Rows.Single(r => r.Key == "Description").Sample;
        Assert.IsFalse(description.Contains("ali@example.com") || description.Contains("abc123"), description);
        Assert.IsTrue(description.Length <= ImportMappingTable.SampleLength, $"{description.Length} chars");
        StringAssert.EndsWith(description, "…");
        Assert.AreEqual(StatusTooltip.RawPayloadHidden, view.Rows.Single(r => r.Key == "ImageUrls").Sample, "A JSON body in a field is evidence, not a sample.");
        Assert.AreEqual("", ImportMappingTable.SafeSample("   "));
    }

    [TestMethod]
    public void AHundredAndFiftyFieldsComposeWithCountsThatMatchTheRows()
    {
        var rows = Enumerable.Range(1, 150).Select(i => new MappingRowInput($"F{i}", $"Alan {i}", i % 10 == 0 ? "" : i % 7 == 0 ? "shared" : $"p{i}")).Prepend(new MappingRowInput("Sku", "SKU", "sku")).Prepend(new MappingRowInput("Name", "Başlık", "")).ToList();
        var known = rows.Select(r => r.Path).Where(p => p.Length > 0 && p != "p5").Distinct().ToArray();

        var view = ImportMappingTable.Compose(rows, known, _ => "v");

        Assert.AreEqual(rows.Count, view.Rows.Count);
        Assert.AreEqual(view.Rows.Count(r => r.Status == MappingRowStatus.Mapped), view.Mapped);
        Assert.AreEqual(1, view.MissingRequired, "Only the empty required Name.");
        Assert.AreEqual(view.Rows.Count(r => r.Status == MappingRowStatus.DuplicateTarget), view.Duplicates);
        Assert.IsTrue(view.Duplicates >= 2, "The shared path lands on many fields.");
        Assert.AreEqual(1, view.Unknown, "p5 was left out of the scan.");
        Assert.AreEqual("Name", view.FirstProblemKey);
        Assert.IsTrue(view.HasBlocking);
    }
}
