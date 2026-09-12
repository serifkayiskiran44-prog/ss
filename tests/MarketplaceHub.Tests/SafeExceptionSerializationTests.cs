using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #784 (SECURITY: Safe exception serialization) + #785 (SECURITY: Local file path disclosure guard):
// a global error handler or job-failure log must keep enough structure to actually debug from (type,
// message, full stack, inner-exception chain) while never leaking a secret, PII, or the operating local
// user's profile path into a log, export, or on-screen error.
[TestClass]
public sealed class SafeExceptionSerializationTests
{
    [TestMethod]
    public void WindowsUserProfilePathHasItsUsernameRedacted()
    {
        var safe = AuditStore.Sanitize(@"Dosya bulunamadı: C:\Users\serifkayiskiran\Documents\gizli-liste.xlsx");
        StringAssert.Contains(safe, @"C:\Users\[user]\Documents\gizli-liste.xlsx");
        Assert.IsFalse(safe.Contains("serifkayiskiran", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void WindowsTempPathUnderTheUserProfileHasItsUsernameRedactedToo()
    {
        var safe = AuditStore.Sanitize(@"IOException at C:\Users\serifkayiskiran\AppData\Local\Temp\import-9f3a.tmp");
        Assert.IsFalse(safe.Contains("serifkayiskiran", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(safe, @"AppData\Local\Temp\import-9f3a.tmp");
    }

    [TestMethod]
    public void ANormalRelativePathIsLeftCompletelyUntouched()
    {
        var message = @"Kaynak dosyası bulunamadı: data\catalog\feed.xml";
        Assert.AreEqual(message, AuditStore.Sanitize(message));
    }

    [TestMethod]
    public void SerializePreservesExceptionTypeMessageAndStackTrace()
    {
        Exception thrown;
        try { throw new InvalidOperationException("Bir işlem hatası oluştu."); }
        catch (Exception ex) { thrown = ex; }

        var report = SafeExceptionSerializer.Serialize(thrown);

        StringAssert.Contains(report, nameof(InvalidOperationException));
        StringAssert.Contains(report, "Bir işlem hatası oluştu.");
        StringAssert.Contains(report, nameof(SerializePreservesExceptionTypeMessageAndStackTrace)); // proves the real stack trace, not a placeholder, is present.
    }

    [TestMethod]
    public void SerializeWalksTheFullInnerExceptionChain()
    {
        Exception thrown;
        try
        {
            try { throw new ArgumentException("İç hata: geçersiz değer."); }
            catch (Exception inner) { throw new InvalidOperationException("Dış hata.", inner); }
        }
        catch (Exception ex) { thrown = ex; }

        var report = SafeExceptionSerializer.Serialize(thrown);

        StringAssert.Contains(report, "Dış hata.");
        StringAssert.Contains(report, "İç hata: geçersiz değer.");
        StringAssert.Contains(report, nameof(ArgumentException));
    }

    [TestMethod]
    public void SerializeRedactsASecretEmbeddedInTheExceptionMessage()
    {
        var thrown = new InvalidOperationException("Bağlantı başarısız: api_key=sk_live_abcdef1234567890");
        var report = SafeExceptionSerializer.Serialize(thrown);

        Assert.IsFalse(report.Contains("sk_live_abcdef1234567890"), "A raw secret value must never reach a serialized exception report.");
        StringAssert.Contains(report, "[redacted]");
    }

    [TestMethod]
    public void SerializeRedactsALocalFilePathEmbeddedInTheExceptionMessage()
    {
        var thrown = new InvalidOperationException(@"Erişim reddedildi: C:\Users\serifkayiskiran\gizli.db");
        var report = SafeExceptionSerializer.Serialize(thrown);

        Assert.IsFalse(report.Contains("serifkayiskiran", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void SerializeRedactsABearerTokenEmbeddedInAnHttpErrorMessage()
    {
        var thrown = new InvalidOperationException("HTTP isteği reddedildi. Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.payload.signature");
        var report = SafeExceptionSerializer.Serialize(thrown);

        Assert.IsFalse(report.Contains("eyJhbGciOiJIUzI1NiJ9"), "A bearer token must never reach a serialized exception report.");
    }

    [TestMethod]
    public void SerializeCapsDepthInsteadOfLoopingForeverOnACyclicOrExtremelyDeepChain()
    {
        // Deliberately deep (50 levels) rather than truly cyclic -- .NET's own InnerException chain cannot be
        // made to cycle back to itself, so the realistic risk this guards against is an unusually deep chain.
        Exception current = new InvalidOperationException("level-0");
        for (var i = 1; i <= 50; i++) current = new InvalidOperationException($"level-{i}", current);

        var report = SafeExceptionSerializer.Serialize(current);

        StringAssert.Contains(report, "level-49"); // near the top of the chain must be present
        Assert.IsFalse(report.Contains("level-0"), "Serialization must stop at a bounded depth rather than walking an unbounded chain.");
    }
}
