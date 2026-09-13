using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #867 (DESIGN: Localization overflow regression). A shared audit that asks the layout itself whether a piece of
// text or a control was cut: a visible text block that is neither wrapped nor trimmed and got a layout clip, or a
// button, tab, header or check box that did, is a finding — with the owner, the sanitized text, and the DIP it
// needed against the DIP it had. Wrapping, an ellipsis and a scrollable host are not findings. The numbers are
// device-independent, so the same tree under a 1.25×, 1.5× or 2× scale reports the same findings.
[TestClass]
public sealed class OverflowAuditTests
{
    const string Long = "Şüpheli işlemlerin çözümlenmesi için özel üretim, çok uzun adlı, ölçülü ürün — sonbahar koleksiyonu";
    sealed record Row(string Name, decimal Price);

    [TestMethod]
    public void ClippedTextAndControlsAreFoundWrappedTrimmedAndScrollableTextIsNot()
    {
        RunSta(() =>
        {
            var clippedText = new TextBlock { Text = Long, TextWrapping = TextWrapping.NoWrap };
            var wrapped = new TextBlock { Text = Long, TextWrapping = TextWrapping.Wrap };
            var trimmed = new TextBlock { Text = Long, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
            var secret = new TextBlock { Text = "Hata: Authorization: Bearer abc123xyz · ali@example.com · " + Long, TextWrapping = TextWrapping.NoWrap };
            var button = new Button { Content = "Kaynak sağlığını kontrol et (tam okuma)", Width = 60 };
            var check = new CheckBox { Content = "Program açıkken otomatik havuz güncellemesi yapılsın", Width = 80 };
            var scrollable = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Width = 100, Content = new TextBlock { Text = Long, TextWrapping = TextWrapping.NoWrap } };
            var constrained = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Width = 100, Content = new TextBlock { Text = Long, TextWrapping = TextWrapping.NoWrap } };
            var grid = new DataGrid { AutoGenerateColumns = false, IsReadOnly = true, Width = 300, Height = 90, ItemsSource = new List<Row> { new("kısa", 1234567.89m) } };
            grid.Columns.Add(GridColumns.Text("Ad", "Name", 100)); grid.Columns.Add(GridColumns.Text("Fiyat", "Price", 30));
            var host = new StackPanel { Width = 100 };
            foreach (var child in new UIElement[] { clippedText, wrapped, trimmed, secret, button, check, scrollable, constrained })
                host.Children.Add(new Border { BorderThickness = new Thickness(0), Child = child });
            var root = new StackPanel(); root.Children.Add(host); root.Children.Add(grid);
            var window = new Window { Content = root, Width = 500, Height = 600, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
            window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = DesignTokens.Source });
            try
            {
                window.Show(); Drain(window);
                IReadOnlyList<OverflowFinding> findings = null;
                foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0 })
                {
                    root.LayoutTransform = scale == 1.0 ? Transform.Identity : new ScaleTransform(scale, scale); Drain(window);
                    var pass = OverflowAudit.Audit(root);
                    if (findings is null) findings = pass;
                    else CollectionAssert.AreEqual(findings.Select(f => f.ToString()).ToList(), pass.Select(f => f.ToString()).ToList(), $"the same DIP findings at {scale}x");
                }

                OverflowFinding Of(FrameworkElement element) => findings.Single(f => ReferenceEquals(f.Element, element) || (f.Element is TextBlock t && element is ContentControl c && IsInside(t, c)));
                var text = Of(clippedText); Assert.AreEqual(OverflowKind.ClippedText, text.Kind); Assert.IsTrue(text.Needed > text.Available, $"{text}"); Assert.AreEqual(100, text.Available, 1.0); StringAssert.StartsWith(text.Text, "Şüpheli");
                Assert.IsFalse(findings.Any(f => ReferenceEquals(f.Element, wrapped)), "wrapped text grows down, it is not cut");
                Assert.IsFalse(findings.Any(f => ReferenceEquals(f.Element, trimmed)), "an ellipsis is by design, not a cut");
                var hidden = Of(secret); Assert.IsFalse(hidden.Text.Contains("abc123xyz")); Assert.IsFalse(hidden.Text.Contains("ali@example.com")); Assert.IsTrue(hidden.Text.Length <= OverflowAudit.TextLimit + 1, "the finding's text is capped");
                var inButton = Of(button); StringAssert.Contains(inButton.Owner, "Button"); Assert.AreEqual(OverflowKind.ClippedText, inButton.Kind);
                StringAssert.Contains(Of(check).Owner, "CheckBox");
                Assert.IsFalse(findings.Any(f => f.Element is TextBlock t && IsInside(t, scrollable)), "a scrollable host is reachable, not cut");
                Assert.IsTrue(findings.Any(f => f.Element is TextBlock t && IsInside(t, constrained)), "a host that forbids scrolling cuts the text");
                var priceCell = findings.Single(f => f.Element is TextBlock t && IsInside(t, grid) && t.Text.Contains("567")); StringAssert.Contains(priceCell.Owner, "DataGridCell"); Assert.IsTrue(priceCell.Needed > priceCell.Available);
                Assert.IsFalse(findings.Any(f => f.Element is TextBlock t && t.Text == "kısa"), "a value that fits is not a finding");
                foreach (var finding in findings) { StringAssert.Contains(finding.ToString(), "DIP"); Assert.IsTrue(finding.Available >= 0 && finding.Needed > 0); }
            }
            finally { window.Close(); }
        });
    }

    static bool IsInside(DependencyObject node, DependencyObject ancestor)
    {
        for (var current = node; current is not null; current = VisualTreeHelper.GetParent(current)) if (ReferenceEquals(current, ancestor)) return true;
        return false;
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
