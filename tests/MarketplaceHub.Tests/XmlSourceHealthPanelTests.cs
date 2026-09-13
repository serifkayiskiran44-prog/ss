using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #830 (DESIGN: XML source detail health panel). One verdict from recorded facts -- healthy, degraded, failed,
// never-run; six lines (reachability, latency, last success, schema drift, last validation, retry) plus the pool;
// actions that fit the verdict; error text never carries a header, credential, user info or payload.
[TestClass]
public sealed class XmlSourceHealthPanelTests
{
    static readonly DateTime Now = new(2026, 9, 13, 14, 0, 0, DateTimeKind.Utc);

    static SourceHealthFacts Facts(string health = "HEALTHY", long? latency = 250, string error = "", DateTime? lastSuccess = null, int count = 120, string feed = "COMPLETE",
        int rev = 2, int applied = 2, string lastShape = "shape-a", string currentShape = "shape-a", SourceRunSnapshot run = null, int pending = 0,
        int? blocking = null, int? warning = null, int? rows = null, DateTime? validated = null, ImportProgressStage? retry = null, bool running = false) =>
        new(health, health == "NEVER_CHECKED" ? null : Now.AddMinutes(-5), latency, health == "HEALTHY" ? 200 : null, error, lastSuccess, count, feed, rev, applied, lastShape, currentShape, run, count, pending, 0, blocking, warning, rows, validated, retry, running);

    [TestMethod]
    public void TheVerdictLadderIsHealthyDegradedFailedNeverRun()
    {
        var healthy = XmlSourceHealthPanel.Compose(Facts(lastSuccess: Now.AddHours(-1), run: new("Completed", Now.AddHours(-1), Now.AddHours(-1), null, "")), Now);
        Assert.AreEqual(SourceHealthVerdict.Healthy, healthy.Verdict); Assert.AreEqual("Sağlıklı", healthy.Headline); Assert.AreEqual(SeverityLevel.Success, healthy.Level);
        Assert.AreEqual(9, healthy.Lines.Count); Assert.AreEqual("Kimlik bilgisi", healthy.Lines[7].Label); Assert.AreEqual("bilinmiyor", healthy.Lines[7].Value);
        StringAssert.Contains(healthy.Lines[0].Value, "erişilebilir"); StringAssert.Contains(healthy.Lines[0].Value, "HTTP 200"); StringAssert.Contains(healthy.Lines[0].Value, "5 dk önce");
        Assert.AreEqual("Son başarı", healthy.Lines[2].Label); StringAssert.Contains(healthy.Lines[2].Value, "1 sa önce"); StringAssert.Contains(healthy.Lines[2].Value, "120 ürün");
        StringAssert.Contains(healthy.Lines[3].Value, "yok"); Assert.AreEqual("gerekmiyor", healthy.Lines[5].Value);
        CollectionAssert.AreEqual(new[] { SourceHealthActionKind.Refresh, SourceHealthActionKind.Check }, healthy.Actions.Select(a => a.Kind).ToArray());

        var slow = XmlSourceHealthPanel.Compose(Facts(latency: 6500, lastSuccess: Now.AddHours(-1)), Now);
        Assert.AreEqual(SourceHealthVerdict.Degraded, slow.Verdict); Assert.AreEqual("Kısmen sorunlu", slow.Headline); StringAssert.Contains(slow.Lines[1].Value, "yavaş"); Assert.AreEqual(SeverityLevel.Warning, slow.Lines[1].Level);

        var failed = XmlSourceHealthPanel.Compose(Facts(health: "TIMEOUT", latency: 15000, error: "XML kaynağı zaman aşımına uğradı.", lastSuccess: Now.AddDays(-1)), Now);
        Assert.AreEqual(SourceHealthVerdict.Failed, failed.Verdict); Assert.AreEqual("Başarısız", failed.Headline); Assert.AreEqual(SeverityLevel.Blocking, failed.Level);
        StringAssert.Contains(failed.Lines[0].Value, "zaman aşımı"); StringAssert.Contains(failed.Lines[0].Detail, "zaman aşımına");
        Assert.IsTrue(failed.Actions.Any(a => a.Kind == SourceHealthActionKind.Restart), "A failed source can be restarted.");

        var never = XmlSourceHealthPanel.Compose(Facts(health: "NEVER_CHECKED", latency: null, count: 0, applied: 0, lastShape: "", currentShape: null), Now);
        Assert.AreEqual(SourceHealthVerdict.NeverRun, never.Verdict); Assert.AreEqual("Hiç çalışmadı", never.Headline);
        Assert.AreEqual("hiç kontrol edilmedi", never.Lines[0].Value); Assert.AreEqual("ölçülmedi", never.Lines[1].Value); Assert.AreEqual("hiç başarılı olmadı", never.Lines[2].Value); Assert.AreEqual(SeverityLevel.Info, never.Lines[2].Level);
        Assert.AreEqual("henüz uygulanmadı", never.Lines[3].Value); Assert.AreEqual("önizleme yapılmadı", never.Lines[4].Value);
    }

    [TestMethod]
    public void DriftValidationAndRetryLinesComeFromTheRecordedFacts()
    {
        var shape = XmlSourceHealthPanel.Compose(Facts(lastSuccess: Now.AddHours(-3), currentShape: "shape-b"), Now);
        Assert.AreEqual(SourceHealthVerdict.Degraded, shape.Verdict); StringAssert.Contains(shape.Lines[3].Value, "XML yapısı"); Assert.AreEqual(SeverityLevel.Warning, shape.Lines[3].Level);
        Assert.IsTrue(shape.Actions.Any(a => a.Kind == SourceHealthActionKind.Preview), "Drift asks for a new preview.");

        var mapping = XmlSourceHealthPanel.Compose(Facts(lastSuccess: Now.AddHours(-3), rev: 4, applied: 3), Now);
        Assert.AreEqual(SourceHealthVerdict.Degraded, mapping.Verdict); StringAssert.Contains(mapping.Lines[3].Value, "eşleme rev. 4"); StringAssert.Contains(mapping.Lines[3].Value, "uygulanan rev. 3");

        var unread = XmlSourceHealthPanel.Compose(Facts(lastSuccess: Now.AddHours(-3), currentShape: null), Now);
        Assert.AreEqual(SourceHealthVerdict.Healthy, unread.Verdict, "An unread structure is not drift."); StringAssert.Contains(unread.Lines[3].Value, "yapı okunmadı");

        var validated = XmlSourceHealthPanel.Compose(Facts(lastSuccess: Now.AddHours(-3), rows: 40, blocking: 0, warning: 3, validated: Now.AddMinutes(-2)), Now);
        Assert.AreEqual("Son doğrulama", validated.Lines[4].Label); StringAssert.Contains(validated.Lines[4].Value, "40 satır · 0 engel · 3 uyarı · 2 dk önce"); Assert.AreEqual(SeverityLevel.Warning, validated.Lines[4].Level);
        var blocked = XmlSourceHealthPanel.Compose(Facts(lastSuccess: Now.AddHours(-3), rows: 40, blocking: 2, warning: 0, validated: Now), Now);
        Assert.AreEqual(SourceHealthVerdict.Degraded, blocked.Verdict); Assert.AreEqual(SeverityLevel.Blocking, blocked.Lines[4].Level);

        var stopped = XmlSourceHealthPanel.Compose(Facts(lastSuccess: Now.AddHours(-3), retry: ImportProgressStage.Apply), Now);
        Assert.AreEqual(SourceHealthVerdict.Failed, stopped.Verdict); StringAssert.Contains(stopped.Lines[5].Value, "Havuza aktar (yerel) aşamasında durdu");
        Assert.IsTrue(stopped.Actions.Any(a => a.Kind == SourceHealthActionKind.Restart));

        var lastRunFailed = XmlSourceHealthPanel.Compose(Facts(lastSuccess: Now.AddHours(-3), run: new("Failed", Now.AddHours(-1), Now.AddHours(-1), null, "Zorunlu XML alanları bulunamadı: Sku")), Now);
        Assert.AreEqual(SourceHealthVerdict.Failed, lastRunFailed.Verdict); StringAssert.Contains(lastRunFailed.Lines[5].Value, "başarısız"); StringAssert.Contains(lastRunFailed.Lines[5].Detail, "Sku");
        var recovered = XmlSourceHealthPanel.Compose(Facts(lastSuccess: Now.AddMinutes(-10), run: new("Failed", Now.AddHours(-1), Now.AddHours(-1), null, "x")), Now);
        Assert.AreEqual(SourceHealthVerdict.Healthy, recovered.Verdict, "A success after the failed run clears it.");
        var abandoned = XmlSourceHealthPanel.Compose(Facts(lastSuccess: Now.AddHours(-3), run: new("Abandoned", Now.AddHours(-1), Now, null, "Lease süresi doldu")), Now);
        StringAssert.Contains(abandoned.Lines[5].Value, "yarım kaldı");
        var running = XmlSourceHealthPanel.Compose(Facts(health: "TIMEOUT", lastSuccess: Now.AddHours(-3), running: true), Now);
        Assert.AreEqual("aktarım sürüyor", running.Lines[5].Value); Assert.IsFalse(running.Actions.Any(a => a.Kind == SourceHealthActionKind.Restart), "Nothing restarts while an import runs.");
        var quarantined = XmlSourceHealthPanel.Compose(Facts(lastSuccess: Now.AddHours(-3), pending: 4), Now);
        Assert.AreEqual(SourceHealthVerdict.Degraded, quarantined.Verdict); StringAssert.Contains(quarantined.Lines[6].Value, "4 bekleyen");
    }

    [TestMethod]
    public void ErrorTextNeverCarriesAHeaderCredentialUserInfoOrPayload()
    {
        var text = XmlSourceHealthPanel.SafeError("401 from https://ali:gizli@h.example.com/f.xml?token=SECRET999 Authorization: Basic dXNlcjpwYXNz Bearer abcdefTOKEN123");
        Assert.IsFalse(text.Contains("gizli") || text.Contains("SECRET999") || text.Contains("dXNlcjpwYXNz") || text.Contains("abcdefTOKEN123"), text);
        StringAssert.Contains(text, "h.example.com", "The host stays so the person knows which address failed.");
        StringAssert.Contains(text, "401");

        Assert.AreEqual("Ayrıntı tanılama günlüğünde.", XmlSourceHealthPanel.SafeError("<?xml version=\"1.0\"?><error><token>SECRET</token></error>"));
        Assert.AreEqual("Ayrıntı tanılama günlüğünde.", XmlSourceHealthPanel.SafeError("Boom\n   at TrMarketplaceHubDesktop.Catalog.XmlSourceHealthChecker.CheckAsync(HttpClient http)"));
        Assert.AreEqual("", XmlSourceHealthPanel.SafeError("  "));

        var failed = XmlSourceHealthPanel.Compose(Facts(health: "AUTH_ERROR", error: "x-api-key: 0123456789abcdef reddedildi"), Now);
        Assert.IsFalse(failed.Lines[0].Detail.Contains("0123456789abcdef"), failed.Lines[0].Detail);
        StringAssert.Contains(failed.Lines[0].Value, "yetki hatası");
    }
}
