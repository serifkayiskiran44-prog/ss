using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #853 (DESIGN: Settings navigation taxonomy). Seven categories over the settings that exist; every entry points at a
// registered route or is hosted inline; keys are unique; secret-bearing pages are exactly the credential pages and
// are never inline; a shell that lacks a route drops the entry (and an emptied category); deep links parse both
// ways; search spans category, label and description; long labels trim with an ellipsis.
[TestClass]
public sealed class SettingsTaxonomyTests
{
    [TestMethod]
    public void TheTaxonomyCoversTheRealSettingsWithUniqueKeysAndMarksSecretPages()
    {
        CollectionAssert.AreEqual(new[] { "general", "store", "connections", "import", "pricing", "notifications", "diagnostics" }, SettingsTaxonomy.Categories.Select(c => c.Key).ToArray());
        var entries = SettingsTaxonomy.Categories.SelectMany(c => c.Entries).ToList();
        Assert.AreEqual(entries.Count, entries.Select(e => e.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count(), "Entry keys are unique.");
        Assert.IsTrue(entries.All(e => e.Label.Length > 0 && e.Description.Length > 0 && e.Route.Length > 0));
        var secret = entries.Where(e => e.SecretBearing).Select(e => e.Route).OrderBy(r => r).ToArray();
        CollectionAssert.AreEqual(new[] { "allegro", "amazon", "ebay", "etsy", "fruugo", "hepsiburada", "joom", "ozon", "shipping", "trendyol", "wish" }, secret, "Exactly the credential pages carry the mark.");
        Assert.IsTrue(entries.Where(e => e.SecretBearing).All(e => !e.Inline), "A secret-bearing page is linked, never hosted inline.");
        Assert.IsTrue(entries.Where(e => e.Inline).All(e => e.Route == "settings"), "Inline entries belong to the settings shell itself.");
        Assert.IsTrue(entries.Where(e => e.SecretBearing && e.Route != "shipping").All(e => e.Section == "connection"), "A channel's credentials live on its connection tab.");

        var found = SettingsTaxonomy.FindEntry("price-policies"); Assert.IsNotNull(found); Assert.AreEqual("pricing", found!.Value.Category.Key);
        Assert.IsNull(SettingsTaxonomy.FindEntry("ghost")); Assert.AreEqual("store", SettingsTaxonomy.Find(" STORE ")!.Key); Assert.IsNull(SettingsTaxonomy.Find(""));
    }

    [TestMethod]
    public void AShellWithoutARouteDropsTheEntryDeepLinksParseAndSearchSpansLabels()
    {
        var full = SettingsTaxonomy.Visible(_ => true); Assert.AreEqual(7, full.Count);
        var noConnections = SettingsTaxonomy.Visible(route => route is not ("etsy" or "ebay" or "ozon" or "joom" or "amazon" or "trendyol" or "hepsiburada" or "fruugo" or "allegro" or "wish" or "channels" or "shipping" or "connections" or "api-health"));
        Assert.AreEqual(6, noConnections.Count); Assert.IsFalse(noConnections.Any(c => c.Key == "connections"), "An emptied category disappears.");
        var noReports = SettingsTaxonomy.Visible(route => route != "reports");
        Assert.AreEqual(2, noReports.Single(c => c.Key == "diagnostics").Entries.Count, "A missing route drops its entry, the category stays.");
        Assert.AreEqual(3, SettingsTaxonomy.Visible(_ => false).Single(c => c.Key == "general").Entries.Count(e => e.Inline) + SettingsTaxonomy.Visible(_ => false).Single(c => c.Key == "import").Entries.Count, "Inline entries survive a shell that registers nothing.");

        Assert.AreEqual("pricing", SettingsTaxonomy.ParseDeepLink("settings/pricing")); Assert.AreEqual("store", SettingsTaxonomy.ParseDeepLink("Settings/STORE/")); Assert.IsNull(SettingsTaxonomy.ParseDeepLink("settings")); Assert.IsNull(SettingsTaxonomy.ParseDeepLink("settings/ghost")); Assert.IsNull(SettingsTaxonomy.ParseDeepLink("products"));
        Assert.AreEqual("settings/notifications", SettingsTaxonomy.DeepLink(" Notifications "));
        Assert.AreEqual("settings/pricing", SettingsTaxonomy.DeepLink(SettingsTaxonomy.ParseDeepLink(SettingsTaxonomy.DeepLink("pricing"))!), "Round trip.");

        var hits = SettingsTaxonomy.Search("döviz");
        Assert.IsTrue(hits.Any(h => h.Entry.Key == "pricing-locale" && h.Category.Key == "pricing") && hits.Any(h => h.Entry.Key == "locale" && h.Category.Key == "general"), "Search matches labels and descriptions across categories.");
        Assert.AreEqual(1, SettingsTaxonomy.Search("KDV").Count, "Case-insensitive on the description.");
        Assert.AreEqual(0, SettingsTaxonomy.Search("   ").Count); Assert.AreEqual(0, SettingsTaxonomy.Search("etsy", route => route != "etsy").Count, "Search honours the shell's routes.");
        Assert.AreEqual("Kargo bağlantısı (Navlungo)", SettingsTaxonomy.ShortLabel("Kargo bağlantısı (Navlungo)"));
        var trimmed = SettingsTaxonomy.ShortLabel(new string('A', 80)); Assert.AreEqual(SettingsTaxonomy.MaxLabelLength, trimmed.Length); StringAssert.EndsWith(trimmed, "…");
    }
}
