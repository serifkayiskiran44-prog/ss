using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #800 (DESIGN: Product card source provenance affordance). Which source each field came from, and whether that
// source's mapping has moved on since -- as a summary the operator opens, not another block of card clutter.
// The security line is firm: a source's name is display material, its feed location is not (it routinely
// carries a key in the query string).
[TestClass]
public sealed class ProductProvenanceTests
{
    static readonly DateTime Now = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);

    static CatalogProduct Product(Action<CatalogProduct>? tweak = null)
    {
        var p = new CatalogProduct
        {
            Sku = "A", Name = "Ürün A", SourceKind = "xml", SourceId = "src-1", SourceUpdatedUtc = Now.AddHours(-4),
            PriceSource = "xml", StockSource = "xml", MediaSource = "xml",
        };
        tweak?.Invoke(p); return p;
    }

    static XmlSource Source(Action<XmlSource>? tweak = null)
    {
        var s = new XmlSource { Id = "src-1", Name = "Tedarikçi A", Location = "https://feed.example/list.xml?key=abc123secret", MappingRevision = 4, LastAppliedMappingRevision = 4, LastSuccessfulFeedUtc = Now.AddHours(-4) };
        tweak?.Invoke(s); return s;
    }

    static ProductProvenanceRow Row(ProductProvenanceView view, string field) => view.Rows.Single(r => r.Field == field);

    [TestMethod]
    public void EachFieldNamesTheSourceItCameFromAndOperatorOverridesAreCalledOut()
    {
        var view = ProductProvenance.Build(Product(p => { p.PriceSource = "manual"; p.LockPrice = true; }), Source(), Now);

        CollectionAssert.AreEquivalent(new[] { "Fiyat", "Alış", "Stok", "Görseller", "Başlık", "Açıklama" }, view.Rows.Select(r => r.Field).ToArray()); // #922: the cost has its own row
        Assert.AreEqual("Tedarikçi A", Row(view, "Stok").Origin, "A feed-owned field names its source.");
        StringAssert.Contains(Row(view, "Stok").Detail, "4 saat önce");
        Assert.AreEqual("Elle girildi", Row(view, "Fiyat").Origin, "An operator-set field says so rather than crediting the feed.");
        Assert.IsTrue(Row(view, "Fiyat").IsOperatorOwned);
        StringAssert.Contains(Row(view, "Fiyat").Detail, "kilitli", "A lock is the reason the feed will not take the field back, so it belongs in the provenance.");
        Assert.IsFalse(Row(view, "Stok").IsOperatorOwned);
        Assert.AreEqual(0, view.Warnings.Count);
    }

    [TestMethod]
    public void AMappingRevisionThatHasMovedOnSinceTheLastImportIsFlagged()
    {
        var current = ProductProvenance.Build(Product(), Source(), Now);
        Assert.IsFalse(current.IsRevisionStale);
        StringAssert.Contains(current.SourceSummary, "sürüm 4");

        var moved = ProductProvenance.Build(Product(), Source(s => { s.MappingRevision = 7; s.LastAppliedMappingRevision = 4; }), Now);
        Assert.IsTrue(moved.IsRevisionStale, "The mapping is at 7 but these values were written by 4.");
        StringAssert.Contains(moved.SourceSummary, "sürüm 4");
        CollectionAssert.Contains(moved.Warnings.ToList(), "Kaynak eşlemesi bu içe aktarmadan sonra değişti (sürüm 4 → 7); alan kökenleri güncel olmayabilir.");
    }

    [TestMethod]
    public void AProductWithNoResolvableSourceSaysSoInsteadOfShowingAnEmptySummary()
    {
        var orphan = ProductProvenance.Build(Product(p => p.SourceMissing = true), source: null, Now);
        Assert.AreEqual(ProductProvenance.Unknown, orphan.SourceSummary);
        CollectionAssert.Contains(orphan.Warnings.ToList(), "Bu ürünün kaynağı bulunamıyor; alan kökenleri doğrulanamadı.");
        Assert.IsTrue(orphan.Rows.All(r => r.Origin.Length > 0), "Every field still says something, even if only that it is unknown.");

        var manualProduct = ProductProvenance.Build(new CatalogProduct { Sku = "B", SourceKind = "manual", PriceSource = "manual", StockSource = "manual", MediaSource = "manual" }, null, Now);
        Assert.AreEqual("Elle oluşturuldu", manualProduct.SourceSummary, "A product that never came from a feed is not 'missing a source'.");
        Assert.AreEqual(0, manualProduct.Warnings.Count);
        Assert.IsTrue(manualProduct.Rows.All(r => r.IsOperatorOwned));
    }

    [TestMethod]
    public void MixedOriginsAcrossFieldsAreSummarizedWithoutHidingEitherSide()
    {
        var view = ProductProvenance.Build(Product(p => { p.PriceSource = "manual"; p.MediaSource = "manual"; }), Source(), Now);

        Assert.AreEqual(2, view.Rows.Count(r => r.IsOperatorOwned));
        Assert.AreEqual(4, view.Rows.Count(r => !r.IsOperatorOwned)); // #922: the cost row joined the summary
        StringAssert.Contains(view.Headline, "4 alan kaynaktan"); // #922
        StringAssert.Contains(view.Headline, "2 alan elle");
    }

    [TestMethod]
    public void TheFeedLocationNeverReachesTheSummaryHoweverItIsPhrased()
    {
        var view = ProductProvenance.Build(Product(), Source(s => s.Location = "https://user:pw@feed.example/list.xml?key=abc123secret"), Now);

        var all = view.SourceSummary + " " + view.Headline + " " + string.Join(" ", view.Rows.Select(r => r.Origin + " " + r.Detail)) + " " + string.Join(" ", view.Warnings);
        Assert.IsFalse(all.Contains("abc123secret", StringComparison.Ordinal), all);
        Assert.IsFalse(all.Contains("feed.example", StringComparison.Ordinal), "Even the host is more than a provenance summary needs; the source's name is the identifier operators use: " + all);
        Assert.IsFalse(all.Contains("pw@", StringComparison.Ordinal), all);
        StringAssert.Contains(all, "Tedarikçi A");
    }
}
