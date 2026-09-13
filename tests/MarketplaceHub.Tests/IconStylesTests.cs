using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #860 (DESIGN SYSTEM: Icon sizing and alignment tokens). The app draws its icons as text glyphs; the standard gives
// them a size per role (inline, status, toolbar, navigation), keeps a glyph on the same baseline as the label it
// sits beside, gives icon buttons a square minimum with centred content, refuses an icon-only button without an
// accessible name, and dims a disabled icon button so the state is visible. The numbers are device-independent.
[TestClass]
public sealed class IconStylesTests
{
    [TestMethod]
    public void RolesSizeGlyphsFromTheTokensAndKeepThemOnTheTextBaseline()
    {
        RunSta(() =>
        {
            DesignTokens.Verify(DesignTokens.Resources);
            foreach (var key in new[] { "IconSizeInline", "IconSizeToolbar", "IconSizeNavigation", "IconButtonMinSize", "ToolbarButtonMinSize" })
                Assert.IsTrue(DesignTokens.Required.Any(r => r.Key == key && r.Type == typeof(double)), key);
            Assert.AreEqual(13d, DesignTokens.IconSizeInline); Assert.AreEqual(16d, DesignTokens.IconSizeToolbar); Assert.AreEqual(14d, DesignTokens.IconSizeNavigation);
            Assert.AreEqual(26d, DesignTokens.IconButtonMinSize); Assert.AreEqual(32d, DesignTokens.ToolbarButtonMinSize);

            Assert.AreEqual(13d, IconStyles.Size(IconRole.Inline)); Assert.AreEqual(13d, IconStyles.Size(IconRole.Status)); Assert.AreEqual(16d, IconStyles.Size(IconRole.Toolbar)); Assert.AreEqual(14d, IconStyles.Size(IconRole.Navigation));
            Assert.AreEqual(26d, IconStyles.ButtonMinSize(IconRole.Inline)); Assert.AreEqual(32d, IconStyles.ButtonMinSize(IconRole.Toolbar));

            // A glyph run beside a label: one text block, one baseline; the glyph sized for its role, the label at body size.
            var mixed = IconStyles.WithText("⚠", "Uyarı", IconRole.Status, Brushes.DarkOrange);
            var runs = mixed.Inlines.OfType<Run>().ToList();
            Assert.AreEqual(3, runs.Count); Assert.AreEqual("⚠", runs[0].Text); Assert.AreEqual(IconStyles.Size(IconRole.Status), runs[0].FontSize); Assert.AreEqual(BaselineAlignment.Baseline, runs[0].BaselineAlignment); Assert.AreSame(Brushes.DarkOrange, runs[0].Foreground);
            Assert.AreEqual("Uyarı", runs[2].Text); Assert.AreEqual(DependencyProperty.UnsetValue, runs[2].ReadLocalValue(TextElement.FontSizeProperty), "The label keeps the block's size.");
            var alone = IconStyles.GlyphRun("✔", IconRole.Toolbar); Assert.AreEqual(16d, alone.FontSize); Assert.AreEqual(BaselineAlignment.Baseline, alone.BaselineAlignment);

            // A glyph on its own: centred in a square of its size.
            var glyph = IconStyles.Glyph("☰", IconRole.Toolbar);
            Assert.AreEqual(16d, glyph.FontSize); Assert.AreEqual(TextAlignment.Center, glyph.TextAlignment); Assert.AreEqual(VerticalAlignment.Center, glyph.VerticalAlignment); Assert.IsTrue(glyph.MinWidth >= 16);

            // An icon button: square minimum, centred content, the role's glyph size, the content left as the caller's string.
            var close = new Button { Content = "✕" }; AutomationProperties.SetName(close, "Bildirimi kapat");
            IconStyles.ApplyIconButton(close, IconRole.Inline);
            Assert.AreEqual("✕", close.Content); Assert.AreEqual(26d, close.MinWidth); Assert.AreEqual(26d, close.MinHeight); Assert.AreEqual(13d, close.FontSize);
            Assert.AreEqual(HorizontalAlignment.Center, close.HorizontalContentAlignment); Assert.AreEqual(VerticalAlignment.Center, close.VerticalContentAlignment); Assert.AreEqual(Spacing.Chip, close.Padding);
            var toolbar = IconStyles.ApplyIconButton(new Button { Content = "»" , ToolTip = "Menüyü genişlet" }, IconRole.Toolbar, name: "Menüyü genişlet");
            Assert.AreEqual(32d, toolbar.MinWidth); Assert.AreEqual(16d, toolbar.FontSize); Assert.AreEqual("Menüyü genişlet", AutomationProperties.GetName(toolbar));

            // Icon-only without a spoken name is refused; a mixed glyph-and-label button speaks for itself.
            Assert.ThrowsException<ArgumentException>(() => IconStyles.ApplyIconButton(new Button { Content = "✕" }, IconRole.Inline));
            var back = IconStyles.ApplyIconButton(new Button { Content = "‹ Geri" }, IconRole.Inline);
            Assert.AreEqual("‹ Geri", back.Content); Assert.AreEqual(26d, back.MinHeight); Assert.AreEqual(DependencyProperty.UnsetValue, back.ReadLocalValue(Control.FontSizeProperty), "A mixed button keeps the body size so its label matches the text around it.");

            // Disabled is visible: the button dims and comes back.
            back.IsEnabled = false; Assert.AreEqual(IconStyles.DisabledOpacity, back.Opacity); back.IsEnabled = true; Assert.AreEqual(1d, back.Opacity);

            // 200 % scale: the square stays 26 DIP; the host is what doubles.
            var host = new Border { Child = close, LayoutTransform = new ScaleTransform(2, 2) };
            host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Assert.AreEqual(26d, close.MinWidth); Assert.IsTrue(close.DesiredSize.Width >= 26 && close.DesiredSize.Width < 40, $"{close.DesiredSize.Width}"); Assert.AreEqual(close.DesiredSize.Width * 2, host.DesiredSize.Width, 0.5);
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
