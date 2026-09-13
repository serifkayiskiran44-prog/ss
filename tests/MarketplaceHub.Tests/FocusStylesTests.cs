using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #861 (DESIGN SYSTEM: Focus visual tokens). One keyboard focus ring for every control: a two-tone rectangle (a dark
// outer stroke and a light inner stroke, so it reads on the teal buttons, the white pages and the dark rail alike)
// drawn from four tokens, outside the control's bounds, pixel-snapped so it stays crisp at 150 %; under high
// contrast the system's text and window colours. WPF shows a FocusVisualStyle only for keyboard focus, so a mouse
// click keeps the control's own state and the keyboard gets the ring. A disabled control cannot take focus at all.
[TestClass]
public sealed class FocusStylesTests
{
    [TestMethod]
    public void TheRingIsBuiltFromTheTokensReadsInHighContrastAndAppliesToCodeBuiltSurfaces()
    {
        RunSta(() =>
        {
            DesignTokens.Verify(DesignTokens.Resources);
            Assert.IsTrue(DesignTokens.Required.Any(r => r.Key == "FocusRingColor" && r.Type == typeof(Color)) && DesignTokens.Required.Any(r => r.Key == "FocusRingInnerColor" && r.Type == typeof(Color)));
            Assert.IsTrue(DesignTokens.Required.Any(r => r.Key == "FocusRingThickness" && r.Type == typeof(double)) && DesignTokens.Required.Any(r => r.Key == "FocusRingInnerThickness" && r.Type == typeof(double)));
            Assert.AreEqual(2d, DesignTokens.FocusRingThickness); Assert.AreEqual(1d, DesignTokens.FocusRingInnerThickness);
            Assert.AreEqual(Color.FromRgb(23, 54, 70), DesignTokens.FocusRingColor); Assert.AreEqual(Colors.White, DesignTokens.FocusRingInnerColor);
            Assert.IsTrue(DesignTokens.Resources.Contains(FocusStyles.KeyboardFocusVisualKey), "The token file carries the ring as a style for XAML.");

            // The ring: a style for the focus adorner whose template draws the two strokes outside the bounds, pixel-snapped.
            var style = FocusStyles.Create(highContrast: false);
            Assert.AreEqual(typeof(Control), style.TargetType);
            var template = (ControlTemplate)style.Setters.OfType<Setter>().Single(s => s.Property == Control.TemplateProperty).Value;
            var root = (FrameworkElement)template.LoadContent();
            var rectangles = Descendants(root).OfType<Rectangle>().ToList();
            Assert.AreEqual(2, rectangles.Count, "An outer and an inner stroke.");
            var outer = rectangles[0]; var inner = rectangles[1];
            Assert.AreEqual(DesignTokens.FocusRingThickness, outer.StrokeThickness); Assert.AreEqual(DesignTokens.FocusRingColor, ((SolidColorBrush)outer.Stroke).Color); Assert.AreEqual(new Thickness(-DesignTokens.FocusRingThickness), outer.Margin);
            Assert.AreEqual(DesignTokens.FocusRingInnerThickness, inner.StrokeThickness); Assert.AreEqual(DesignTokens.FocusRingInnerColor, ((SolidColorBrush)inner.Stroke).Color);
            Assert.IsTrue(rectangles.All(r => r.SnapsToDevicePixels && !r.IsHitTestVisible && r.Fill is null), "Crisp at any scale, never in the way of the mouse, never a fill.");

            // High contrast: the system's colours, the same shape.
            var hc = FocusStyles.Create(highContrast: true);
            var hcRoot = (FrameworkElement)((ControlTemplate)hc.Setters.OfType<Setter>().Single(s => s.Property == Control.TemplateProperty).Value).LoadContent();
            var hcRectangles = Descendants(hcRoot).OfType<Rectangle>().ToList();
            Assert.AreEqual(SystemColors.WindowTextColor, ((SolidColorBrush)hcRectangles[0].Stroke).Color); Assert.AreEqual(SystemColors.WindowColor, ((SolidColorBrush)hcRectangles[1].Stroke).Color);

            // A code-built surface becomes a keyboard stop with the ring; a disabled one cannot take focus.
            var row = new Border { Child = new TextBlock { Text = "Satır" } };
            FocusStyles.MakeFocusable(row);
            Assert.IsTrue(row.Focusable && KeyboardNavigation.GetIsTabStop(row)); Assert.IsNotNull(row.FocusVisualStyle); Assert.AreEqual(typeof(Control), row.FocusVisualStyle.TargetType);
            var button = FocusStyles.Apply(new Button { Content = "Kaydet" }); Assert.IsNotNull(button.FocusVisualStyle);
            var window = new Window { Content = new StackPanel { Children = { row, button } }, Width = 300, Height = 200, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
            try
            {
                window.Show(); Drain(window);
                button.IsEnabled = false; Assert.IsFalse(button.Focus(), "A disabled control cannot take focus, so it never shows a ring.");
                button.IsEnabled = true; Assert.IsTrue(row.Focus()); Assert.IsTrue(row.IsKeyboardFocused);
            }
            finally { window.Close(); }

            // A settings banner row, built in code, carries the ring.
            var host = new StackPanel();
            SettingsValidationBanner.Render(host, new[] { new SettingsIssue(SettingsIssueKind.MissingSecret, "trendyol-connection", "Eksik", "detay", SeverityLevel.Blocking) }, false, _ => { }, () => { }, () => { });
            var bannerRow = Descendants(host).OfType<Border>().Single(b => (string?)b.Tag == "settings-issue");
            Assert.IsNotNull(bannerRow.FocusVisualStyle); Assert.IsTrue(bannerRow.Focusable);
        });
    }

    static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        var count = node is Visual ? VisualTreeHelper.GetChildrenCount(node) : (node is FrameworkElement ? 0 : 0);
        for (var i = 0; i < count; i++) { var child = VisualTreeHelper.GetChild(node, i); yield return child; foreach (var d in Descendants(child)) yield return d; }
        if (count == 0 && node is Panel panel) foreach (UIElement child in panel.Children) { yield return child; foreach (var d in Descendants(child)) yield return d; }
        else if (count == 0 && node is Decorator decorator && decorator.Child is not null) { yield return decorator.Child; foreach (var d in Descendants(decorator.Child)) yield return d; }
    }

    static void Drain(Window window) { window.UpdateLayout(); for (var i = 0; i < 4; i++) window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { })); }

    static void RunSta(Action body)
    {
        Exception failure = null;
        var thread = new Thread(() => { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); try { body(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new AssertFailedException(failure.ToString());
    }
}
