using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

/// Coverage for #2546: the channel-plan editor's dirty-state must be pure
/// structural comparison against an explicit baseline snapshot - never derived
/// from watching individual field-changed events (which also fire when the
/// editor is populated programmatically by LoadPlan).
[TestClass]
public sealed class ChannelPlanEditorStateTests
{
    static ChannelPlanEditorFields Fields(string listing = "L1", string url = "https://example.test/l1", string category = "Elektronik", string price = "10.00", string currency = "USD", string stock = "5", string notes = "not") =>
        new(listing, url, category, price, currency, stock, notes);

    [TestMethod]
    public void IdenticalSnapshotsAreNotDirty()
    {
        var baseline = Fields();
        var current = Fields();
        Assert.IsFalse(ChannelPlanEditorState.IsDirty(baseline, current));
    }

    [TestMethod]
    public void ASingleChangedFieldIsDirty()
    {
        var baseline = Fields();
        var current = Fields(price: "12.00");
        Assert.IsTrue(ChannelPlanEditorState.IsDirty(baseline, current));
    }

    [TestMethod]
    public void ExactRevertToBaselineIsNoLongerDirty()
    {
        var baseline = Fields();
        var edited = Fields(notes: "değişti");
        Assert.IsTrue(ChannelPlanEditorState.IsDirty(baseline, edited));
        var revertedBack = Fields(); // user typed it back exactly
        Assert.IsFalse(ChannelPlanEditorState.IsDirty(baseline, revertedBack));
    }

    [TestMethod]
    public void EveryFieldIndependentlyMarksDirty()
    {
        var baseline = Fields();
        Assert.IsTrue(ChannelPlanEditorState.IsDirty(baseline, baseline with { ListingId = "L2" }));
        Assert.IsTrue(ChannelPlanEditorState.IsDirty(baseline, baseline with { ListingUrl = "https://example.test/other" }));
        Assert.IsTrue(ChannelPlanEditorState.IsDirty(baseline, baseline with { TargetCategory = "Ev" }));
        Assert.IsTrue(ChannelPlanEditorState.IsDirty(baseline, baseline with { Price = "99.00" }));
        Assert.IsTrue(ChannelPlanEditorState.IsDirty(baseline, baseline with { Currency = "EUR" }));
        Assert.IsTrue(ChannelPlanEditorState.IsDirty(baseline, baseline with { Stock = "0" }));
        Assert.IsTrue(ChannelPlanEditorState.IsDirty(baseline, baseline with { Notes = "başka" }));
    }

    [TestMethod]
    public void EmptyBaselineFieldsRoundTrip()
    {
        var baseline = new ChannelPlanEditorFields("", "", "", "0", "USD", "0", "");
        Assert.IsFalse(ChannelPlanEditorState.IsDirty(baseline, baseline));
    }

    [TestMethod]
    public void FindThenSaveWithItsVersionAllowsRepeatedEditsOfTheSamePlan()
    {
        // Mirrors the fixed ChannelProductsPanel flow (Find -> track Version ->
        // Save with that Version): before #2546 wired loadedVersion through, the
        // panel always saved with Version=0, so a second edit of an
        // already-saved plan would always hit the CAS stale-conflict check and
        // fail. This locks in that re-saving the same plan repeatedly by
        // reading back its Version each time keeps working.
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "channel-plan-editor-" + System.Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TrMarketplaceHubDesktop.ChannelProductsStore(root);
            store.Save(new() { ChannelId = "etsy", ShopId = "shop-a", ProductId = "p1", Currency = "USD" });
            var loaded = store.Find("etsy", "shop-a", "p1")!;
            store.Save(new() { ChannelId = "etsy", ShopId = "shop-a", ProductId = "p1", Currency = "USD", Notes = "ikinci düzenleme", Version = loaded.Version });
            var reloaded = store.Find("etsy", "shop-a", "p1")!;
            Assert.AreEqual("ikinci düzenleme", reloaded.Notes);
            store.Save(new() { ChannelId = "etsy", ShopId = "shop-a", ProductId = "p1", Currency = "USD", Notes = "üçüncü düzenleme", Version = reloaded.Version });
            Assert.AreEqual("üçüncü düzenleme", store.Find("etsy", "shop-a", "p1")!.Notes);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); System.IO.Directory.Delete(root, true); }
    }
}
