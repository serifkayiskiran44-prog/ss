using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #792: the pure part of column layout persistence -- format, trust rules and the order fallback -- with no WPF.
[TestClass]
public sealed class DataGridLayoutCodecTests
{
    static DataGridColumnLayout Col(string key, int index, double width = 100, bool visible = true) => new(key, index, width, visible);

    [TestMethod]
    public void ALayoutRoundTripsWithDeviceIndependentWidthsUnchanged()
    {
        var state = new DataGridLayoutState(DataGridLayoutCodec.CurrentVersion, [Col("Sku", 1, 135.5), Col("Name", 0, 200, visible: false)], "Price", true);
        var back = DataGridLayoutCodec.Deserialize(DataGridLayoutCodec.Serialize(state))!;
        Assert.AreEqual(2, back.Columns.Count);
        Assert.AreEqual(135.5, back.Columns[0].Width, "Widths are DIPs and must come back exactly; DPI scaling is the renderer's job, not the layout's.");
        Assert.IsFalse(back.Columns[1].Visible);
        Assert.AreEqual("Price", back.SortBy); Assert.IsTrue(back.SortDescending);
    }

    [TestMethod]
    public void AnythingUntrustworthyReadsBackAsNullSoTheDefaultLayoutWins()
    {
        Assert.IsNull(DataGridLayoutCodec.Deserialize(null));
        Assert.IsNull(DataGridLayoutCodec.Deserialize("   "));
        Assert.IsNull(DataGridLayoutCodec.Deserialize("{not json"));
        Assert.IsNull(DataGridLayoutCodec.Deserialize("""{"Version":2,"Columns":[],"SortBy":"","SortDescending":false}"""), "A layout from another schema version is not guessed at.");
        Assert.IsNull(DataGridLayoutCodec.Deserialize("""{"Version":1,"SortBy":"","SortDescending":false}"""), "No columns, no layout.");
        var cleaned = DataGridLayoutCodec.Deserialize("""{"Version":1,"Columns":[{"Key":"","DisplayIndex":0,"Width":10,"Visible":true},{"Key":"Sku","DisplayIndex":0,"Width":-5,"Visible":true},{"Key":"Name","DisplayIndex":1,"Width":120,"Visible":true}],"SortBy":null,"SortDescending":false}""")!;
        Assert.AreEqual(1, cleaned.Columns.Count, "Entries without a key or with an impossible width are dropped individually, not the whole layout.");
        Assert.AreEqual("Name", cleaned.Columns[0].Key);
        Assert.AreEqual("", cleaned.SortBy);
    }

    [TestMethod]
    public void OrderResolutionSkipsRemovedColumnsKeepsNewOnesInPlaceAndSettlesCollisions()
    {
        string[] live = ["Status", "Sku", "Name", "Price", "Stock"];

        var reordered = DataGridLayoutCodec.ResolveOrder(live, [Col("Name", 0), Col("Ghost", 1), Col("Sku", 2), Col("Status", 3)]);
        CollectionAssert.AreEqual(new[] { "Name", "Sku", "Status", "Price", "Stock" }, reordered.ToArray(), "Ghost is skipped; Price and Stock (unknown to the saved layout) stay at their default positions.");

        var newColumnInMiddle = DataGridLayoutCodec.ResolveOrder(live, [Col("Stock", 0), Col("Price", 1), Col("Sku", 2), Col("Status", 3)]);
        CollectionAssert.AreEqual(new[] { "Stock", "Price", "Name", "Sku", "Status" }, newColumnInMiddle.ToArray(), "A column the saved layout never saw is inserted at its default index (2), not appended.");

        var collision = DataGridLayoutCodec.ResolveOrder(live, [Col("Price", 0), Col("Sku", 0)]);
        CollectionAssert.AreEqual(new[] { "Status", "Sku", "Name", "Price", "Stock" }, collision.ToArray(), "Two saved columns claiming the same index keep their default relative order (Sku before Price) and the unknown ones take their default positions, which here degrades cleanly to the default order.");

        CollectionAssert.AreEqual(live, DataGridLayoutCodec.ResolveOrder(live, []).ToArray(), "No saved columns: the default order, untouched.");
    }
}
