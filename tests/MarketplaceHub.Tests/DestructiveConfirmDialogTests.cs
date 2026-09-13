using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #821's dialog in a real owned window: the confirm button is dead until the phrase is typed (so Enter cannot
// confirm by reflex), Escape cancels, a count that moved at the moment of confirmation keeps the dialog open
// with the reason, and the exact phrase with a steady count confirms.
[TestClass]
public sealed class DestructiveConfirmDialogTests
{
    [TestMethod]
    public void TheButtonWakesOnlyForTheExactPhraseAndAStaleCountKeepsTheDialogOpen()
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            Window window = null;
            try
            {
                var intent = new DestructiveIntent("Pasife al", "filtrelenen ürünler", 40);
                var current = 40;
                window = DestructiveConfirmDialog.Build(null, intent, () => current, out var typed);
                window.Left = -4000; window.Top = -4000; window.ShowInTaskbar = false;
                window.Show(); Drain(window);

                var bar = ((DockPanel)window.Content).Children.OfType<StackPanel>().Single();
                var confirm = bar.Children.OfType<Button>().Single(b => (string)b.Content == "Pasife al");
                var cancel = bar.Children.OfType<Button>().Single(b => (string)b.Content == "Vazgeç");
                Assert.IsFalse(confirm.IsEnabled, "Nothing typed: the destructive button is dead.");
                Assert.IsFalse(confirm.IsDefault, "…and Enter does not reach it.");
                Assert.IsTrue(cancel.IsCancel, "Escape cancels.");

                typed.Text = "4"; Drain(window);
                Assert.IsFalse(confirm.IsEnabled, "A partial phrase does not wake the button.");
                typed.Text = "40"; Drain(window);
                Assert.IsTrue(confirm.IsEnabled, "The exact phrase wakes it.");
                Assert.IsTrue(confirm.IsDefault, "…and only then is it Enter.");

                // The count moves between opening and confirming: refused, dialog stays open, reason shown.
                current = 37;
                confirm.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Drain(window);
                Assert.IsTrue(window.IsVisible, "A stale count does not close the dialog as if confirmed.");
                var feedback = ((StackPanel)((ScrollViewer)((DockPanel)window.Content).Children.OfType<ScrollViewer>().Single()).Content).Children.OfType<TextBlock>().Last();
                Assert.AreEqual(Visibility.Visible, feedback.Visibility);
                StringAssert.Contains(feedback.Text, "37");
                Assert.IsFalse(confirm.IsEnabled, "After a refusal the phrase must be typed again.");

                // Steady count and the phrase retyped: confirmed.
                current = 40; typed.Text = ""; typed.Text = "40"; Drain(window);
                Assert.IsTrue(confirm.IsEnabled);
                confirm.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Drain(window);
                Assert.IsFalse(window.IsVisible, "Confirmed: the dialog closes.");
            }
            catch (Exception ex) { failure = ex; }
            finally { try { if (window is { IsVisible: true }) window.Close(); } catch (Exception) { } }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new AssertFailedException(failure.ToString());
    }

    static void Drain(Window window) { window.UpdateLayout(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
}
