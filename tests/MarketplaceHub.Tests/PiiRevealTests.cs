using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #841 (DESIGN: Customer PII reveal affordance). Masked by default with recognisable stubs; a reveal refused
// without policy or without a reason, granted with a fixed re-mask moment; the audit names fields and reason,
// never a value; the policy clamps its seconds.
[TestClass]
public sealed class PiiRevealTests
{
    static readonly DateTime Now = new(2026, 9, 13, 20, 0, 0, DateTimeKind.Utc);
    static readonly OrderCustomer Customer = new("etsy", "S1", "o-1", "Ayşe Yılmaz", "ayse.yilmaz@example.com", "+90 532 123 45 67", "Bağdat Cad. 12/3 Kadıköy İstanbul");

    [TestMethod]
    public void EveryFieldIsMaskedToARecognisableStub()
    {
        var masked = PiiReveal.Masked(Customer).ToDictionary(x => x.Field, x => x.Masked);
        Assert.AreEqual("A••• Y•••", masked["Ad"]);
        Assert.AreEqual("a••@e•••.com", masked["E-posta"]);
        Assert.AreEqual("+•••••••••67", masked["Telefon"]);
        Assert.AreEqual("Bağdat •••", masked["Adres"]);
        var all = string.Join(" ", masked.Values);
        Assert.IsFalse(all.Contains("Yılmaz") || all.Contains("yilmaz") || all.Contains("532") || all.Contains("Kadıköy"), all);
        Assert.AreEqual("—", PiiReveal.MaskEmail("")); Assert.AreEqual("x•••", PiiReveal.MaskEmail("x@")); Assert.AreEqual("••", PiiReveal.MaskPhone("12")); Assert.AreEqual("—", PiiReveal.MaskAddress("  ")); Assert.AreEqual("Ist •••", PiiReveal.MaskAddress("Istanbul"));
        Assert.IsTrue(new OrderCustomer("e", "s", "o", "", " ", "", "").IsEmpty); Assert.IsFalse(Customer.IsEmpty);
    }

    [TestMethod]
    public void ARevealIsRefusedWithoutPolicyOrReasonAndGrantedWithARemaskMoment()
    {
        var denied = PiiReveal.Request(new PiiRevealPolicy(Allowed: false), "müşteri kargo adresini teyit etti", Now);
        Assert.IsFalse(denied.Allowed); StringAssert.Contains(denied.DeniedBecause, "izin vermiyor"); Assert.IsNull(denied.RemaskAtUtc);

        var noReason = PiiReveal.Request(new PiiRevealPolicy(Allowed: true), "ok", Now);
        Assert.IsFalse(noReason.Allowed); StringAssert.Contains(noReason.DeniedBecause, "Gerekçe gerekli");

        var granted = PiiReveal.Request(new PiiRevealPolicy(Allowed: true, RevealSeconds: 45), "  kargo   adresi teyidi ", Now);
        Assert.IsTrue(granted.Allowed); Assert.AreEqual("kargo adresi teyidi", granted.Reason); Assert.AreEqual(Now.AddSeconds(45), granted.RemaskAtUtc);

        var noReasonNeeded = PiiReveal.Request(new PiiRevealPolicy(Allowed: true, RequireReason: false), "", Now);
        Assert.IsTrue(noReasonNeeded.Allowed);

        Assert.AreEqual(PiiRevealPolicy.MinSeconds, new PiiRevealPolicy(true, 1).Normalized().RevealSeconds); Assert.AreEqual(PiiRevealPolicy.MaxSeconds, new PiiRevealPolicy(true, 9999).Normalized().RevealSeconds);
        Assert.AreEqual(Now.AddSeconds(5), PiiReveal.Request(new PiiRevealPolicy(true, 1, false), "", Now).RemaskAtUtc, "The clamp applies to the grant.");
    }

    [TestMethod]
    public void TheAuditNamesFieldsAndReasonNeverAValue()
    {
        var granted = PiiReveal.AuditFor(Customer, PiiReveal.Request(new PiiRevealPolicy(Allowed: true), "kargo adresi teyidi token=SECRET1", Now), Now);
        Assert.AreEqual("orders", granted.Module); Assert.AreEqual("pii-reveal", granted.Action); Assert.AreEqual("o-1", granted.OrderId); Assert.AreEqual("S1", granted.ShopId);
        StringAssert.Contains(granted.Detail, "Alanlar: Ad, E-posta, Telefon, Adres"); StringAssert.Contains(granted.Detail, "gerekçe: kargo adresi teyidi"); StringAssert.Contains(granted.Detail, "otomatik maskeleme: 20:00:30Z");
        Assert.IsFalse(granted.Detail.Contains("Yılmaz") || granted.Detail.Contains("example.com") || granted.Detail.Contains("532") || granted.Detail.Contains("Bağdat") || granted.Detail.Contains("SECRET1"), granted.Detail);

        var denied = PiiReveal.AuditFor(Customer, PiiReveal.Request(new PiiRevealPolicy(Allowed: false), "bakmak istedim", Now), Now);
        Assert.AreEqual("pii-reveal-denied", denied.Action); Assert.AreEqual("Warning", denied.Outcome); StringAssert.Contains(denied.Detail, "Reddedildi:"); StringAssert.Contains(denied.Detail, "bakmak istedim");
    }
}
