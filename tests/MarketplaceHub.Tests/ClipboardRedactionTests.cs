using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #786 (SECURITY: Clipboard copy redaction). No copy-to-clipboard action exists anywhere in this app today
// (confirmed by grep -- zero Clipboard.* call sites), so there is no existing UI entry point to wire this
// into yet; this builds the classification/masking core so a future copy command has a ready, tested
// building block, and reuses AuditStore.Sanitize's existing pattern detection instead of duplicating it.
[TestClass]
public sealed class ClipboardRedactionTests
{
    [TestMethod]
    public void AFieldNamedAsASecretIsNeverCopiedRawRegardlessOfItsValue()
    {
        var copied = ClipboardRedaction.PrepareForCopy("Token", "abc123");
        Assert.AreNotEqual("abc123", copied);
        Assert.IsTrue(ClipboardRedaction.IsSensitive("Token", "abc123"));
    }

    [TestMethod]
    public void ATokenLikeValueEmbeddedInAGenericFieldIsRedactedByContent()
    {
        var copied = ClipboardRedaction.PrepareForCopy("Notlar", "Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.payload.sig");
        Assert.IsFalse(copied.Contains("eyJhbGciOiJIUzI1NiJ9"));
        Assert.IsTrue(ClipboardRedaction.IsSensitive("Notlar", "Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.payload.sig"));
    }

    [TestMethod]
    public void AnEmailOrPhoneEmbeddedInAGenericFieldIsRedacted()
    {
        var email = ClipboardRedaction.PrepareForCopy("Açıklama", "Müşteri e-postası: ayse@example.com");
        Assert.IsFalse(email.Contains("ayse@example.com"));

        var phone = ClipboardRedaction.PrepareForCopy("Açıklama", "Telefon: 0532 123 45 67");
        Assert.IsFalse(phone.Contains("532 123 45 67") && phone.Contains("0532"));
    }

    [TestMethod]
    public void NonSensitiveTextIsCopiedCompletelyUnchanged()
    {
        var text = "SKU-1234 - Mavi Kazak - Beden M";
        Assert.AreEqual(text, ClipboardRedaction.PrepareForCopy("Ürün Adı", text));
        Assert.IsFalse(ClipboardRedaction.IsSensitive("Ürün Adı", text));
    }

    [TestMethod]
    public void MultiSelectionJudgesEachFieldIndependentlyInsteadOfMaskingTheWholeBatchOrNone()
    {
        var fields = new[]
        {
            ("Ürün Adı", "Mavi Kazak"),
            ("RefreshToken", "refresh-abc-123"),
            ("SKU", "SKU-1"),
            ("Notlar", "api_key=sk_live_shouldnotleak"),
        };

        var copied = ClipboardRedaction.PrepareManyForCopy(fields);

        Assert.AreEqual("Mavi Kazak", copied[0]);
        Assert.AreNotEqual("refresh-abc-123", copied[1]);
        Assert.AreEqual("SKU-1", copied[2]);
        Assert.IsFalse(copied[3].Contains("sk_live_shouldnotleak"));
    }
}
