using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #832 (DESIGN: XML diff colour-independent semantics). Four kinds from raw values; each kind has its own glyph,
// word and border weight (colour is extra, and under high contrast comes from SystemColors); values are masked,
// flattened and capped; the product content preview (#805) now carries the kind too.
[TestClass]
public sealed class FieldDiffTests
{
    [TestMethod]
    public void KindsComeFromTheRawValuesAndANewProductIsAllAdded()
    {
        Assert.AreEqual(DiffKind.Added, FieldDiff.Classify("", "Acme"));
        Assert.AreEqual(DiffKind.Removed, FieldDiff.Classify("Uzun açıklama", "  "));
        Assert.AreEqual(DiffKind.Changed, FieldDiff.Classify("10", "12"));
        Assert.AreEqual(DiffKind.Unchanged, FieldDiff.Classify("a", "a"));
        Assert.AreEqual(DiffKind.Unchanged, FieldDiff.Classify(null, ""), "Empty on both sides is not a removal.");

        var existing = new CatalogProduct { Sku = "S1", Name = "Eski başlık", Description = "Açıklama", Price = 10, Currency = "TRY", Stock = 5 };
        var incoming = new CatalogProduct { Sku = "S1", Name = "Yeni başlık", Brand = "Acme", Price = 12, Currency = "TRY", Stock = 5, ImageUrls = "https://a/1.jpg\nhttps://a/2.jpg" };
        var rows = FieldDiff.Build(existing, incoming, FieldDiff.ProductFields, includeUnchanged: true);
        Assert.AreEqual(FieldDiff.ProductFields.Count, rows.Count, "Every product field has a row when unchanged rows are kept.");
        Assert.AreEqual(DiffKind.Changed, rows.Single(r => r.Field == "Başlık").Kind);
        Assert.AreEqual(DiffKind.Removed, rows.Single(r => r.Field == "Açıklama").Kind);
        Assert.AreEqual(DiffKind.Added, rows.Single(r => r.Field == "Marka").Kind);
        Assert.AreEqual(DiffKind.Changed, rows.Single(r => r.Field == "Fiyat").Kind);
        Assert.AreEqual(DiffKind.Unchanged, rows.Single(r => r.Field == "Stok").Kind);
        Assert.AreEqual(DiffKind.Added, rows.Single(r => r.Field == "Görseller").Kind); Assert.AreEqual("2 görsel", rows.Single(r => r.Field == "Görseller").After);
        Assert.AreEqual("2 eklendi · 2 değişti · 1 kaldırıldı · 5 aynı", FieldDiff.Summary(rows), "Counts add up to the field count.");

        var fresh = FieldDiff.Build<CatalogProduct>(null, incoming, FieldDiff.ProductFields, includeUnchanged: false);
        Assert.IsTrue(fresh.All(r => r.Kind == DiffKind.Added), "Nothing existed: every present field is added.");
        Assert.IsFalse(fresh.Any(r => r.Field == "Açıklama"), "An empty incoming field on a new product is unchanged and dropped.");
    }

    [TestMethod]
    public void EachKindIsToldApartWithoutColourAndHighContrastUsesSystemColors()
    {
        var kinds = Enum.GetValues<DiffKind>();
        var glyphs = kinds.Select(k => FieldDiff.Present(k, false).Glyph).ToList();
        var words = kinds.Select(k => FieldDiff.Present(k, false).Word).ToList();
        Assert.AreEqual(kinds.Length, glyphs.Distinct().Count(), "Four kinds, four glyphs.");
        Assert.AreEqual(kinds.Length, words.Distinct().Count(), "Four kinds, four words.");
        Assert.IsTrue(FieldDiff.Present(DiffKind.Added, false).BorderWeight > FieldDiff.Present(DiffKind.Unchanged, false).BorderWeight, "An unchanged row is the quietest border.");
        Assert.IsTrue(FieldDiff.Present(DiffKind.Removed, false).BorderWeight >= FieldDiff.Present(DiffKind.Changed, false).BorderWeight);
        CollectionAssert.AreEqual(new[] { "eklendi", "kaldırıldı", "değişti", "aynı" }, words);

        var hc = kinds.Select(k => FieldDiff.Present(k, true).Accent).ToList();
        var system = new[] { SystemColors.WindowTextColor, SystemColors.HotTrackColor, SystemColors.HighlightColor, SystemColors.GrayTextColor, SystemColors.ControlTextColor, SystemColors.WindowTextColor };
        Assert.IsTrue(hc.All(c => system.Contains(c)), "Under high contrast every accent is a system colour: " + string.Join(",", hc));
        Assert.AreEqual("＋ eklendi", FieldDiff.Present(DiffKind.Added, false).Badge);
    }

    [TestMethod]
    public void ValuesAreMaskedFlattenedAndCappedAndTheContentPreviewCarriesTheKind()
    {
        var secret = FieldDiff.Row("Açıklama", "Satır 1\r\nSatır  2 token=SECRETXYZ123", "");
        Assert.AreEqual(DiffKind.Removed, secret.Kind);
        Assert.IsFalse(secret.Before.Contains("SECRETXYZ123"), secret.Before);
        Assert.IsFalse(secret.Before.Contains('\n') || secret.Before.Contains("  "), "One line, single spaces: " + secret.Before);
        Assert.AreEqual(FieldDiff.Empty, secret.After);

        var longText = FieldDiff.Row("Açıklama", "", new string('a', 1000));
        Assert.IsTrue(longText.AfterTruncated); Assert.AreEqual(FieldDiff.MaxLength + 1, longText.After.Length);

        var before = new CatalogProduct { Name = "Eski", Description = "Var" };
        var after = new CatalogProduct { Name = "Yeni", Description = "" , Brand = "Acme" };
        var view = ProductContentDiff.Build(before, after);
        Assert.AreEqual(DiffKind.Changed, view.Rows.Single(r => r.Field == "Başlık").Kind);
        Assert.AreEqual(DiffKind.Removed, view.Rows.Single(r => r.Field == "Açıklama").Kind);
        Assert.AreEqual(DiffKind.Added, view.Rows.Single(r => r.Field == "Marka").Kind);
    }
}
