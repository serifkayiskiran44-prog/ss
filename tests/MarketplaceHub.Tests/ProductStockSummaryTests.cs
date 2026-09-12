using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #799 (DESIGN: Product card stock status composition). The card has to compose what is on hand, what the
// safety buffer withholds, what a maximum cap keeps back, and whether the figure is stale -- with signals that
// are not colour. The constraint that shapes the design: the channel's available quantity is owned by
// CatalogStore.PreviewStock, and this summary must consume that projection rather than re-deriving it.
[TestClass]
public sealed class ProductStockSummaryTests
{
    static readonly DateTime Now = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);

    static CatalogProduct Product(Action<CatalogProduct>? tweak = null)
    {
        var p = new CatalogProduct { Sku = "A", Name = "Ürün A", Stock = 10, Active = true, SourceKind = "xml", SourceUpdatedUtc = Now.AddHours(-2) };
        tweak?.Invoke(p); return p;
    }

    static StockPolicy Policy(int safety = 3, int? maximum = null) => new() { Channel = "local", Shop = "default", SafetyStock = safety, MaximumStock = maximum, Enabled = true };

    [TestMethod]
    public void TheSummaryReportsTheProjectionItIsGivenRatherThanRecomputingIt()
    {
        // A projection that deliberately disagrees with the naive on-hand-minus-buffer arithmetic: if the summary
        // did its own maths it would say 7, and the card would then contradict what the channel actually sends.
        var summary = ProductStockSummary.Build(Product(), Policy(safety: 3), projectedAvailable: 4, Now);

        Assert.AreEqual("4", summary.Available, "The owner's projection wins; the card is a presenter, not a second calculator.");
        Assert.AreEqual("10", summary.OnHand);
        Assert.AreEqual("3", summary.SafetyBuffer);
        StringAssert.Contains(summary.Withheld, "6", "10 on hand against a projected 4 means 6 units are held back, whatever the reason.");
    }

    [TestMethod]
    public void WithoutAChannelProjectionTheCardSaysSoInsteadOfInventingANumber()
    {
        var summary = ProductStockSummary.Build(Product(), policy: null, projectedAvailable: null, Now);

        Assert.AreEqual("—", summary.Available);
        Assert.AreEqual(ProductStockSummary.NoProjection, summary.Level);
        StringAssert.Contains(summary.Label, "projeksiyon");
        Assert.AreEqual("10", summary.OnHand, "On-hand stock is the product's own field and is still shown.");
    }

    [TestMethod]
    public void ZeroNegativeAndBufferOnlyStockEachReadDifferently()
    {
        var empty = ProductStockSummary.Build(Product(p => p.Stock = 0), Policy(), 0, Now);
        Assert.AreEqual(ProductStockSummary.OutOfStock, empty.Level);
        Assert.AreEqual("0", empty.OnHand);

        var bufferOnly = ProductStockSummary.Build(Product(p => p.Stock = 3), Policy(safety: 3), 0, Now);
        Assert.AreEqual(ProductStockSummary.BufferOnly, bufferOnly.Level, "Stock exists but the buffer withholds all of it; that is not the same as empty.");
        Assert.AreNotEqual(empty.Label, bufferOnly.Label);

        // Corrupt or hand-edited data: a negative must be reported, never rendered as if it were sellable.
        var negative = ProductStockSummary.Build(Product(p => p.Stock = -5), Policy(), 0, Now);
        Assert.AreEqual(ProductStockSummary.Invalid, negative.Level);
        CollectionAssert.Contains(negative.Warnings.ToList(), "Stok negatif görünüyor; kayıt elle düzeltilmeli.");

        var capped = ProductStockSummary.Build(Product(p => p.Stock = 100), Policy(safety: 0, maximum: 20), 20, Now);
        Assert.AreEqual(ProductStockSummary.Capped, capped.Level);
        Assert.IsTrue(capped.Label.Contains("üst sınır", StringComparison.CurrentCultureIgnoreCase), capped.Label);
    }

    [TestMethod]
    public void EveryLevelSignalsItselfWithoutColourAndAStaleFeedIsCalledOut()
    {
        foreach (var level in new[] { ProductStockSummary.Ready, ProductStockSummary.BufferOnly, ProductStockSummary.Capped, ProductStockSummary.OutOfStock, ProductStockSummary.Invalid, ProductStockSummary.NoProjection })
        {
            var info = ProductStockSummary.Describe(level);
            Assert.IsFalse(string.IsNullOrWhiteSpace(info.Glyph), level);
            Assert.IsFalse(string.IsNullOrWhiteSpace(info.Label), level);
        }
        var labels = new[] { ProductStockSummary.Ready, ProductStockSummary.BufferOnly, ProductStockSummary.Capped, ProductStockSummary.OutOfStock, ProductStockSummary.Invalid, ProductStockSummary.NoProjection }
            .Select(l => ProductStockSummary.Describe(l).Label).ToArray();
        Assert.AreEqual(labels.Length, labels.Distinct().Count(), "No two levels may read the same with the colour removed.");

        var stale = ProductStockSummary.Build(Product(p => p.SourceUpdatedUtc = Now.AddDays(-9)), Policy(), 7, Now);
        Assert.IsTrue(stale.IsStale);
        StringAssert.Contains(stale.Updated, "9 gün önce");
        CollectionAssert.Contains(stale.Warnings.ToList(), "Stok verisi kaynaktan 7 günden uzun süredir güncellenmedi.");
        Assert.AreEqual("7", stale.Available, "Flagging staleness must not alter the projected quantity.");

        var manual = ProductStockSummary.Build(Product(p => { p.SourceKind = "manual"; p.SourceUpdatedUtc = Now.AddDays(-9); }), Policy(), 7, Now);
        Assert.IsFalse(manual.IsStale, "A manually maintained product is as old as the operator left it, not stale.");
    }

    [TestMethod]
    public void LargeQuantitiesAreGroupedAndKeptInTheirOwnFields()
    {
        var summary = ProductStockSummary.Build(Product(p => p.Stock = 1234567), Policy(safety: 1000), 1233567, Now);

        Assert.AreEqual(1234567.ToString("N0", CultureInfo.CurrentCulture), summary.OnHand);
        Assert.AreEqual(1233567.ToString("N0", CultureInfo.CurrentCulture), summary.Available);
        Assert.IsFalse(summary.Available.Contains(summary.OnHand, StringComparison.Ordinal), "Quantities live in separate fields so a long number cannot run into its neighbour.");
    }

    [TestMethod]
    public void TheProjectionTheCardShowsIsTheOneTheRealStoreProduces()
    {
        var root = Path.Combine(Path.GetTempPath(), "stock-summary-" + Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = new CatalogStore(root);
            var source = new XmlSource { Id = "src", Name = "Src" };
            catalog.Import(source, new[] { new CatalogProduct { SourceId = source.Id, Sku = "A", Name = "Ürün A", Price = 10, Stock = 10, Active = true } });
            catalog.SaveStockPolicy(new StockPolicy { Channel = "local", Shop = "default", SafetyStock = 3, MaximumStock = 5, Enabled = true });
            var product = catalog.Products().Single();

            var projected = catalog.PreviewStock("local", "default", product.Id);
            var summary = ProductStockSummary.Build(product, catalog.GetStockPolicy("local", "default"), projected, DateTime.UtcNow);

            Assert.AreEqual(5, projected, "min(10 - 3, cap 5) is the store's answer.");
            Assert.AreEqual("5", summary.Available);
            Assert.AreEqual(ProductStockSummary.Capped, summary.Level);
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
