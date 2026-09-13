using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #849 (DESIGN: Report result column chooser). The schema's classified column starts hidden and stays hidden while
// the policy forbids it; order, visibility and DIP widths round-trip through the shared codec; a persisted layout
// that names a column the schema lost is dropped and a column the schema gained appears at its default place; the
// last visible column cannot be hidden; a hundred columns resolve, persist, group and search in milliseconds.
[TestClass]
public sealed class ReportColumnsTests
{
    [TestMethod]
    public void TheClassifiedColumnFollowsThePolicyAndDefaultsAreSafe()
    {
        var schema = ReportColumns.OrdersSchema;
        Assert.AreEqual(7, schema.Count); Assert.AreEqual(1, schema.Count(c => c.Classified)); Assert.AreEqual("Tracking", schema.Single(c => c.Classified).Key);
        Assert.IsTrue(schema.All(c => ReportTemplateRenderer.IsAllowed(c.Key)), "Every schema column is one the renderer allows.");
        Assert.AreEqual(0, ReportColumns.SchemaFor(ReportCatalog.Find("orders")!).Count, "Only the report the workspace runs has result columns.");

        var defaults = ReportColumns.Default(schema);
        CollectionAssert.AreEqual(new[] { "OrderId", "ShopId", "Status", "Price", "Currency", "UpdatedUtc" }, defaults.VisibleKeys.ToArray(), "Classified hidden by default.");
        Assert.AreEqual(1, defaults.HiddenClassified); StringAssert.Contains(ReportColumns.Summary(defaults, false), "6 / 7 kolon görünür"); StringAssert.Contains(ReportColumns.Summary(defaults, false), ReportColumns.PolicyClosedWord); StringAssert.Contains(ReportColumns.Summary(defaults, true), "sınıflandırılmış kolon gizli");

        var shown = ReportColumns.Toggle(defaults, "Tracking", true, classifiedAllowed: false);
        Assert.IsFalse(shown.Columns.Single(c => c.Column.Key == "Tracking").Visible, "The policy is off: the classified column cannot be shown.");
        shown = ReportColumns.Toggle(defaults, "Tracking", true, classifiedAllowed: true);
        Assert.IsTrue(shown.Columns.Single(c => c.Column.Key == "Tracking").Visible);
        var persistedWithTracking = ReportColumns.Persist(shown);
        Assert.IsFalse(ReportColumns.Resolve(schema, persistedWithTracking, false).VisibleKeys.Contains("Tracking"), "A saved layout cannot outrank a policy that is off now.");
        Assert.IsTrue(ReportColumns.Resolve(schema, persistedWithTracking, true).VisibleKeys.Contains("Tracking"));

        var one = new ReportColumnLayout(new[] { new ReportColumnChoice(schema[0], true, 100) });
        Assert.IsTrue(ReportColumns.Toggle(one, "OrderId", false, true).Columns[0].Visible, "The last visible column stays.");
        Assert.AreSame(defaults, ReportColumns.Toggle(defaults, "Ghost", false, true)); Assert.AreSame(defaults, ReportColumns.Move(defaults, "OrderId", 0));
    }

    [TestMethod]
    public void OrderVisibilityAndWidthsRoundTripAndSurviveASchemaChange()
    {
        var schema = ReportColumns.OrdersSchema;
        var arranged = ReportColumns.Move(ReportColumns.Default(schema), "Price", -3);
        arranged = ReportColumns.Toggle(arranged, "ShopId", false, false);
        arranged = ReportColumns.Resize(arranged, "OrderId", 200);
        CollectionAssert.AreEqual(new[] { "Price", "OrderId", "ShopId", "Status", "Currency", "UpdatedUtc", "Tracking" }, arranged.Columns.Select(c => c.Column.Key).ToArray());
        var json = ReportColumns.Persist(arranged);
        var back = ReportColumns.Resolve(schema, json, false);
        CollectionAssert.AreEqual(arranged.Columns.Select(c => c.Column.Key).ToArray(), back.Columns.Select(c => c.Column.Key).ToArray());
        CollectionAssert.AreEqual(new[] { "Price", "OrderId", "Status", "Currency", "UpdatedUtc" }, back.VisibleKeys.ToArray());
        Assert.AreEqual(200, back.Columns.Single(c => c.Column.Key == "OrderId").Width);
        Assert.AreEqual(ReportColumns.MaxWidth, ReportColumns.Resize(arranged, "OrderId", 5000).Columns.Single(c => c.Column.Key == "OrderId").Width, "Widths clamp.");
        Assert.AreSame(arranged, ReportColumns.Resize(arranged, "OrderId", double.NaN));

        // Schema change: a saved "Ghost" is dropped, the never-saved "Currency" appears at its default place with its default width.
        var stale = DataGridLayoutCodec.Serialize(new DataGridLayoutState(DataGridLayoutCodec.CurrentVersion, new[] { new DataGridColumnLayout("Ghost", 0, 100, true), new DataGridColumnLayout("UpdatedUtc", 1, 3, true), new DataGridColumnLayout("OrderId", 2, 150, false) }, "", false));
        var resolved = ReportColumns.Resolve(schema, stale, false);
        Assert.IsFalse(resolved.Columns.Any(c => c.Column.Key == "Ghost")); Assert.AreEqual(7, resolved.Columns.Count);
        Assert.AreEqual("UpdatedUtc", resolved.Columns[0].Column.Key); Assert.AreEqual(150, resolved.Columns.Single(c => c.Column.Key == "UpdatedUtc").Width, "A width below the minimum falls back to the column's default.");
        Assert.IsFalse(resolved.Columns.Single(c => c.Column.Key == "OrderId").Visible); Assert.IsTrue(resolved.Columns.Single(c => c.Column.Key == "Currency").Visible);
        Assert.AreEqual("Currency", resolved.Columns[4].Column.Key, "A column the saved layout never knew keeps its default position.");

        var garbage = ReportColumns.Resolve(schema, "{not json", true);
        CollectionAssert.AreEqual(ReportColumns.Default(schema).VisibleKeys.ToArray(), garbage.VisibleKeys.ToArray());
        var allHidden = DataGridLayoutCodec.Serialize(new DataGridLayoutState(DataGridLayoutCodec.CurrentVersion, schema.Select((c, i) => new DataGridColumnLayout(c.Key, i, 100, false)).ToList(), "", false));
        CollectionAssert.AreEqual(new[] { "OrderId" }, ReportColumns.Resolve(schema, allHidden, false).VisibleKeys.ToArray(), "A layout hiding everything still shows the first column.");

        var reordered = ReportColumns.Reorder(arranged, new[] { "UpdatedUtc", "Price", "OrderId", "Status", "Currency" });
        CollectionAssert.AreEqual(new[] { "UpdatedUtc", "Price", "ShopId", "OrderId", "Status", "Currency", "Tracking" }, reordered.Columns.Select(c => c.Column.Key).ToArray(), "Visible columns take the grid's order; hidden ones stay where they were.");
        Assert.AreSame(arranged, ReportColumns.Reorder(arranged, new[] { "Price" }), "A partial order is refused.");
    }

    [TestMethod]
    public void AHundredColumnsResolvePersistGroupAndSearchInMilliseconds()
    {
        var schema = Enumerable.Range(0, 100).Select(i => new ReportColumn($"col-{i:D3}", $"Kolon {i:D3}", $"Grup {i / 10}", Classified: i % 25 == 0, Width: 80 + i)).ToList();
        var clock = Stopwatch.StartNew();
        var layout = ReportColumns.Default(schema);
        layout = ReportColumns.Move(layout, "col-099", -99); layout = ReportColumns.Toggle(layout, "col-050", false, false);
        var json = ReportColumns.Persist(layout); var back = ReportColumns.Resolve(schema, json, false);
        var groups = ReportColumns.Grouped(back, null); var found = ReportColumns.Grouped(back, "kolon 07");
        clock.Stop();
        Assert.AreEqual(100, back.Columns.Count); Assert.AreEqual("col-099", back.Columns[0].Column.Key); Assert.AreEqual(96, back.Visible.Count, "Four classified hidden, one hidden by hand, the rest shown.");
        Assert.AreEqual(10, groups.Count); Assert.AreEqual("Grup 9", groups[0].Group, "Groups follow the first appearance in the layout."); Assert.AreEqual(100, groups.Sum(g => g.Items.Count));
        Assert.AreEqual(10, found.Sum(g => g.Items.Count)); Assert.IsTrue(found.All(g => g.Items.All(i => i.Column.Label.StartsWith("Kolon 07", StringComparison.Ordinal))));
        Assert.IsTrue(clock.ElapsedMilliseconds < 500, $"{clock.ElapsedMilliseconds} ms");
        Assert.AreEqual("report-columns:orders-csv", ReportColumns.PreferenceKey(" Orders-CSV "));
    }
}
