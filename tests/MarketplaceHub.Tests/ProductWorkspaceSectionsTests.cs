using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #801 (DESIGN: Product workspace section navigation). One catalogue of sections behind the workspace, so a
// deep link, the keyboard and the restored-focus path all name sections the same way. A link to a section that
// no longer exists must land somewhere sensible instead of on a blank pane.
[TestClass]
public sealed class ProductWorkspaceSectionsTests
{
    [TestMethod]
    public void TheCatalogueIsStableOrderedAndUniquelyKeyed()
    {
        var sections = ProductWorkspaceSections.All;

        CollectionAssert.AreEqual(
            new[] { "identity", "content", "price-stock", "media", "channel", "audit" },
            sections.Select(s => s.Key).ToArray(),
            "The keys are the deep-link surface: they are stable identifiers, in the order the workspace shows them.");
        Assert.AreEqual(sections.Count, sections.Select(s => s.Key).Distinct().Count());
        Assert.AreEqual(sections.Count, sections.Select(s => s.Label).Distinct().Count());
        Assert.IsTrue(sections.All(s => !string.IsNullOrWhiteSpace(s.Label)));
        Assert.AreEqual("identity", ProductWorkspaceSections.Default.Key);
    }

    [TestMethod]
    public void AKnownKeyResolvesAndAnUnknownOneFallsBackWithoutPretendingItWorked()
    {
        Assert.AreEqual("media", ProductWorkspaceSections.Resolve("media").Section.Key);
        Assert.IsTrue(ProductWorkspaceSections.Resolve("media").Found);
        Assert.AreEqual("price-stock", ProductWorkspaceSections.Resolve("  PRICE-STOCK ").Section.Key, "Keys are matched case-insensitively and trimmed; a link typed by hand still works.");

        var missing = ProductWorkspaceSections.Resolve("variants");
        Assert.IsFalse(missing.Found, "A link to a section this build does not have is reported, not silently redirected.");
        Assert.AreEqual(ProductWorkspaceSections.Default.Key, missing.Section.Key, "...and it still lands on a real section rather than a blank pane.");
        Assert.AreEqual(ProductWorkspaceSections.Default.Key, ProductWorkspaceSections.Resolve(null).Section.Key);
        Assert.AreEqual(ProductWorkspaceSections.Default.Key, ProductWorkspaceSections.Resolve("").Section.Key);
    }

    [TestMethod]
    public void DeepLinksRoundTripThroughTheRouteFormat()
    {
        Assert.AreEqual("products#media", ProductWorkspaceSections.Route("media"));
        Assert.AreEqual("products#identity", ProductWorkspaceSections.Route("nonsense"), "An unroutable section still produces a link that opens the workspace.");

        var parsed = ProductWorkspaceSections.ParseRoute("products#price-stock");
        Assert.AreEqual("products", parsed.Page);
        Assert.AreEqual("price-stock", parsed.SectionKey);

        var bare = ProductWorkspaceSections.ParseRoute("products");
        Assert.AreEqual("products", bare.Page);
        Assert.AreEqual("", bare.SectionKey, "A page link with no fragment carries no section, so the workspace keeps whatever it had.");

        foreach (var section in ProductWorkspaceSections.All)
            Assert.AreEqual(section.Key, ProductWorkspaceSections.ParseRoute(ProductWorkspaceSections.Route(section.Key)).SectionKey, section.Key);
    }

    [TestMethod]
    public void TheLastVisitedSectionIsRememberedSoReturningToTheWorkspaceRestoresIt()
    {
        var memory = new ProductWorkspaceSectionMemory();
        Assert.AreEqual(ProductWorkspaceSections.Default.Key, memory.Current, "A fresh workspace opens on the first section.");

        memory.Remember("channel");
        Assert.AreEqual("channel", memory.Current);

        memory.Remember("does-not-exist");
        Assert.AreEqual("channel", memory.Current, "An unknown key must not wipe the remembered section.");

        Assert.AreEqual("audit", memory.Resolve("audit"), "An explicit deep link wins over the remembered section...");
        Assert.AreEqual("audit", memory.Current, "...and becomes the new remembered one.");
        Assert.AreEqual("audit", memory.Resolve(""), "Navigating without a section keeps the operator where they were.");
    }
}
