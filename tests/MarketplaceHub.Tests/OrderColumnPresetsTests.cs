using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #834 (DESIGN: Order list column presets). Presets name only real columns and never PII; a preset resolves to
// visibility + order over the live grid, skipping columns the grid lost and hiding ones it gained; the custom
// layout is never overwritten by a preset, a user edit becomes the custom layout, and both survive a restart.
[TestClass]
public sealed class OrderColumnPresetsTests
{
    static readonly string[] Live = { "Marketplace", "ShopId", "OrderId", "RawStatus", "PaymentStatus", "StockDecisionLabel", "DeliveryLabel", "SlaLabel", "Carriers", "TrackingNumbers", "TotalLabel", "Source", "SyncLabel", "UrgencyLabel" };

    [TestMethod]
    public void PresetsUseOnlyLiveColumnsNeverPiiAndResolveToVisibilityAndOrder()
    {
        foreach (var preset in OrderColumnPresets.Presets)
        {
            Assert.IsTrue(preset.Columns.All(Live.Contains), preset.Key + " names a column the grid does not have.");
            Assert.IsFalse(preset.Columns.Any(OrderColumnPresets.IsPii), preset.Key + " carries PII.");
        }
        CollectionAssert.AreEqual(new[] { "custom", "operations", "shipping", "finance" }, OrderColumnPresets.Options.Select(o => o.Key).ToArray());

        var shipping = OrderColumnPresets.Resolve("shipping", Live, new[] { new DataGridColumnLayout("OrderId", 0, 140, true) });
        Assert.IsNotNull(shipping);
        Assert.AreEqual(Live.Length, shipping!.Columns.Count, "Every live column has a row: shown or hidden.");
        CollectionAssert.AreEqual(new[] { "OrderId", "ShopId", "DeliveryLabel", "SlaLabel", "Carriers", "TrackingNumbers", "SyncLabel" }, shipping.Columns.Where(c => c.Visible).OrderBy(c => c.DisplayIndex).Select(c => c.Key).ToArray(), "Preset order, preset columns visible.");
        Assert.IsTrue(shipping.Columns.Where(c => !c.Visible).All(c => c.DisplayIndex >= 7), "Hidden columns follow the visible ones.");
        Assert.AreEqual(140, shipping.Columns.Single(c => c.Key == "OrderId").Width, "A width the grid has is kept; a preset never resizes."); Assert.AreEqual(0, shipping.Columns.Single(c => c.Key == "ShopId").Width);
        Assert.IsNull(OrderColumnPresets.Resolve("nope", Live));

        // Schema change: the grid lost a column the preset names and gained one it does not know.
        var changed = Live.Where(k => k != "Carriers").Append("CustomerName").Append("NewColumn").ToList();
        var resolved = OrderColumnPresets.Resolve("shipping", changed)!;
        Assert.IsFalse(resolved.Columns.Any(c => c.Key == "Carriers"), "A column the grid no longer has is skipped, not invented.");
        Assert.IsFalse(resolved.Columns.Single(c => c.Key == "NewColumn").Visible, "A column the preset does not know stays hidden under the preset.");
        Assert.IsFalse(resolved.Columns.Single(c => c.Key == "CustomerName").Visible, "A PII column is collapsed under every preset.");
        var pii = OrderColumnPresets.Presets.Select(p => OrderColumnPresets.Resolve(p.Key, changed)!).ToList();
        Assert.IsTrue(pii.All(s => !s.Columns.Single(c => c.Key == "CustomerName").Visible));
    }

    [TestMethod]
    public void APresetNeverOverwritesTheCustomLayoutAndAUserEditBecomesIt()
    {
        var prefs = new Dictionary<string, string>(StringComparer.Ordinal);
        var controller = new OrderColumnPresetController(k => prefs.TryGetValue(k, out var v) ? v : null, (k, v) => prefs[k] = v);
        Assert.AreEqual("custom", controller.Current, "Nothing remembered: custom, with nothing to apply."); Assert.IsNull(controller.Initial(Live));

        var mine = new DataGridLayoutState(DataGridLayoutCodec.CurrentVersion, Live.Select((k, i) => new DataGridColumnLayout(k, Live.Length - 1 - i, 77, k != "Source")).ToList(), "OrderId", true);
        controller.UserEdited(mine);
        Assert.AreEqual("custom", controller.Current);
        var savedCustom = prefs[OrderColumnPresets.CustomLayoutPreferenceKey];

        var applied = controller.Select("operations", Live);
        Assert.IsNotNull(applied); Assert.AreEqual("operations", controller.Current); Assert.AreEqual("operations", prefs[OrderColumnPresets.PresetPreferenceKey]);
        Assert.AreEqual(savedCustom, prefs[OrderColumnPresets.CustomLayoutPreferenceKey], "Choosing a preset leaves the custom layout exactly as it was.");
        Assert.IsTrue(applied!.Columns.Single(c => c.Key == "Source").Visible == false && applied.Columns.Single(c => c.Key == "RawStatus").Visible);

        var back = controller.Select("custom", Live);
        Assert.IsNotNull(back); Assert.IsFalse(back!.Columns.Single(c => c.Key == "Source").Visible, "Back to custom: the user's own layout, Source still hidden.");
        Assert.AreEqual("OrderId", back.SortBy); Assert.IsTrue(back.SortDescending);

        controller.Select("finance", Live);
        var edited = mine with { Columns = mine.Columns.Select(c => c.Key == "TotalLabel" ? c with { Width = 200 } : c).ToList() };
        controller.UserEdited(edited);
        Assert.AreEqual("custom", controller.Current, "A change the user makes under a preset is theirs: the grid is custom now.");
        Assert.AreEqual(200, controller.CustomLayout!.Columns.Single(c => c.Key == "TotalLabel").Width);

        Assert.ThrowsException<ArgumentException>(() => controller.Select("nope", Live));

        // Restart: a new controller over the same store remembers the preset and keeps the custom layout.
        controller.Select("shipping", Live);
        var restarted = new OrderColumnPresetController(k => prefs.TryGetValue(k, out var v) ? v : null, (k, v) => prefs[k] = v);
        Assert.AreEqual("shipping", restarted.Current);
        Assert.IsTrue(restarted.Initial(Live)!.Columns.Single(c => c.Key == "TrackingNumbers").Visible);
        Assert.AreEqual(200, restarted.CustomLayout!.Columns.Single(c => c.Key == "TotalLabel").Width, "The custom layout survived the restart untouched.");
        prefs[OrderColumnPresets.PresetPreferenceKey] = "garbage";
        Assert.AreEqual("custom", new OrderColumnPresetController(k => prefs.TryGetValue(k, out var v) ? v : null, (k, v) => prefs[k] = v).Current, "An unknown remembered key falls back to custom.");
    }
}
