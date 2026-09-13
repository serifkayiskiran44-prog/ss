using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #842 (DESIGN: Channel matrix sticky axes). The flat rows pivot to product rows × store columns in a fixed
// order; a store the shell does not offer has no column and no cells; a 1000 × 20 matrix pivots whole; cells
// read without colour.
[TestClass]
public sealed class ChannelMatrixPivotTests
{
    static ChannelListingMatrixRow Row(string productId, string name, string channel, string channelName, string shop, string mapping, string auth = "CONNECTED") =>
        new(productId, "SKU-" + productId, name, channel, channelName, shop, mapping, mapping == "MISSING" ? "" : "L-" + productId, "None", "", null, auth, "ProductsRead");

    [TestMethod]
    public void RowsAndColumnsPivotInAFixedOrderAndAnUnofferedStoreHasNoColumn()
    {
        var rows = new[]
        {
            Row("p2", "Tabak", "etsy", "Etsy", "S1", "SYNCED"), Row("p1", "Kupa", "etsy", "Etsy", "S1", "MISSING"),
            Row("p1", "Kupa", "ebay", "eBay", "E9", "ERROR"), Row("p2", "Tabak", "ebay", "eBay", "E9", "DRAFT", "AUTH_ERROR"),
            Row("p1", "Kupa", "trendyol", "Trendyol", "T1", "STALE"),
        };
        var all = ChannelMatrixPivot.Build(rows, null);
        CollectionAssert.AreEqual(new[] { "ebay|E9", "etsy|S1", "trendyol|T1" }, all.Columns.Select(c => c.Key).ToArray(), "Columns by channel name, then shop.");
        CollectionAssert.AreEqual(new[] { "p1", "p2" }, all.Rows.Select(r => r.ProductId).ToArray(), "Rows by product name.");
        Assert.AreEqual(6, all.CellCount);
        var kupa = all.Rows[0];
        CollectionAssert.AreEqual(new[] { "✖ hata", "○ eşleme yok", "◔ bayat" }, kupa.Labels.ToArray());
        Assert.AreEqual(3, kupa.Problems);
        var tabak = all.Rows[1];
        Assert.AreEqual("⚠ bağlantı", tabak.Labels[0], "A connection problem outranks the mapping state."); Assert.AreEqual("✔ yayında", tabak.Labels[1]); Assert.AreEqual("· kayıt yok", tabak.Labels[2], "No entry for that store: an empty cell, not a missing column.");
        Assert.IsNull(tabak.Cells[2]);
        Assert.AreEqual("Etsy\nS1", all.Columns[1].Header);

        var offered = ChannelMatrixPivot.Build(rows, new[] { "etsy|S1", "trendyol|T1" });
        CollectionAssert.AreEqual(new[] { "etsy|S1", "trendyol|T1" }, offered.Columns.Select(c => c.Key).ToArray(), "eBay is not offered: no column.");
        Assert.IsFalse(offered.Rows.SelectMany(r => r.Cells).Any(c => c is { MappingStatus: "ERROR" }), "…and none of its cells survive anywhere.");
        Assert.AreEqual(2, offered.Rows[0].Cells.Count);
        Assert.AreEqual(0, ChannelMatrixPivot.Build(rows, Array.Empty<string>()).Columns.Count, "Nothing offered: nothing shown.");
    }

    [TestMethod]
    public void AThousandByTwentyMatrixPivotsWholeAndCellsReadWithoutColour()
    {
        var statuses = new[] { "SYNCED", "MISSING", "ERROR", "STALE", "PENDING", "DRAFT" };
        var rows = new List<ChannelListingMatrixRow>(20000);
        for (var p = 0; p < 1000; p++) for (var s = 0; s < 20; s++) rows.Add(Row($"p{p:D4}", $"Ürün {p:D4}", s % 2 == 0 ? "etsy" : "ebay", s % 2 == 0 ? "Etsy" : "eBay", $"S{s:D2}", statuses[(p + s) % statuses.Length]));
        var matrix = ChannelMatrixPivot.Build(rows, null);
        Assert.AreEqual(20, matrix.Columns.Count); Assert.AreEqual(1000, matrix.Rows.Count); Assert.AreEqual(20000, matrix.CellCount);
        Assert.IsTrue(matrix.Rows.All(r => r.Cells.Count == 20 && r.Cells.All(c => c is not null)));
        Assert.AreEqual("p0000", matrix.Rows[0].ProductId); Assert.AreEqual("p0999", matrix.Rows[^1].ProductId);
        var markers = statuses.Select(s => new ChannelMatrixCell(s, "CONNECTED", "", "None", "").Marker).ToList();
        Assert.AreEqual(statuses.Length, markers.Distinct().Count(), "Every state has its own marker.");
        Assert.AreEqual(SeverityLevel.Blocking, new ChannelMatrixCell("SYNCED", "AUTH_ERROR", "", "None", "").Level);
        Assert.AreEqual(SeverityLevel.Success, new ChannelMatrixCell("SYNCED", "CONNECTED", "", "None", "").Level);
    }
}
