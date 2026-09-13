using System;
using System.Linq;
using System.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #817 (DESIGN: Warning versus blocking visual semantics). A warning and a blocking error must differ in every
// channel at once -- glyph, word, border weight, call to action -- and only last in colour; under high contrast
// the colours are the system's; a mixed set rolls up to the blocking ones; a raw payload never becomes a headline.
[TestClass]
public sealed class SeverityStyleTests
{
    [TestMethod]
    public void WarningAndBlockingDifferInEveryChannelNotOnlyInColour()
    {
        var warning = SeverityStyle.For(SeverityLevel.Warning, highContrast: false);
        var blocking = SeverityStyle.For(SeverityLevel.Blocking, highContrast: false);

        Assert.AreNotEqual(warning.Glyph, blocking.Glyph, "A colour-blind operator tells them apart by glyph.");
        Assert.AreNotEqual(warning.Word, blocking.Word, "A screen reader tells them apart by word.");
        Assert.IsTrue(blocking.BorderWeight > warning.BorderWeight, "A monochrome display tells them apart by border weight.");
        Assert.AreNotEqual(warning.CallToAction, blocking.CallToAction, "A warning is reviewed; a blocking error is fixed.");
        Assert.AreNotEqual(warning.Accent, blocking.Accent);
        Assert.AreEqual("✖ Hata", blocking.Badge);
        Assert.AreEqual("⚠ Uyarı", warning.Badge);

        var all = Enum.GetValues<SeverityLevel>().Select(l => SeverityStyle.For(l, false)).ToArray();
        Assert.AreEqual(all.Length, all.Select(p => p.Glyph).Distinct().Count(), "Every level has its own glyph.");
        Assert.AreEqual(all.Length, all.Select(p => p.Word).Distinct().Count(), "Every level has its own word.");
        Assert.IsTrue(all.All(p => p.Glyph.Length > 0 && p.Word.Length > 0));
    }

    [TestMethod]
    public void HighContrastUsesTheSystemsColoursNeverTheRgbTable()
    {
        foreach (var level in Enum.GetValues<SeverityLevel>())
        {
            var normal = SeverityStyle.For(level, highContrast: false);
            var high = SeverityStyle.For(level, highContrast: true);
            Assert.AreEqual(normal.Glyph, high.Glyph, $"{level}: the non-colour signals do not change with the theme.");
            Assert.AreEqual(normal.Word, high.Word);
            Assert.AreEqual(normal.BorderWeight, high.BorderWeight);
            Assert.AreEqual(SystemColors.WindowColor, high.Surface, $"{level}: the surface is the system window colour under high contrast.");
        }
        Assert.AreEqual(SystemColors.HotTrackColor, SeverityStyle.For(SeverityLevel.Blocking, true).Accent);
        Assert.AreEqual(SystemColors.HighlightColor, SeverityStyle.For(SeverityLevel.Warning, true).Accent);
        Assert.IsTrue(SeverityStyle.AccentBrush(SeverityLevel.Blocking, true).IsFrozen, "Brushes are shared, so they are frozen.");
    }

    [TestMethod]
    public void AMixedSetRollsUpToTheBlockingOnesAndGivesThemTheCallToAction()
    {
        var mixed = SeverityStyle.Aggregate(new[]
        {
            (SeverityLevel.Warning, "Açıklama kısa."),
            (SeverityLevel.Blocking, "Ürün adı zorunlu."),
            (SeverityLevel.Info, "Görsel yok."),
            (SeverityLevel.Blocking, "Para birimi üç harfli olmalı."),
        });

        Assert.AreEqual(SeverityLevel.Blocking, mixed.Highest);
        Assert.AreEqual(2, mixed.BlockingCount);
        Assert.AreEqual(1, mixed.WarningCount);
        Assert.IsTrue(mixed.IsMixed);
        StringAssert.Contains(mixed.Headline, "2 engel");
        StringAssert.Contains(mixed.Headline, "1 uyarı");
        StringAssert.Contains(mixed.Headline, "Ürün adı zorunlu.", "The first blocking message leads the headline.");
        Assert.AreEqual("İlk engele git", mixed.CallToAction, "The action belongs to what blocks the save.");

        var warningsOnly = SeverityStyle.Aggregate(new[] { (SeverityLevel.Warning, "Açıklama kısa.") });
        Assert.AreEqual(SeverityLevel.Warning, warningsOnly.Highest);
        Assert.IsFalse(warningsOnly.HasBlocking);
        StringAssert.Contains(warningsOnly.Headline, "kaydedilebilir", "Warnings alone do not stop a save, and the headline says so.");
        Assert.AreEqual("Uyarıları gözden geçir", warningsOnly.CallToAction);

        var clean = SeverityStyle.Aggregate(Array.Empty<(SeverityLevel, string)>());
        Assert.AreEqual(SeverityLevel.Info, clean.Highest);
        Assert.AreEqual("", clean.CallToAction, "Nothing to do means no button.");
    }

    [TestMethod]
    public void TheLevelMappingsAgreeWithTheOwnersTheyReplace()
    {
        Assert.AreEqual(SeverityLevel.Blocking, SeverityStyle.FromValidation(ProductValidation.Blocking));
        Assert.AreEqual(SeverityLevel.Warning, SeverityStyle.FromValidation(ProductValidation.Warning));
        Assert.AreEqual(SeverityLevel.Info, SeverityStyle.FromValidation(ProductValidation.Info));
        Assert.AreEqual(SeverityLevel.Info, SeverityStyle.FromValidation("nonsense"), "An unknown severity is informational, never silently blocking.");
        Assert.AreEqual(SeverityLevel.Blocking, SeverityStyle.FromNotification(NotificationSeverity.Error));
        Assert.AreEqual(SeverityLevel.Warning, SeverityStyle.FromNotification(NotificationSeverity.Warning));
        Assert.AreEqual(SeverityLevel.Blocking, SeverityStyle.FromAnomaly(DashboardAnomalies.Critical));
        Assert.AreEqual(SeverityLevel.Blocking, SeverityStyle.Highest(new[] { SeverityLevel.Info, SeverityLevel.Blocking, SeverityLevel.Warning }));
        Assert.AreEqual(SeverityLevel.Info, SeverityStyle.Highest(Array.Empty<SeverityLevel>()));
    }

    [TestMethod]
    public void ARawPayloadOrASecretNeverBecomesAHeadline()
    {
        var payload = SeverityStyle.Aggregate(new[] { (SeverityLevel.Blocking, "{\"error\":\"invalid_grant\",\"error_description\":\"x\"}") });
        StringAssert.Contains(payload.Headline, StatusTooltip.RawPayloadHidden);
        Assert.IsFalse(payload.Headline.Contains("invalid_grant"));

        var secret = SeverityStyle.Aggregate(new[] { (SeverityLevel.Blocking, "Reddedildi: Authorization: Bearer abc.def · ali@example.com") });
        Assert.IsFalse(secret.Headline.Contains("abc.def") || secret.Headline.Contains("ali@example.com"), secret.Headline);

        var novel = SeverityStyle.Aggregate(new[] { (SeverityLevel.Warning, new string('u', 900)) });
        Assert.IsTrue(novel.Headline.Length < 200, "A headline is a line, not the finding in full.");
    }
}
