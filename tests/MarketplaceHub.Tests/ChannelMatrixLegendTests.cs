using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #843 (DESIGN: Channel matrix status legend). One table explains every state the service records and the two
// facts that outrank them; glyph and word are distinct per state; a cell and the legend agree by construction;
// the legend counts only the states present; "local only" is the connection catalogue's fact, never a guess.
[TestClass]
public sealed class ChannelMatrixLegendTests
{
    static ChannelListingMatrixRow Row(string productId, string channel, string shop, string mapping, string auth = "CONNECTED", string capabilities = "ProductsRead") =>
        new(productId, "SKU-" + productId, "Ürün " + productId, channel, channel.ToUpperInvariant(), shop, mapping, "", "None", "", null, auth, capabilities);

    [TestMethod]
    public void TheLegendExplainsEveryRecordedStateWithDistinctGlyphsAndWords()
    {
        foreach (var status in ChannelMatrixLegend.ServiceStatuses)
            Assert.AreNotEqual(ChannelMatrixLegend.None, ChannelMatrixLegend.For(status, "CONNECTED", false).Key, status + " must have its own legend entry.");
        Assert.AreEqual(ChannelMatrixLegend.Entries.Count, ChannelMatrixLegend.Entries.Select(e => e.Glyph).Distinct().Count(), "Every entry has its own glyph.");
        Assert.AreEqual(ChannelMatrixLegend.Entries.Count, ChannelMatrixLegend.Entries.Select(e => e.Word).Distinct().Count(), "Every entry has its own word.");
        Assert.IsTrue(ChannelMatrixLegend.Entries.All(e => e.Description.Length > 10), "Every entry says what it means.");

        Assert.AreEqual(ChannelMatrixLegend.LocalOnly, ChannelMatrixLegend.For("SYNCED", "AUTH_ERROR", true).Key, "A channel without live capability outranks even a connection problem: there is nothing to connect to.");
        Assert.AreEqual(ChannelMatrixLegend.AuthError, ChannelMatrixLegend.For("SYNCED", "AUTH_ERROR", false).Key, "On a live channel a dead connection outranks the mapping state.");
        Assert.AreEqual(ChannelMatrixLegend.LocalOnly, ChannelMatrixLegend.For("MISSING", "CONNECTED", true).Key, "A channel without live capability outranks the mapping state.");
        Assert.AreEqual("ERROR", ChannelMatrixLegend.For("error", "CONNECTED", false).Key, "Case-insensitive.");
        Assert.AreEqual(ChannelMatrixLegend.None, ChannelMatrixLegend.For("WHATEVER", "CONNECTED", false).Key, "An unknown state is not invented into something else.");
        Assert.AreEqual(ChannelMatrixLegend.None, ChannelMatrixLegend.For(ChannelMatrixLegend.LocalOnly, "CONNECTED", false).Key, "The modifier keys are not reachable as mapping statuses.");

        var cell = new ChannelMatrixCell("STALE", "CONNECTED", "", "None", "");
        Assert.AreEqual(cell.Marker, ChannelMatrixLegend.Get("STALE").Glyph); Assert.AreEqual(cell.Word, ChannelMatrixLegend.Get("STALE").Word); Assert.AreEqual(cell.Level, ChannelMatrixLegend.Get("STALE").Level);
        Assert.AreEqual("⊘ yalnız yerel", new ChannelMatrixCell("DRAFT", "CONNECTED", "", "None", "", LocalOnly: true).Label);
    }

    [TestMethod]
    public void TheLegendCountsOnlyTheStatesOnScreenAndLocalOnlyComesFromTheCatalogueFact()
    {
        var rows = new List<ChannelListingMatrixRow>
        {
            Row("p1", "etsy", "S1", "SYNCED"), Row("p2", "etsy", "S1", "SYNCED"), Row("p3", "etsy", "S1", "ERROR"),
            Row("p1", "trendyol", "T1", "DRAFT", capabilities: ChannelListingMatrixService.LocalOnlyCapabilities), Row("p2", "trendyol", "T1", "MISSING", capabilities: ChannelListingMatrixService.LocalOnlyCapabilities),
            Row("p1", "ebay", "E1", "PENDING", auth: "AUTH_ERROR"),
        };
        var matrix = ChannelMatrixPivot.Build(rows, null);
        var counts = ChannelMatrixLegend.Counts(matrix);
        CollectionAssert.AreEqual(new[] { "SYNCED", "ERROR", "AUTH_ERROR", "LOCAL_ONLY", "NONE" }, counts.Select(c => c.Entry.Key).ToArray(), "Legend order; only states present (no STALE, no PENDING as such -- eBay's cell is a connection problem).");
        Assert.AreEqual(2, counts.Single(c => c.Entry.Key == "SYNCED").Count); Assert.AreEqual(1, counts.Single(c => c.Entry.Key == "ERROR").Count);
        Assert.AreEqual(2, counts.Single(c => c.Entry.Key == "LOCAL_ONLY").Count, "Both Trendyol cells are local-only whatever their mapping state says.");
        Assert.AreEqual(3, counts.Single(c => c.Entry.Key == "NONE").Count, "p2/p3 on eBay and p3 on Trendyol have no record.");
        Assert.IsTrue(ChannelListingMatrixService.IsLocalOnly(ChannelListingMatrixService.LocalOnlyCapabilities)); Assert.IsFalse(ChannelListingMatrixService.IsLocalOnly("ProductsRead, OrdersRead"));
        var trendyol = matrix.Rows.Single(r => r.ProductId == "p1").Cells[matrix.Columns.ToList().FindIndex(c => c.Channel == "trendyol")];
        Assert.IsTrue(trendyol!.LocalOnly); StringAssert.Contains(trendyol.Legend.Description, "canlı ilan yeteneği yok");
        Assert.AreEqual(matrix.Rows[0].Labels.Count, matrix.Rows[0].Descriptions.Count, "A description per cell for the tooltip.");
    }
}
