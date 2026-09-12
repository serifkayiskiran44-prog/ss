using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #805 (DESIGN: Product content before-after preview). A read-only look at what a save is about to change to
// the customer-facing text. Unlike the always-visible dirty indicator (#802), this one *does* show values --
// the operator asked to see them -- so the rules are: sanitized, length-capped, and never a live write.
[TestClass]
public sealed class ProductContentDiffTests
{
    static CatalogProduct Product(Action<CatalogProduct>? tweak = null)
    {
        var p = new CatalogProduct
        {
            Id = "p1", Sku = "A", Name = "Kırmızı kupa", Description = "Seramik kupa", Brand = "Marka", Category = "Ev",
            Price = 100m, Stock = 5, UpdatedUtc = new DateTime(2026, 9, 13, 9, 0, 0, DateTimeKind.Utc),
        };
        tweak?.Invoke(p); return p;
    }

    [TestMethod]
    public void AnUnchangedProductPreviewsNothingAndSaysSo()
    {
        var diff = ProductContentDiff.Build(Product(), Product());

        Assert.IsFalse(diff.HasChanges);
        Assert.AreEqual(0, diff.Rows.Count);
        StringAssert.Contains(diff.Headline, "değişiklik yok");
        Assert.IsFalse(diff.IsStale);
    }

    [TestMethod]
    public void EveryChangedContentFieldIsShownBeforeAndAfterAndUnchangedOnesAreLeftOut()
    {
        var before = Product();
        var after = Product(p => { p.Name = "Mavi kupa"; p.Category = "Mutfak"; });

        var diff = ProductContentDiff.Build(before, after);

        Assert.IsTrue(diff.HasChanges);
        CollectionAssert.AreEqual(new[] { "Başlık", "Kategori" }, diff.Rows.Select(r => r.Field).ToArray(), "Only the fields that actually changed appear, in a fixed order.");
        var title = diff.Rows.Single(r => r.Field == "Başlık");
        Assert.AreEqual("Kırmızı kupa", title.Before);
        Assert.AreEqual("Mavi kupa", title.After);
        StringAssert.Contains(diff.Headline, "2 alan");
        Assert.IsFalse(diff.Rows.Any(r => r.Field == "Açıklama"), "An untouched description is not padding for the preview.");
    }

    [TestMethod]
    public void EmptyingOrFillingAFieldReadsAsSuchRatherThanAsABlankLine()
    {
        var cleared = ProductContentDiff.Build(Product(), Product(p => p.Description = ""));
        Assert.AreEqual("(boş)", cleared.Rows.Single().After, "An emptied field is stated, not shown as nothing at all.");

        var filled = ProductContentDiff.Build(Product(p => p.Brand = ""), Product(p => p.Brand = "Yeni marka"));
        Assert.AreEqual("(boş)", filled.Rows.Single().Before);
        Assert.AreEqual("Yeni marka", filled.Rows.Single().After);
    }

    [TestMethod]
    public void LongOrMarkupHeavyTextIsFlattenedAndCappedSoThePreviewStaysReadable()
    {
        var html = "<p>" + new string('a', 4000) + "</p>\r\n<div>son</div>";
        var diff = ProductContentDiff.Build(Product(), Product(p => p.Description = html));

        var row = diff.Rows.Single();
        Assert.IsTrue(row.After.Length <= ProductContentDiff.MaxPreviewLength + 1, $"A 4 KB description must not be pasted whole into a dialog: {row.After.Length}");
        Assert.IsFalse(row.After.Contains('\n'), "Newlines are flattened so a row stays a row.");
        Assert.IsFalse(row.After.Contains('\r'));
        StringAssert.EndsWith(row.After, "…", "The truncation is visible rather than silent.");
        Assert.IsTrue(row.AfterTruncated);
        Assert.IsFalse(row.BeforeTruncated);
    }

    [TestMethod]
    public void APreviewBuiltAgainstAnOlderSavedVersionSaysTheRecordMovedOn()
    {
        var baseline = Product();
        var edited = Product(p => { p.Name = "Mavi kupa"; });
        var stored = Product(p => p.UpdatedUtc = baseline.UpdatedUtc.AddMinutes(5));

        var fresh = ProductContentDiff.Build(baseline, edited, stored);
        Assert.IsFalse(ProductContentDiff.Build(baseline, edited, baseline).IsStale);
        Assert.IsTrue(fresh.IsStale, "The saved record changed under the operator; previewing against a stale baseline would show a diff that no longer applies.");
        StringAssert.Contains(fresh.Warning, "yeniden yükleyin");
    }

    [TestMethod]
    public void ContentPastedFromASupplierCannotCarryASecretIntoThePreview()
    {
        var diff = ProductContentDiff.Build(
            Product(p => p.Description = "eski"),
            Product(p => p.Description = "Authorization: Bearer abc123secret · musteri@example.com"));

        var row = diff.Rows.Single();
        Assert.IsFalse(row.After.Contains("abc123secret", StringComparison.Ordinal), row.After);
        Assert.IsFalse(row.After.Contains("musteri@example.com", StringComparison.Ordinal), row.After);
    }
}
