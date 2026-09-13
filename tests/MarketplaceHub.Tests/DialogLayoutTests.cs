using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #818 (DESIGN: Resizable dialog layout standard). Sizing is one rule in DIPs: fit the work area with a margin,
// never below a usable minimum, always resizable between the two. 200% DPI is just a smaller DIP work area.
[TestClass]
public sealed class DialogLayoutTests
{
    [TestMethod]
    public void ADialogFitsA1024x768ScreenWithAMargin()
    {
        // Windows' work area at 1024x768 with a taskbar is about 1024x728 DIPs; the Excel mapping dialog asks for 520x700.
        var size = DialogLayout.Fit(520, 700, 1024, 728);

        Assert.AreEqual(520, size.Width);
        Assert.AreEqual(728 - DialogLayout.WorkAreaMargin, size.Height, "A dialog taller than the work area is capped, not clipped.");
        Assert.AreEqual(1024 - DialogLayout.WorkAreaMargin, size.MaxWidth);
        Assert.AreEqual(728 - DialogLayout.WorkAreaMargin, size.MaxHeight);
        Assert.IsTrue(size.MinWidth <= size.Width && size.MinHeight <= size.Height, "The minimum never exceeds the fitted size.");
    }

    [TestMethod]
    public void At200PercentDpiTheSameRuleFitsTheSmallerDipWorkArea()
    {
        // A 1920x1080 display at 200% is 960x540 DIPs: the same rule, no pixel conversion anywhere.
        var size = DialogLayout.Fit(720, 560, 960, 540);

        Assert.AreEqual(720, size.Width);
        Assert.AreEqual(540 - DialogLayout.WorkAreaMargin, size.Height);
        Assert.IsTrue(size.MaxWidth < 960 && size.MaxHeight < 540);
    }

    [TestMethod]
    public void RequestsBelowTheUsableMinimumOrOutrightNonsenseAreLifted()
    {
        var tiny = DialogLayout.Fit(10, 10, 1920, 1040);
        Assert.AreEqual(DialogLayout.MinDialogWidth, tiny.Width);
        Assert.AreEqual(DialogLayout.MinDialogHeight, tiny.Height);

        var nan = DialogLayout.Fit(double.NaN, double.NaN, 1920, 1040);
        Assert.AreEqual(DialogLayout.MinDialogWidth, nan.Width);
        Assert.AreEqual(DialogLayout.MinDialogHeight, nan.Height);

        var absurdScreen = DialogLayout.Fit(800, 600, 100, 100);
        Assert.IsTrue(absurdScreen.Width >= DialogLayout.MinDialogWidth && absurdScreen.Height >= DialogLayout.MinDialogHeight, "A reported work area smaller than the minimum still yields a usable dialog.");
    }

    [TestMethod]
    public void TitlesAndMessagesNeverCarryASecret()
    {
        var safe = DialogLayout.SafeText("Dışa aktarım · Authorization: Bearer abc.def · ali@example.com · C:\\Users\\serif\\out.xlsx");
        foreach (var forbidden in new[] { "abc.def", "ali@example.com", "\\serif\\" })
            Assert.IsFalse(safe.Contains(forbidden, StringComparison.Ordinal), $"'{forbidden}' reached a modal: {safe}");
        Assert.AreEqual("", DialogLayout.SafeText(null));
    }

    [TestMethod]
    public void LongValidationTextIsRedactedButNeverTruncated()
    {
        // The audit sanitizer caps rows; a modal must show the whole instruction, wrapped and scrolled, not a cut.
        var sentence = "Ürün adı zorunlu ve en fazla 200 karakter olabilir; para birimi üç harfli bir kod olmalı. ";
        var text = string.Concat(Enumerable.Repeat(sentence, 60)) + "Authorization: Bearer abc.def.ghi";

        var safe = DialogLayout.SafeText(text);

        Assert.IsTrue(safe.Length >= 60 * sentence.Length - 1, $"The text was cut to {safe.Length} characters.");
        Assert.IsFalse(safe.Contains("abc.def.ghi"), "Redaction still applies to the whole text.");
        StringAssert.Contains(safe, "[redacted]");
    }
}
