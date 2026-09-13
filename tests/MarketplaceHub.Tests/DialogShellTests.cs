using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #818's shell in a real (owned, closed) window: the body scrolls, the button bar stays put under it, the
// primary action is Enter and cancel is Escape -- reversed for a destructive dialog -- and a wall of validation
// text wraps and scrolls instead of widening the dialog.
[TestClass]
public sealed class DialogShellTests
{
    [TestMethod]
    public void TheShellScrollsItsBodyKeepsItsButtonBarAndWiresEnterAndEscape()
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            Window window = null;
            try
            {
                var longText = string.Join(" ", Enumerable.Repeat("Ürün adı zorunlu ve en fazla 200 karakter olabilir; SKU veya barkoddan en az biri zorunlu; para birimi üç harfli bir kod olmalı.", 40));
                var body = new StackPanel();
                body.Children.Add(DialogShell.Message(longText));
                var confirmed = false;
                window = DialogShell.Create(null, "Önizleme · Authorization: Bearer abc.def", body,
                    new DialogShell.Action[] { new("Vazgeç", IsCancel: true), new("Uygula", IsPrimary: true, OnClick: () => { confirmed = true; return true; }) }, 620, 480);
                window.Left = -4000; window.Top = -4000; window.ShowInTaskbar = false;
                window.Show(); Drain(window);

                Assert.IsFalse(window.Title.Contains("abc.def"), "A modal title never carries a token.");
                Assert.AreEqual(ResizeMode.CanResize, window.ResizeMode);
                Assert.IsTrue(window.MinWidth >= DialogLayout.MinDialogWidth && window.MinHeight >= DialogLayout.MinDialogHeight);
                Assert.IsTrue(window.MaxHeight <= SystemParameters.WorkArea.Height, "The dialog cannot outgrow the work area.");
                Assert.IsTrue(window.ActualWidth <= 620 + 1, $"Long text must not widen the dialog: {window.ActualWidth}");

                var root = (DockPanel)window.Content;
                var scroll = root.Children.OfType<ScrollViewer>().Single();
                var bar = root.Children.OfType<StackPanel>().Single();
                Assert.AreEqual(Dock.Bottom, DockPanel.GetDock(bar), "The button bar is docked under the body, never inside the scroll.");
                Assert.IsTrue(scroll.ScrollableHeight > 0, $"Forty lines of validation text scroll instead of pushing the buttons off screen (extent {scroll.ExtentHeight}, viewport {scroll.ViewportHeight}, scroll {scroll.ActualWidth}x{scroll.ActualHeight}, window {window.ActualWidth}x{window.ActualHeight}, body {body.ActualHeight}).");

                var buttons = bar.Children.OfType<Button>().ToArray();
                var primary = buttons.Single(b => (string)b.Content == "Uygula");
                var cancel = buttons.Single(b => (string)b.Content == "Vazgeç");
                Assert.IsTrue(primary.IsDefault, "Enter runs the primary action.");
                Assert.IsTrue(cancel.IsCancel, "Escape cancels.");
                Assert.IsFalse(cancel.IsDefault);
                Assert.AreEqual("Uygula", AutomationProperties.GetName(primary));

                primary.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Drain(window);
                Assert.IsTrue(confirmed, "The primary action ran.");
                Assert.IsFalse(window.IsVisible, "Confirming closes the dialog.");

                // Destructive: Enter goes to Cancel so a stray keystroke cannot confirm.
                var destructive = DialogShell.Create(null, "Ürünü sil", DialogShell.Message("3 ürün silinecek."),
                    new DialogShell.Action[] { new("Vazgeç", IsCancel: true), new("Sil", IsPrimary: true) }, 460, 240, destructive: true);
                destructive.Left = -4000; destructive.Top = -4000; destructive.Show(); Drain(destructive);
                var dBar = ((DockPanel)destructive.Content).Children.OfType<StackPanel>().Single();
                Assert.IsTrue(dBar.Children.OfType<Button>().Single(b => (string)b.Content == "Vazgeç").IsDefault, "Destructive: Enter is Cancel.");
                Assert.IsFalse(dBar.Children.OfType<Button>().Single(b => (string)b.Content == "Sil").IsDefault);
                destructive.Close(); Drain(destructive);
            }
            catch (Exception ex) { failure = ex; }
            finally { try { if (window is { IsVisible: true }) window.Close(); } catch (Exception) { } }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new AssertFailedException(failure.ToString());
    }

    static void Drain(Window window) { window.UpdateLayout(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
}
