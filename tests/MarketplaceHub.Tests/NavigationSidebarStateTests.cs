using System;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #812 (DESIGN: Navigation sidebar collapse persistence). Whether the sidebar is collapsed, and how wide it was
// when open, are the operator's choices and survive a restart; in icons-only mode every item keeps its full name
// for the tooltip and the accessibility tree, and widths are DIPs so a DPI change does not move the split.
[TestClass]
public sealed class NavigationSidebarStateTests
{
    [TestMethod]
    public void TheCollapsedStateAndTheOpenWidthSurviveARestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "sidebar-" + Guid.NewGuid().ToString("N"));
        try
        {
            var chosen = NavigationSidebar.Toggle(NavigationSidebar.Resize(NavigationSidebar.Default, 260));
            Assert.IsTrue(chosen.Collapsed);
            Assert.AreEqual(260, chosen.ExpandedWidth);

            new UiPreferenceStore(root).Set(NavigationSidebar.PreferenceKey, NavigationSidebar.Serialize(chosen));
            SqliteConnection.ClearAllPools();

            var restored = NavigationSidebar.Parse(new UiPreferenceStore(root).Get(NavigationSidebar.PreferenceKey));
            Assert.IsTrue(restored.Collapsed, "Collapsed is a choice, not a session accident.");
            Assert.AreEqual(260, restored.ExpandedWidth, "Expanding again must return to the width the operator had, not the default.");
            Assert.AreEqual(NavigationSidebar.CollapsedWidth, NavigationSidebar.CurrentWidth(restored));
            Assert.AreEqual(260, NavigationSidebar.CurrentWidth(NavigationSidebar.Toggle(restored)));
        }
        finally { SqliteConnection.ClearAllPools(); try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [TestMethod]
    public void NothingSavedOrGarbageSavedFallsBackToTheDefaultOpenSidebar()
    {
        foreach (var saved in new[] { null, "", "   ", "{", "collapsed=maybe", "{\"Collapsed\":true,\"ExpandedWidth\":\"wide\"}", "{\"Collapsed\":true,\"ExpandedWidth\":-40}", "{\"Collapsed\":false,\"ExpandedWidth\":1e308}" })
        {
            var state = NavigationSidebar.Parse(saved);
            Assert.IsTrue(state.ExpandedWidth >= NavigationSidebar.MinExpandedWidth && state.ExpandedWidth <= NavigationSidebar.MaxExpandedWidth, $"'{saved}' must never produce an unusable width: {state.ExpandedWidth}");
            Assert.IsFalse(double.IsNaN(state.ExpandedWidth) || double.IsInfinity(state.ExpandedWidth));
        }
        Assert.IsFalse(NavigationSidebar.Parse(null).Collapsed, "A fresh install opens with the full sidebar.");
        Assert.AreEqual(NavigationSidebar.Default.ExpandedWidth, NavigationSidebar.Parse("{\"Collapsed\":true,\"ExpandedWidth\":-40}").ExpandedWidth, "A nonsensical width is replaced, not clamped into something the operator never chose.");
    }

    [TestMethod]
    public void ResizingIsClampedAndIgnoredWhileCollapsed()
    {
        Assert.AreEqual(NavigationSidebar.MinExpandedWidth, NavigationSidebar.Resize(NavigationSidebar.Default, 10).ExpandedWidth, "Too narrow to read a label is not a sidebar.");
        Assert.AreEqual(NavigationSidebar.MaxExpandedWidth, NavigationSidebar.Resize(NavigationSidebar.Default, 5000).ExpandedWidth, "The sidebar cannot swallow the workspace.");
        Assert.AreEqual(240, NavigationSidebar.Resize(NavigationSidebar.Default, 240).ExpandedWidth);

        var collapsed = NavigationSidebar.Toggle(NavigationSidebar.Default);
        var resized = NavigationSidebar.Resize(collapsed, 300);
        Assert.IsTrue(resized.Collapsed);
        Assert.AreEqual(NavigationSidebar.Default.ExpandedWidth, resized.ExpandedWidth, "A drag while collapsed does not silently rewrite the open width.");
    }

    [TestMethod]
    public void WidthsAreDipsSoADpiChangeDoesNotMoveTheSplit()
    {
        var state = NavigationSidebar.Resize(NavigationSidebar.Default, 233.5);
        var roundTrip = NavigationSidebar.Parse(NavigationSidebar.Serialize(state));
        Assert.AreEqual(233.5, roundTrip.ExpandedWidth, "The stored value is the layout value; there is no pixel conversion to drift through at 125% or 200%.");
        Assert.IsFalse(NavigationSidebar.Serialize(state).Contains("px", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void IconsOnlyModeKeepsTheFullNameForTheTooltipAndTheAccessibilityTree()
    {
        var open = NavigationSidebar.Present("Ürün yönetimi", "Ortak ürün havuzu", collapsed: false);
        Assert.AreEqual("Ürün yönetimi", open.Content);
        Assert.AreEqual("Ortak ürün havuzu", open.ToolTip);
        Assert.AreEqual("Ürün yönetimi", open.AccessibleName);

        var icons = NavigationSidebar.Present("Ürün yönetimi", "Ortak ürün havuzu", collapsed: true);
        Assert.AreNotEqual("Ürün yönetimi", icons.Content, "Icons-only shows a glyph, not the label squeezed into 56 DIPs.");
        Assert.IsTrue(icons.Content.Length is >= 1 and <= 2, $"A glyph is one or two characters: '{icons.Content}'");
        Assert.AreEqual("ÜY", icons.Content, "The glyph is built with Turkish casing (ü → Ü), not invariant casing.");
        Assert.AreEqual("Ürün yönetimi", icons.ToolTip, "With the label gone, the tooltip is the label.");
        Assert.AreEqual("Ürün yönetimi", icons.AccessibleName, "A screen reader hears the same name in both modes.");

        Assert.AreEqual("İK", NavigationSidebar.Present("İlk kurulum", "", collapsed: true).Content);
        Assert.AreEqual("E", NavigationSidebar.Present("eBay", "", collapsed: true).Content, "A one-word title is one letter, not a fake second one.");
        Assert.AreEqual("?", NavigationSidebar.Present("   ", "", collapsed: true).Content);
    }
}
