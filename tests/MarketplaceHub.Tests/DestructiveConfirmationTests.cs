using System;
using System.Globalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #821 (DESIGN: Destructive action typed confirmation). A high-impact local destructive action asks the operator
// to type the affected count; wrong text, a count that moved, and a live marketplace write are all refused.
[TestClass]
public sealed class DestructiveConfirmationTests
{
    static readonly DestructiveIntent Twelve = new("Sil", "seçili ürünler", 12);

    [TestMethod]
    public void ThePhraseIsTheCountAndTheMessageSaysCountScopeAndVerb()
    {
        Assert.AreEqual("12", DestructiveConfirmation.Phrase(Twelve));
        var message = DestructiveConfirmation.Message(Twelve);
        StringAssert.Contains(message, 12.ToString("N0", CultureInfo.CurrentCulture));
        StringAssert.Contains(message, "seçili ürünler");
        StringAssert.Contains(message, "sil");
        StringAssert.Contains(message, "geri alınamaz");
        Assert.IsTrue(DestructiveConfirmation.RequiresTyping(Twelve));
        Assert.IsFalse(DestructiveConfirmation.RequiresTyping(Twelve with { AffectedCount = 1 }), "One record needs the ordinary destructive confirm, not typing.");
    }

    [TestMethod]
    public void OnlyTheExactPhraseIsAccepted()
    {
        Assert.IsTrue(DestructiveConfirmation.Validate(Twelve, "12", 12).Allowed);
        Assert.IsTrue(DestructiveConfirmation.Validate(Twelve, " 12 ", 12).Allowed, "Surrounding whitespace is not a mistake.");
        foreach (var wrong in new[] { null, "", "1", "13", "on iki", "12a", "SİL" })
        {
            var verdict = DestructiveConfirmation.Validate(Twelve, wrong, 12);
            Assert.IsFalse(verdict.Allowed, $"'{wrong}' must not confirm.");
            StringAssert.Contains(verdict.Reason, "12");
        }
    }

    [TestMethod]
    public void ACountThatMovedSinceTheDialogOpenedIsRefusedWithBothNumbers()
    {
        var verdict = DestructiveConfirmation.Validate(Twelve, "12", 9);

        Assert.IsFalse(verdict.Allowed, "The operator confirmed twelve; nine is a different action.");
        StringAssert.Contains(verdict.Reason, 12.ToString("N0", CultureInfo.CurrentCulture));
        StringAssert.Contains(verdict.Reason, 9.ToString("N0", CultureInfo.CurrentCulture));
        StringAssert.Contains(verdict.Reason, "önizle");
        Assert.IsFalse(DestructiveConfirmation.Validate(Twelve, "12", 13).Allowed, "More is as wrong as fewer.");
    }

    [TestMethod]
    public void CancelIsNotAConfirmationAndASingleRecordSkipsTyping()
    {
        Assert.IsFalse(DestructiveConfirmation.Validate(Twelve, null, 12).Allowed, "Nothing typed is Cancel.");
        var single = Twelve with { AffectedCount = 1 };
        Assert.IsTrue(DestructiveConfirmation.Validate(single, null, 1).Allowed, "One record: the ordinary confirm already happened.");
        Assert.IsFalse(DestructiveConfirmation.Validate(single, null, 2).Allowed, "…but the count is still checked.");
    }

    [TestMethod]
    public void ALiveMarketplaceWriteIsRefusedOutrightByThisPattern()
    {
        var live = Twelve with { IsLocalOnly = false };

        var verdict = DestructiveConfirmation.Validate(live, "12", 12);

        Assert.IsFalse(verdict.Allowed, "A typed word must never stand in for preview, approval, revision and idempotency.");
        Assert.AreEqual(DestructiveConfirmation.LiveWriteRefused, verdict.Reason);
        Assert.ThrowsException<InvalidOperationException>(() => DestructiveConfirmDialog.Show(null, live, () => 12), "The dialog refuses to even open for a live write.");
    }
}
