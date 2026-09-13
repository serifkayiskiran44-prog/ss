using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #855 (DESIGN: Connection secret masking controls). The secret row is a masked box that refuses copy and cut,
// cleans a paste to one line, says only whether a saved value exists, resolves to the typed value or the saved
// one, and keeps what was typed through a validation failure; it is labelled and a tab stop.
[TestClass]
public sealed class SecretFieldTests
{
    [TestMethod]
    public void PasteIsCleanedToOneLineAndTheRowResolvesTypedOrSaved()
    {
        Assert.AreEqual("abc123", SecretField.Clean("  abc123\r\n")); Assert.AreEqual("abc123", SecretField.Clean("\"abc123\"")); Assert.AreEqual("abc123", SecretField.Clean("'abc123'\n"));
        Assert.AreEqual("first-line", SecretField.Clean("\n\n  first-line  \nsecond-line\n")); Assert.AreEqual("", SecretField.Clean("   \n  ")); Assert.AreEqual("", SecretField.Clean(null));
        Assert.AreEqual(SecretField.MaxLength, SecretField.Clean(new string('x', 900)).Length);
        RunSta(() =>
        {
            var row = SecretField.Build("API secret", "Şifreli saklanır.");
            var window = new Window { Content = new StackPanel { Children = { row.Field.Root } }, Width = 600, Height = 300, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
            try
            {
                window.Show(); Drain(window);
                Assert.IsInstanceOfType(row.Box, typeof(PasswordBox), "Masked by construction; there is no reveal."); Assert.IsTrue(row.Box.IsTabStop && row.Box.Focusable);
                StringAssert.StartsWith(AutomationProperties.GetName(row.Box), "API secret"); StringAssert.Contains(AutomationProperties.GetName(row.Box), "zorunlu");
                Assert.AreEqual(SecretField.EmptyText, row.Presence.Text); Assert.IsFalse(row.HasSaved); Assert.IsFalse(row.HasTyped);
                Assert.AreEqual("", row.Resolve(null)); Assert.AreEqual("old-secret", row.Resolve("old-secret"), "An untouched box keeps the saved value.");

                row.SetSaved(true);
                Assert.AreEqual(SecretField.SavedText, row.Presence.Text); StringAssert.Contains(AutomationProperties.GetHelpText(row.Box), "Kayıtlı gizli değer var"); Assert.IsFalse(row.HasTyped);
                row.Box.Password = "new-secret";
                Assert.IsTrue(row.HasTyped); Assert.AreEqual("new-secret", row.Resolve("old-secret"), "A typed value wins.");

                // Copy and cut are refused; paste is the only way in besides typing.
                row.Box.Focus(); Drain(window);
                Assert.IsFalse(ApplicationCommands.Copy.CanExecute(null, row.Box)); Assert.IsFalse(ApplicationCommands.Cut.CanExecute(null, row.Box));
                // A paste runs through the cleaning handler -- raised directly, because Paste.CanExecute answers from the
                // machine's clipboard (full on a dev machine, empty on the CI runner) and says nothing about the row. A quoted,
                // multi-line dump lands as one clean line with the default paste cancelled; a non-text paste changes nothing.
                var pasting = new DataObjectPastingEventArgs(new DataObject(DataFormats.UnicodeText, "\"pasted-value\"\r\nsecond line\r\n"), false, DataFormats.UnicodeText);
                row.Box.RaiseEvent(pasting);
                Assert.IsTrue(pasting.CommandCancelled, "The default paste is replaced by the cleaned value."); Assert.AreEqual("pasted-value", row.Box.Password);
                var files = new DataObjectPastingEventArgs(new DataObject(DataFormats.FileDrop, new[] { "C:\\x.txt" }), false, DataFormats.FileDrop);
                row.Box.RaiseEvent(files);
                Assert.IsTrue(files.CommandCancelled); Assert.AreEqual("pasted-value", row.Box.Password, "A non-text paste changes nothing.");
                row.Box.Password = "new-secret";

                // A validation failure keeps what was typed and speaks in the row's own slot.
                row.Field.SetValidation("Trendyol API kimlik bilgileri geçersiz.");
                Assert.AreEqual(Visibility.Visible, row.Field.ValidationText.Visibility); Assert.IsTrue(row.HasTyped); Assert.AreEqual("new-secret", row.Resolve("old-secret"));
                row.Field.SetValidation(""); Assert.AreEqual(Visibility.Collapsed, row.Field.ValidationText.Visibility);

                // After a save the typed value leaves the box and only presence remains; after a delete, nothing.
                row.MarkSaved(); Assert.IsFalse(row.HasTyped); Assert.IsTrue(row.HasSaved); Assert.AreEqual("kept", row.Resolve("kept"));
                row.MarkCleared(); Assert.IsFalse(row.HasSaved); Assert.AreEqual(SecretField.EmptyText, row.Presence.Text);
                Assert.IsFalse(Descendants(row.Field.Root).OfType<TextBlock>().Any(t => t.Text.Contains("new-secret") || t.Text.Contains("old-secret")), "No text on the row ever carries a value.");
            }
            finally { window.Close(); }
        });
    }

    static void Drain(Window window) { window.UpdateLayout(); for (var i = 0; i < 4; i++) window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { })); }

    static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        var count = node is Visual ? VisualTreeHelper.GetChildrenCount(node) : 0;
        for (var i = 0; i < count; i++) { var child = VisualTreeHelper.GetChild(node, i); yield return child; foreach (var d in Descendants(child)) yield return d; }
    }

    static void RunSta(Action body)
    {
        Exception failure = null;
        var thread = new Thread(() => { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); try { body(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new AssertFailedException(failure.ToString());
    }
}
