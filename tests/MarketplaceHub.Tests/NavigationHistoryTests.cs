using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #889 (UX STATE: Scoped navigation history). The trail keeps its scope in both directions: Back leaves a forward
// branch, Forward walks it with the same checks, a step remembers the record's revision and says so when the record
// changed meanwhile, a crumb never carries personal data, and a saved trail comes back only through a re-validation
// against this session's screens, stores and records.
[TestClass]
public sealed class NavigationHistoryTests
{
    const string All = DashboardStoreFilter.AllStoresKey;
    static readonly string[] Stores = { "etsy|shop-a", "ozon|shop-c" };
    static DrillThroughStack Board(string store = All) => new(new DrillTarget("dashboard", "Genel bakış", store));
    static DrillTarget Product(string id, string revision = "r1", string store = All) => new("products", "Ürün yönetimi", store, "product", id, "SKU-" + id, revision);
    static readonly Func<DrillTarget, bool> Alive = _ => true;

    [TestMethod]
    public void ForwardWalksTheBranchBackLeftAndANewStepDropsIt()
    {
        var stack = Board();
        Assert.IsTrue(stack.Open(new DrillTarget("products", "Kritik stok", All, "anomaly", "oversell", "Aşırı satış riski"), Stores).Allowed);
        Assert.IsTrue(stack.Open(Product("9"), Stores).Allowed);
        Assert.IsFalse(stack.CanGoForward, "nothing is in front of the newest step");

        stack.Back(Alive);
        Assert.IsTrue(stack.CanGoForward); Assert.AreEqual("oversell", stack.Current.EntityId);
        var forward = stack.Forward(Alive);
        Assert.AreEqual("9", forward.Target.EntityId); Assert.AreEqual("", forward.Notice); Assert.IsFalse(stack.CanGoForward);
        Assert.AreEqual(3, stack.Crumbs.Count);

        stack.Back(Alive); stack.Back(Alive);
        Assert.AreEqual("dashboard", stack.Current.Route); Assert.IsTrue(stack.CanGoForward);
        var branch = stack.Forward(Alive); Assert.AreEqual("oversell", branch.Target.EntityId, "forward walks the branch in the order it was left");
        Assert.IsTrue(stack.Open(new DrillTarget("orders", "Siparişler"), Stores).Allowed);
        Assert.IsFalse(stack.CanGoForward, "a new step is a new branch");
        var stay = stack.Forward(Alive); Assert.AreEqual("orders", stay.Target.Route); Assert.AreEqual(3, stack.Crumbs.Count);
    }

    [TestMethod]
    public void AStepNoticesARecordThatChangedMeanwhileAndKeepsTheNewRevision()
    {
        var stack = Board();
        stack.Open(Product("9", "r1"), Stores);
        stack.Open(new DrillTarget("orders", "Siparişler"), Stores);

        var back = stack.Back(Alive, _ => "r2");
        Assert.IsTrue(back.RevisionChanged); StringAssert.Contains(back.Notice, "değişti"); Assert.IsFalse(back.DroppedStaleEntity);
        Assert.AreEqual("r2", stack.Current.Revision, "the step now remembers the revision it showed");
        Assert.IsFalse(stack.Back(Alive, _ => "r2").RevisionChanged, "the root has no record");
        var forward = stack.Forward(Alive, _ => "r2");
        Assert.IsFalse(forward.RevisionChanged, "the same revision is not a change");
        stack.Back(Alive, _ => "r2");
        var changedAgain = stack.Forward(Alive, _ => "r3");
        Assert.IsTrue(changedAgain.RevisionChanged); StringAssert.StartsWith(changedAgain.Notice, "İleri gidilen"); Assert.AreEqual("r3", stack.Current.Revision);
        // A step without a revision, or a kind whose revision cannot be read, never claims a change.
        var bare = Board(); bare.Open(Product("1", ""), Stores); bare.Open(new DrillTarget("orders", "Siparişler"), Stores);
        Assert.IsFalse(bare.Back(Alive, _ => "anything").RevisionChanged);
        bare.Forward(Alive, _ => ""); Assert.IsFalse(bare.Back(Alive, _ => "").RevisionChanged);
    }

    [TestMethod]
    public void AForwardStepWhoseRecordVanishedDegradesToItsScreen()
    {
        var stack = Board();
        stack.Open(Product("9"), Stores);
        stack.Back(Alive);
        var forward = stack.Forward(_ => false);
        Assert.IsTrue(forward.DroppedStaleEntity); Assert.AreEqual("", forward.Target.EntityId); Assert.AreEqual("products", forward.Target.Route);
        StringAssert.Contains(forward.Notice, "İleri gidilen kayıt artık yok");
        Assert.AreEqual("Ürün yönetimi", stack.Crumbs[^1].Label, "the crumb stops claiming the record");
    }

    [TestMethod]
    public void CrumbsNeverCarryPersonalDataAndStayShort()
    {
        var stack = Board();
        stack.Open(new DrillTarget("orders", "Sipariş ve kargo", All, "order", "etsy|S1|1001", "ayse.yilmaz@example.com · 1001"), Stores);
        Assert.AreEqual("Sipariş ve kargo", stack.Crumbs[^1].Label, "a label the redaction would change falls back to the screen's title");
        Assert.IsFalse(stack.TrailText().Contains("example.com"));
        stack.Open(Product("2") with { EntityLabel = new string('ü', 200) }, Stores);
        Assert.AreEqual(DrillThroughStack.LabelLimit, stack.Crumbs[^1].Label.Length); StringAssert.EndsWith(stack.Crumbs[^1].Label, "…");
        var payload = DrillHistoryCodec.Serialize(stack.Snapshot());
        Assert.IsFalse(payload.Contains("example.com"), "the saved trail carries no personal data either");
        Assert.IsTrue(DrillHistoryCodec.TryDeserialize(payload, out var state)); Assert.AreEqual("", state.Steps[1].EntityLabel);
    }

    [TestMethod]
    public void HistoryRoundTripsAndRestoreRevalidatesStoresScreensAndRecords()
    {
        var stack = Board("etsy|shop-a");
        stack.Open(new DrillTarget("products", "Kritik stok", "etsy|shop-a", "anomaly", "oversell", "Aşırı satış riski"), Stores);
        stack.Open(Product("9", "r1", "etsy|shop-a"), Stores);
        stack.Back(Alive);
        var payload = DrillHistoryCodec.Serialize(stack.Snapshot());
        Assert.IsTrue(DrillHistoryCodec.TryDeserialize(payload, out var saved));
        Assert.AreEqual(2, saved.Steps.Count); Assert.AreEqual(1, saved.Forward.Count); Assert.AreEqual("r1", saved.Forward[0].Revision);

        // The same session again: everything comes back, forward included.
        var same = Board();
        var restored = same.Restore(saved, _ => true, Stores, Alive);
        Assert.AreEqual(2, restored.Steps); Assert.AreEqual(1, restored.Forward); Assert.AreEqual(0, restored.Notices.Count, string.Join("; ", restored.Notices));
        Assert.AreEqual("etsy|shop-a", same.CurrentStoreKey); Assert.AreEqual("oversell", same.Current.EntityId); Assert.IsTrue(same.CanGoBack); Assert.IsTrue(same.CanGoForward);
        Assert.AreEqual("9", same.Forward(Alive).Target.EntityId);

        // A session that no longer offers the trail's store leaves the whole trail behind.
        var other = Board();
        var dropped = other.Restore(saved, _ => true, new[] { "ozon|shop-c" }, Alive);
        Assert.AreEqual(1, dropped.Steps); Assert.AreEqual(0, dropped.Forward); Assert.AreEqual(All, other.CurrentStoreKey); Assert.IsFalse(other.CanGoBack);
        StringAssert.Contains(dropped.Notices.Single(), "sunulmayan bir mağazaya");

        // A screen this build lacks cuts the trail there.
        var cut = Board();
        var cutResult = cut.Restore(saved, route => route != "products", Stores, Alive);
        Assert.AreEqual(1, cutResult.Steps); Assert.AreEqual(0, cutResult.Forward); StringAssert.Contains(cutResult.Notices.Single(), "bu yapıda yok");

        // A record that is gone keeps its screen and drops the record.
        var gone = Board();
        var goneResult = gone.Restore(saved, _ => true, Stores, t => t.EntityId != "9");
        Assert.AreEqual(2, goneResult.Steps); Assert.AreEqual(1, goneResult.Forward); StringAssert.Contains(goneResult.Notices.Single(), "kayıt artık yok");
        var degraded = gone.Forward(Alive); Assert.AreEqual("", degraded.Target.EntityId); Assert.AreEqual("products", degraded.Target.Route);

        // Payloads that are not a trail are refused, not repaired.
        Assert.IsFalse(DrillHistoryCodec.TryDeserialize("", out _));
        Assert.IsFalse(DrillHistoryCodec.TryDeserialize("nope", out _));
        Assert.IsFalse(DrillHistoryCodec.TryDeserialize("{\"steps\":[]}", out _));
        Assert.IsFalse(DrillHistoryCodec.TryDeserialize("{\"steps\":[{\"route\":\"../etc\",\"title\":\"x\"}]}", out _));
        Assert.IsFalse(DrillHistoryCodec.TryDeserialize("{\"steps\":[{\"route\":\"dashboard\",\"title\":\"" + new string('a', 300) + "\"}]}", out _));
        var many = new DrillHistoryState(Enumerable.Range(0, DrillThroughStack.MaxSteps + 1).Select(i => new DrillTarget("products", "Ürün " + i)).ToList(), Array.Empty<DrillTarget>());
        Assert.IsTrue(DrillHistoryCodec.TryDeserialize(DrillHistoryCodec.Serialize(many), out var capped)); Assert.AreEqual(DrillThroughStack.MaxSteps, capped.Steps.Count, "the codec never writes more than the cap");
        Assert.IsFalse(DrillHistoryCodec.TryDeserialize("{\"steps\":[" + string.Join(",", Enumerable.Repeat("{\"route\":\"products\",\"title\":\"x\"}", DrillThroughStack.MaxSteps + 1)) + "]}", out _), "and reads no more than it");
        Assert.IsTrue(DrillHistoryCodec.TryDeserialize("{\"steps\":[{\"route\":\"orders\",\"title\":\"Siparişler\",\"entityKind\":\"order\",\"entityId\":\"etsy|S1|1\",\"entityLabel\":\"ayse@example.com\"}]}", out var labelled));
        Assert.AreEqual("", labelled.Steps[0].EntityLabel, "a saved label with personal data comes back empty");
    }
}
