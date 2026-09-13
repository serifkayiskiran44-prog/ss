using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #844 (DESIGN: Channel matrix readiness filters). Legend states as "any of" row filters; counts from the
// unfiltered matrix; zero result named; unknown keys dropped; toggling is pure; 100 000 cells filter fast.
[TestClass]
public sealed class ChannelMatrixReadinessFilterTests
{
    static ChannelListingMatrixRow Row(string productId, string channel, string shop, string mapping, string auth = "CONNECTED", string capabilities = "ProductsRead") =>
        new(productId, "SKU-" + productId, "Ürün " + productId, channel, channel.ToUpperInvariant(), shop, mapping, "", "None", "", null, auth, capabilities);

    [TestMethod]
    public void FiltersCombineAsAnyOfCountsStayWholeAndAZeroResultIsNamed()
    {
        var matrix = ChannelMatrixPivot.Build(new[]
        {
            Row("p1", "etsy", "S1", "SYNCED"), Row("p1", "ebay", "E1", "MISSING"),
            Row("p2", "etsy", "S1", "ERROR"), Row("p2", "ebay", "E1", "SYNCED"),
            Row("p3", "etsy", "S1", "STALE"), Row("p3", "ebay", "E1", "DRAFT", auth: "AUTH_ERROR"),
            Row("p4", "etsy", "S1", "SYNCED"), Row("p4", "ebay", "E1", "SYNCED"),
        }, null);

        var all = ChannelMatrixReadinessFilter.Apply(matrix, null);
        Assert.AreEqual(4, all.Matrix.Rows.Count); Assert.AreEqual(0, all.HiddenRows); Assert.IsFalse(all.IsEmpty);
        Assert.AreEqual(4, all.Counts.Single(c => c.Entry.Key == "SYNCED").Count, "p1/etsy, p2/ebay, p4/etsy, p4/ebay.");

        var errors = ChannelMatrixReadinessFilter.Apply(matrix, new[] { "ERROR" });
        CollectionAssert.AreEqual(new[] { "p2" }, errors.Matrix.Rows.Select(r => r.ProductId).ToArray()); Assert.AreEqual(3, errors.HiddenRows);
        Assert.AreEqual(4, errors.Counts.Single(c => c.Entry.Key == "SYNCED").Count, "Counts describe the whole matrix, not the filtered one.");
        Assert.AreEqual(2, errors.Matrix.Columns.Count, "Columns are never dropped by a state filter.");

        var combined = ChannelMatrixReadinessFilter.Apply(matrix, new[] { "error", "AUTH_ERROR", "missing" });
        CollectionAssert.AreEqual(new[] { "p1", "p2", "p3" }, combined.Matrix.Rows.Select(r => r.ProductId).ToArray(), "Any of the active states keeps a row; keys are case-insensitive.");

        var none = ChannelMatrixReadinessFilter.Apply(matrix, new[] { "PENDING" });
        Assert.IsTrue(none.IsEmpty); Assert.AreEqual(4, none.HiddenRows); StringAssert.Contains(none.EmptyText, "bekliyor"); StringAssert.Contains(none.EmptyText, "filtreyi kaldırın");

        var unknown = ChannelMatrixReadinessFilter.Apply(matrix, new[] { "READY", "SUPERSONIC" });
        Assert.AreEqual(4, unknown.Matrix.Rows.Count, "A key the legend does not know is dropped -- no invented state, no accidental empty screen."); Assert.AreEqual(0, unknown.Active.Count);
        Assert.AreEqual("Gösterilecek ürün yok.", ChannelMatrixReadinessFilter.Apply(new ChannelMatrix(Array.Empty<ChannelMatrixColumn>(), Array.Empty<ChannelMatrixRow>()), null).EmptyText);

        var set = ChannelMatrixReadinessFilter.Toggle(new HashSet<string>(), "error");
        CollectionAssert.AreEquivalent(new[] { "ERROR" }, set.ToArray());
        var back = ChannelMatrixReadinessFilter.Toggle(set, "ERROR");
        Assert.AreEqual(0, back.Count); Assert.AreEqual(1, set.Count, "Toggling returns a new set; the input is untouched.");
    }

    [TestMethod]
    public void AHundredThousandCellsFilterInMilliseconds()
    {
        var statuses = new[] { "SYNCED", "MISSING", "ERROR", "STALE", "PENDING", "DRAFT" };
        var rows = new List<ChannelListingMatrixRow>(100_000);
        for (var p = 0; p < 1000; p++) for (var s = 0; s < 100; s++) rows.Add(Row($"p{p:D4}", "etsy", $"S{s:D3}", statuses[(p * 7 + s) % statuses.Length]));
        var matrix = ChannelMatrixPivot.Build(rows, null);
        Assert.AreEqual(100_000, matrix.CellCount);
        var clock = Stopwatch.StartNew();
        var result = ChannelMatrixReadinessFilter.Apply(matrix, new[] { "ERROR", "STALE" });
        clock.Stop();
        Assert.IsTrue(result.Matrix.Rows.Count > 0 && result.Matrix.Rows.Count <= 1000);
        Assert.AreEqual(100_000, result.Counts.Sum(c => c.Count), "Every cell is counted once.");
        Assert.IsTrue(clock.ElapsedMilliseconds < 1500, $"100k cells filtered in {clock.ElapsedMilliseconds} ms.");
    }
}
