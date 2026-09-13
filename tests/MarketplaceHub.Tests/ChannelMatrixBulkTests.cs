using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #845 (DESIGN: Channel matrix bulk preview drawer). The drawer model counts affected / skipped / blocked lines
// and caps a large selection's listing; apply is refused for an empty selection, a store the shell does not
// offer, nothing to change, a stale matrix revision or an already-applied preview; the revision moves with the
// matrix and not with row order.
[TestClass]
public sealed class ChannelMatrixBulkTests
{
    static readonly ChannelMatrixBulkTarget Etsy = new("etsy|S1", "etsy", "S1", "Etsy · S1");
    static ChannelListingMatrixRow Row(string productId, string channel, string shop, string mapping, string listing = "") =>
        new(productId, "SKU-" + productId, "Ürün " + productId, channel, channel, shop, mapping, listing, "None", "", null, "CONNECTED", "ProductsRead");
    static BulkProductPreviewLine Line(string sku, string before, string after, string status = "READY", string error = "") => new() { ProductId = "p-" + sku, Sku = sku, Name = "Ürün " + sku, Before = before, After = after, Status = status, Error = error };
    static BulkProductPreview Preview(params BulkProductPreviewLine[] lines) => new(Guid.NewGuid(), new BulkProductOperationRequest(BulkProductOperationKind.SetChannelMapping, Channel: "etsy", ShopId: "S1", TargetCategory: "Mug"), lines, DateTime.UtcNow);

    [TestMethod]
    public void TheDrawerCountsAndCapsAndRefusesEmptyWrongStoreAndNoChange()
    {
        var preview = Preview(Line("A", "plan yok", "ilan=; kategori=Mug"), Line("B", "ilan=; kategori=Mug", "ilan=; kategori=Mug"), Line("C", "plan yok", "ilan=; kategori=Mug", "ERROR", "Ürün silinmiş token=SECRET"));
        var m = ChannelMatrixBulk.Compose(preview, Etsy, new[] { "etsy|S1" }, "rev1", 3);
        Assert.IsTrue(m.CanApply, m.Reason); Assert.AreEqual("3 seçili ürün · 1 uygulanacak · 1 zaten aynı · 1 engelli", m.Summary);
        Assert.AreEqual("＋", m.Lines[0].Marker); Assert.AreEqual("＝", m.Lines[1].Marker); Assert.AreEqual("plan zaten aynı", m.Lines[1].Reason); Assert.AreEqual("✖", m.Lines[2].Marker);
        Assert.IsFalse(m.Lines[2].Reason.Contains("SECRET"), "A line's error is sanitized."); Assert.AreEqual("Etsy · S1", m.TargetLabel); Assert.AreEqual("rev1", m.RevisionAtPreview);

        Assert.IsFalse(ChannelMatrixBulk.Compose(preview, Etsy, new[] { "etsy|S1" }, "rev1", 0).CanApply, "Empty selection.");
        StringAssert.Contains(ChannelMatrixBulk.Compose(preview, Etsy, new[] { "etsy|S1" }, "rev1", 0).Reason, "Seçim boş");
        var wrong = ChannelMatrixBulk.Compose(preview, Etsy, new[] { "ebay|E9" }, "rev1", 3);
        Assert.IsFalse(wrong.CanApply); StringAssert.Contains(wrong.Reason, "yanlış mağazaya yazılmaz");
        var nothing = ChannelMatrixBulk.Compose(Preview(Line("B", "x", "x")), Etsy, null, "rev1", 1);
        Assert.IsFalse(nothing.CanApply); StringAssert.Contains(nothing.Reason, "Uygulanacak değişiklik yok");
        Assert.IsFalse(ChannelMatrixBulk.Compose(null, Etsy, null, "rev1", 2).CanApply);

        var big = Preview(Enumerable.Range(0, 1000).Select(i => Line($"S{i:D4}", "plan yok", "ilan=; kategori=Mug")).ToArray());
        var large = ChannelMatrixBulk.Compose(big, Etsy, null, "rev1", 1000);
        Assert.AreEqual(1000, large.Affected); Assert.AreEqual(ChannelMatrixBulk.MaxLinesShown, large.Lines.Count); Assert.AreEqual(950, large.LinesTruncated); Assert.IsTrue(large.CanApply);
    }

    [TestMethod]
    public void TheRevisionFollowsTheMatrixNotItsOrderAndApplyIsRefusedWhenStaleOrRepeated()
    {
        var rows = new[] { Row("p1", "etsy", "S1", "MISSING"), Row("p2", "etsy", "S1", "SYNCED", "L2") };
        var rev = ChannelMatrixBulk.Revision(rows);
        Assert.AreEqual(16, rev.Length);
        Assert.AreEqual(rev, ChannelMatrixBulk.Revision(rows.Reverse()), "Order does not matter.");
        Assert.AreNotEqual(rev, ChannelMatrixBulk.Revision(new[] { Row("p1", "etsy", "S1", "DRAFT", "L1"), rows[1] }), "A state or listing change moves the revision.");
        Assert.AreNotEqual(rev, ChannelMatrixBulk.Revision(rows.Take(1)), "A row gone moves the revision.");

        var applied = new HashSet<Guid>(); var id = Guid.NewGuid();
        Assert.IsTrue(ChannelMatrixBulk.CheckBeforeApply(rev, rev, new[] { "etsy|S1" }, "etsy|S1", applied, id).Ok);
        var stale = ChannelMatrixBulk.CheckBeforeApply(rev, "other", new[] { "etsy|S1" }, "etsy|S1", applied, id);
        Assert.IsFalse(stale.Ok); StringAssert.Contains(stale.Reason, "bayat revizyon");
        var wrongStore = ChannelMatrixBulk.CheckBeforeApply(rev, rev, new[] { "ebay|E9" }, "etsy|S1", applied, id);
        Assert.IsFalse(wrongStore.Ok); StringAssert.Contains(wrongStore.Reason, "yanlış mağazaya");
        applied.Add(id);
        var repeated = ChannelMatrixBulk.CheckBeforeApply(rev, rev, new[] { "etsy|S1" }, "etsy|S1", applied, id);
        Assert.IsFalse(repeated.Ok); StringAssert.Contains(repeated.Reason, "zaten uygulandı");
        Assert.IsTrue(ChannelMatrixBulk.CheckBeforeApply(rev, rev, null, "etsy|S1", applied, Guid.NewGuid()).Ok, "A new preview after the same matrix is fine.");
    }
}
