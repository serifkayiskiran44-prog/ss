using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #858 (DESIGN SYSTEM: Typography scale tokens). Seven text roles -- page title, section title, subsection title,
// body, hint, caption, numeric KPI, monospace -- read their size, weight and face from the one token file; the
// muted roles take a system colour under high contrast; every role wraps a long Turkish label instead of clipping
// it; the numbers are device-independent, so a 200 % scale happens outside the text, not inside the tokens.
[TestClass]
public sealed class TextStylesTests
{
    [TestMethod]
    public void RolesReadTheTokensAndTheTokenFileCarriesThem()
    {
        RunSta(() =>
        {
            DesignTokens.Verify(DesignTokens.Resources);
            Assert.IsTrue(DesignTokens.Required.Any(r => r.Key == "TextPageTitleSize") && DesignTokens.Required.Any(r => r.Key == "FontFamilyMono") && DesignTokens.Required.Any(r => r.Key == "FontWeightTitle"));
            Assert.AreEqual(25d, DesignTokens.TextPageTitleSize); Assert.AreEqual(20d, DesignTokens.TextSectionTitleSize); Assert.AreEqual(16d, DesignTokens.TextSubsectionTitleSize);
            Assert.AreEqual(13d, DesignTokens.TextBodySize); Assert.AreEqual(11d, DesignTokens.TextCaptionSize); Assert.AreEqual(25d, DesignTokens.TextKpiSize); Assert.AreEqual(13d, DesignTokens.TextMonoSize);
            Assert.AreEqual("Segoe UI", DesignTokens.FontFamilyBody.Source); StringAssert.StartsWith(DesignTokens.FontFamilyMono.Source, "Consolas");
            Assert.AreEqual(FontWeights.SemiBold, DesignTokens.FontWeightTitle); Assert.AreEqual(FontWeights.Bold, DesignTokens.FontWeightKpi);

            (double Size, FontWeight Weight, string Family)[] expected =
            {
                (25, FontWeights.SemiBold, "Segoe UI"), (20, FontWeights.SemiBold, "Segoe UI"), (16, FontWeights.SemiBold, "Segoe UI"),
                (13, FontWeights.Normal, "Segoe UI"), (13, FontWeights.Normal, "Segoe UI"), (11, FontWeights.Normal, "Segoe UI"), (25, FontWeights.Bold, "Segoe UI"), (13, FontWeights.Normal, "Consolas"),
            };
            var roles = new[] { TextRole.PageTitle, TextRole.SectionTitle, TextRole.SubsectionTitle, TextRole.Body, TextRole.Hint, TextRole.Caption, TextRole.Kpi, TextRole.Mono };
            for (var i = 0; i < roles.Length; i++)
            {
                var block = TextStyles.Create(roles[i], "Örnek");
                Assert.AreEqual(expected[i].Size, block.FontSize, roles[i].ToString()); Assert.AreEqual(expected[i].Weight, block.FontWeight, roles[i].ToString()); StringAssert.StartsWith(block.FontFamily.Source, expected[i].Family, roles[i].ToString());
                Assert.AreEqual(TextWrapping.Wrap, block.TextWrapping, "Every role wraps by default.");
            }

            // Apply keeps what the block already says and where it sits; it only sets the type.
            var existing = new TextBlock { Text = "Kalır", Margin = new Thickness(4, 8, 4, 6), Foreground = Brushes.DarkOrange };
            TextStyles.Apply(existing, TextRole.SectionTitle);
            Assert.AreEqual("Kalır", existing.Text); Assert.AreEqual(new Thickness(4, 8, 4, 6), existing.Margin); Assert.AreSame(Brushes.DarkOrange, existing.Foreground, "A title's colour is the caller's.");

            // The muted roles carry the design grey, and the system's grey-text colour under high contrast -- never a colour that vanishes on a black ground.
            var hint = TextStyles.Apply(new TextBlock(), TextRole.Hint, highContrast: false); Assert.AreEqual(TextStyles.MutedColor, ((SolidColorBrush)hint.Foreground).Color);
            var hintHc = TextStyles.Apply(new TextBlock(), TextRole.Hint, highContrast: true); Assert.AreEqual(SystemColors.GrayTextColor, ((SolidColorBrush)hintHc.Foreground).Color);
            var captionHc = TextStyles.Apply(new TextBlock(), TextRole.Caption, highContrast: true); Assert.AreEqual(SystemColors.GrayTextColor, ((SolidColorBrush)captionHc.Foreground).Color);
            var bodyHc = TextStyles.Apply(new TextBlock(), TextRole.Body, highContrast: true); Assert.AreEqual(DependencyProperty.UnsetValue, bodyHc.ReadLocalValue(TextBlock.ForegroundProperty), "Body text inherits the surface's colour.");

            // A monospace input keeps its own text and takes the face and size.
            var box = new TextBox { Text = "x * 1.40 + 100" }; TextStyles.ApplyMono(box);
            StringAssert.StartsWith(box.FontFamily.Source, "Consolas"); Assert.AreEqual(DesignTokens.TextMonoSize, box.FontSize); Assert.AreEqual("x * 1.40 + 100", box.Text);
        });
    }

    [TestMethod]
    public void LongTurkishLabelsWrapAndTheNumbersAreDeviceIndependent()
    {
        RunSta(() =>
        {
            const string title = "Şüpheli işlemlerin çözümlenmesi, iade uzlaşması ve kargo istisnaları için kanal bazlı yapılandırma özeti";
            foreach (var role in new[] { TextRole.PageTitle, TextRole.SectionTitle, TextRole.Body, TextRole.Caption })
            {
                var block = TextStyles.Create(role, title);
                block.Measure(new Size(320, double.PositiveInfinity));
                var oneLine = TextStyles.Create(role, "Kısa"); oneLine.Measure(new Size(320, double.PositiveInfinity));
                Assert.IsTrue(block.DesiredSize.Width <= 320.5, $"{role}: {block.DesiredSize.Width} wider than its host.");
                Assert.IsTrue(block.DesiredSize.Height >= oneLine.DesiredSize.Height * 2, $"{role}: a long label takes more than one line instead of being cut.");
            }

            // 200 % scale: the text keeps its DIP measure; the scale is applied by the host around it.
            var text = TextStyles.Create(TextRole.Body, title);
            var host = new Border { Child = text, LayoutTransform = new ScaleTransform(2, 2) };
            host.Measure(new Size(640, double.PositiveInfinity));
            var plain = TextStyles.Create(TextRole.Body, title); plain.Measure(new Size(320, double.PositiveInfinity));
            Assert.AreEqual(plain.DesiredSize.Height, text.DesiredSize.Height, 0.5, "The text measures the same DIP height under a 2× layout scale.");
            Assert.AreEqual(plain.DesiredSize.Height * 2, host.DesiredSize.Height, 1.0, "The host is what doubles.");
            Assert.AreEqual(13d, text.FontSize, "A token is a number of DIP, never scaled by the host.");
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
