using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #863 (DESIGN SYSTEM: Form control height consistency). The form controls share one standard: an input (TextBox,
// PasswordBox, ComboBox, DatePicker) is at least the control height with the input padding; a button and a check
// box are at least the hit-target minimum; the standard lives in the token file so a dialog window resolves the
// same styles as the main window; the numbers are device-independent, and a long value never changes a control's
// height (a single-line input scrolls, a label wraps).
[TestClass]
public sealed class FormControlStylesTests
{
    [TestMethod]
    public void TheTokenFileCarriesTheControlStandardAndADialogResolvesIt()
    {
        RunSta(() =>
        {
            DesignTokens.Verify(DesignTokens.Resources);
            Assert.IsTrue(DesignTokens.Required.Any(r => r.Key == "HitTargetMinSize" && r.Type == typeof(double)));
            Assert.AreEqual(24d, DesignTokens.HitTargetMinSize); Assert.AreEqual(30d, DesignTokens.ControlMinHeight);

            var tokens = new ResourceDictionary { Source = DesignTokens.Source };
            foreach (var (type, minHeight) in new[] { (typeof(TextBox), 30d), (typeof(PasswordBox), 30d), (typeof(ComboBox), 30d), (typeof(DatePicker), 30d), (typeof(Button), 24d), (typeof(CheckBox), 24d) })
            {
                Assert.IsTrue(tokens.Contains(type), $"{type.Name}: the token file carries the shared style.");
                var style = (Style)tokens[type];
                Assert.AreEqual(minHeight, Setter(style, FrameworkElement.MinHeightProperty), $"{type.Name}: minimum height");
                Assert.IsNotNull(style.Setters.OfType<Setter>().SingleOrDefault(s => s.Property == FrameworkElement.FocusVisualStyleProperty), $"{type.Name}: the keyboard ring");
            }
            Assert.AreEqual(DesignTokens.InputPadding, Setter((Style)tokens[typeof(DatePicker)], Control.PaddingProperty));
            Assert.AreEqual(VerticalAlignment.Center, Setter((Style)tokens[typeof(CheckBox)], Control.VerticalContentAlignmentProperty));

            // A dialog built by the shell resolves the same standard without the main window and without an Application.
            var body = new StackPanel { Children = { new TextBox { Text = "x" }, new ComboBox(), new CheckBox { Content = "Seçenek" }, new DatePicker() } };
            var dialog = DialogShell.Create(null, "Deneme", body, new[] { new DialogShell.Action("Kapat", false, true, null) }, 400, 300, false);
            try
            {
                dialog.Show(); Drain(dialog);
                var box = Descendants(dialog).OfType<TextBox>().First(); var combo = Descendants(dialog).OfType<ComboBox>().First(); var check = Descendants(dialog).OfType<CheckBox>().First(); var date = Descendants(dialog).OfType<DatePicker>().First();
                Assert.AreEqual(DesignTokens.ControlMinHeight, box.MinHeight); Assert.AreEqual(DesignTokens.ControlMinHeight, combo.MinHeight); Assert.AreEqual(DesignTokens.ControlMinHeight, date.MinHeight);
                Assert.IsTrue(box.ActualHeight >= DesignTokens.ControlMinHeight && combo.ActualHeight >= DesignTokens.ControlMinHeight && date.ActualHeight >= DesignTokens.ControlMinHeight);
                Assert.IsTrue(check.ActualHeight >= DesignTokens.HitTargetMinSize, $"A check box is a {check.ActualHeight}-DIP target.");
                var button = Descendants(dialog).OfType<Button>().First(b => b.Content as string == "Kapat"); Assert.IsTrue(button.ActualHeight >= DesignTokens.HitTargetMinSize);

                // A long value never changes a single-line input's height; the box scrolls instead.
                var height = box.ActualHeight; box.Text = new string('Ş', 400); Drain(dialog);
                Assert.AreEqual(height, box.ActualHeight, 0.5, "A long value scrolls inside the box; the row does not grow.");
            }
            finally { dialog.Close(); }

            // 200 % scale: the heights are DIP; the host doubles.
            var scaled = new TextBox { Text = "x", MinHeight = DesignTokens.ControlMinHeight };
            var host = new Border { Child = scaled, LayoutTransform = new ScaleTransform(2, 2) };
            host.Measure(new Size(200, double.PositiveInfinity));
            Assert.AreEqual(DesignTokens.ControlMinHeight, scaled.DesiredSize.Height, 0.5); Assert.AreEqual(DesignTokens.ControlMinHeight * 2, host.DesiredSize.Height, 1.0);
        });
    }

    static object Setter(Style style, DependencyProperty property) => style.Setters.OfType<Setter>().Single(s => s.Property == property).Value;

    static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        var count = node is Visual ? VisualTreeHelper.GetChildrenCount(node) : 0;
        for (var i = 0; i < count; i++) { var child = VisualTreeHelper.GetChild(node, i); yield return child; foreach (var d in Descendants(child)) yield return d; }
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
