using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #831 (DESIGN: XML preview sticky action toolbar). Counts and validation from the selection; apply only for a
// fresh preview with a clean selection while nothing runs, otherwise the reason; cancel only while running;
// recompute whenever XML is loaded and nothing runs; the freshness spelled out.
[TestClass]
public sealed class PreviewActionToolbarTests
{
    static PreviewToolbarState State(int rows = 300, int shown = 300, int selected = 300, int added = 200, int changed = 50, int unchanged = 50, int blocking = 0, int warning = 12,
        bool loaded = true, bool exists = true, bool fresh = true, bool running = false, string stale = "") =>
        new(rows, shown, selected, added, changed, unchanged, blocking, warning, loaded, exists, fresh, running, stale);

    [TestMethod]
    public void AFreshPreviewWithACleanSelectionCanApplyAndSaysWhatIsAffected()
    {
        var m = PreviewActionToolbar.Compose(State());
        Assert.IsTrue(m.CanApply); Assert.AreEqual("", m.ApplyReason);
        Assert.AreEqual("Seçilileri havuza al (300) · yerel", m.ApplyLabel, "The apply names the count and stays local.");
        Assert.AreEqual("300 / 300 seçili · etkilenen 250 (200 yeni, 50 değişen) · 50 aynı", m.CountsText);
        Assert.AreEqual("0 engel · 12 uyarı (seçili)", m.ValidationText); Assert.AreEqual(SeverityLevel.Warning, m.ValidationLevel);
        Assert.AreEqual("Önizleme güncel", m.StatusText); Assert.AreEqual(SeverityLevel.Success, m.StatusLevel);
        Assert.IsFalse(m.CanCancel); Assert.IsTrue(m.CanRecompute);

        var filtered = PreviewActionToolbar.Compose(State(shown: 12));
        StringAssert.Contains(filtered.CountsText, "filtre: 12 görünür", "A filter never hides how many rows exist.");
        var clean = PreviewActionToolbar.Compose(State(warning: 0));
        Assert.AreEqual(SeverityLevel.Success, clean.ValidationLevel);
    }

    [TestMethod]
    public void ApplyIsRefusedWithAReasonForEveryBlockedState()
    {
        var none = PreviewActionToolbar.Compose(State(selected: 0, added: 0, changed: 0, unchanged: 0, warning: 0));
        Assert.IsFalse(none.CanApply); StringAssert.Contains(none.ApplyReason, "satır seçin"); Assert.AreEqual("seçim yok", none.ValidationText);

        var blocked = PreviewActionToolbar.Compose(State(blocking: 3));
        Assert.IsFalse(blocked.CanApply); StringAssert.Contains(blocked.ApplyReason, "3 seçili satırda engel"); Assert.AreEqual(SeverityLevel.Blocking, blocked.ValidationLevel);

        var stale = PreviewActionToolbar.Compose(State(fresh: false, stale: "XML veya eşleme değişti; önizleme geçersiz, yeniden önizleyin."));
        Assert.IsFalse(stale.CanApply); StringAssert.Contains(stale.ApplyReason, "geçersiz"); StringAssert.Contains(stale.StatusText, "Önizleme geçersiz: XML veya eşleme değişti"); Assert.AreEqual(SeverityLevel.Warning, stale.StatusLevel);
        Assert.IsTrue(stale.CanRecompute, "A stale preview is recomputed from here.");

        var noPreview = PreviewActionToolbar.Compose(State(exists: false, fresh: false, selected: 0, added: 0, changed: 0, unchanged: 0, warning: 0));
        Assert.IsFalse(noPreview.CanApply); StringAssert.Contains(noPreview.ApplyReason, "önizleme hesaplayın"); Assert.AreEqual("Önizleme hesaplanmadı", noPreview.StatusText);

        var noXml = PreviewActionToolbar.Compose(State(loaded: false, exists: false, fresh: false, rows: 0, shown: 0, selected: 0, added: 0, changed: 0, unchanged: 0, warning: 0));
        Assert.IsFalse(noXml.CanApply); StringAssert.Contains(noXml.ApplyReason, "XML'i okuyun"); Assert.IsFalse(noXml.CanRecompute);

        var running = PreviewActionToolbar.Compose(State(running: true));
        Assert.IsFalse(running.CanApply); Assert.IsTrue(running.CanCancel); Assert.IsFalse(running.CanRecompute);
        StringAssert.Contains(running.ApplyReason, "sürüyor"); Assert.AreEqual("Aktarım sürüyor · iptal edilebilir", running.StatusText);
    }
}
