using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #859 (DESIGN SYSTEM: Spacing scale tokens). The four-step scale (inline 4, control 8, section 12, page 20, plus
// the 2-DIP hairline) is the only source of the layout rhythm: the page, section, inline and control margins, the
// directional gaps, the chip padding and the three text-block rhythms all derive from it and live in the token
// file. The numbers are device-independent: a 200 % display scales the host, not the margins.
[TestClass]
public sealed class SpacingTests
{
    [TestMethod]
    public void TheLayoutRhythmDerivesFromTheScaleAndTheTokenFileCarriesIt()
    {
        RunSta(() =>
        {
            DesignTokens.Verify(DesignTokens.Resources);
            foreach (var key in new[] { "PageMargin", "SectionMargin", "InlineMargin", "ChipPadding", "TitleBlockMargin", "HintBlockMargin", "BodyBlockMargin" })
                Assert.IsTrue(DesignTokens.Required.Any(r => r.Key == key && r.Type == typeof(Thickness)), key);

            Assert.AreEqual(new Thickness(20), Spacing.Page); Assert.AreEqual(new Thickness(12), Spacing.Section); Assert.AreEqual(new Thickness(4), Spacing.Inline); Assert.AreEqual(new Thickness(3), Spacing.Control);
            Assert.AreEqual(new Thickness(8, 2, 8, 2), Spacing.Chip);
            Assert.AreEqual(new Thickness(4, 8, 4, 12), Spacing.TitleBlock); Assert.AreEqual(new Thickness(4, 8, 4, 8), Spacing.HintBlock); Assert.AreEqual(new Thickness(3, 4, 3, 7), Spacing.BodyBlock);
            Assert.AreEqual(new Thickness(0, 0, 0, 12), Spacing.BelowSection); Assert.AreEqual(new Thickness(0, 0, 0, 8), Spacing.BelowControl); Assert.AreEqual(new Thickness(0, 0, 0, 4), Spacing.BelowInline);
            Assert.AreEqual(new Thickness(0, 4, 0, 0), Spacing.AboveInline); Assert.AreEqual(new Thickness(0, 8, 0, 0), Spacing.AboveControl); Assert.AreEqual(new Thickness(0, 8, 0, 8), Spacing.VerticalControl);
            Assert.AreEqual(new Thickness(0, 0, 4, 0), Spacing.RightInline); Assert.AreEqual(new Thickness(8, 0, 0, 0), Spacing.LeftControl);

            // Every margin is the scale, not a number of its own.
            Assert.AreEqual(DesignTokens.SpacePage, Spacing.Page.Left); Assert.AreEqual(DesignTokens.SpaceSection, Spacing.Section.Top); Assert.AreEqual(DesignTokens.SpaceInline, Spacing.Inline.Right);
            Assert.AreEqual(DesignTokens.SpaceSection, Spacing.BelowSection.Bottom); Assert.AreEqual(DesignTokens.SpaceControl, Spacing.BelowControl.Bottom); Assert.AreEqual(DesignTokens.SpaceInline, Spacing.BelowInline.Bottom);
            Assert.AreEqual(DesignTokens.SpaceControl, Spacing.TitleBlock.Top); Assert.AreEqual(DesignTokens.SpaceSection, Spacing.TitleBlock.Bottom); Assert.AreEqual(DesignTokens.SpaceInline, Spacing.TitleBlock.Left);
            Assert.AreEqual(DesignTokens.SpaceControl, Spacing.Chip.Left); Assert.AreEqual(DesignTokens.SpaceHairline, Spacing.Chip.Top);

            // 200 % scale: a page keeps its DIP margin; the host around it is what doubles.
            var page = new StackPanel { Margin = Spacing.Page, Children = { new Border { Width = 100, Height = 50 } } };
            var host = new Border { Child = page, LayoutTransform = new ScaleTransform(2, 2) };
            host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.AreEqual(new Thickness(20), page.Margin); Assert.AreEqual(140, page.DesiredSize.Width, 0.5); Assert.AreEqual(280, host.DesiredSize.Width, 0.5);
        });
    }

    static void RunSta(Action body)
    {
        Exception failure = null;
        var thread = new Thread(() => { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); try { body(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new AssertFailedException(failure.ToString());
    }
}
