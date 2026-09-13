using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #855 on the real connection forms, in a temporary directory that never touches the profile's stores: a missing
// secret is refused in the row's own slot; a validation failure keeps what was typed and writes nothing; a valid
// save encrypts the secret, empties the box and says only that a value exists; a rebuilt form shows presence, an
// empty box and no value anywhere; saving with the box left empty keeps the stored secret; a new value replaces it;
// the same on the Ozon form; every box is a labelled tab stop.
[TestClass]
public sealed class SecretPresenceUiTests
{
    [TestMethod]
    public void ASavedSecretShowsPresenceOnlyAnEmptyBoxKeepsItAndAFailureKeepsWhatWasTyped()
    {
        var root = Path.Combine(Path.GetTempPath(), "secret-presence-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            try
            {
                Directory.CreateDirectory(root);
                var trendyolStore = new TrendyolSettingsStore(Path.Combine(root, "trendyol.bin"));
                Window Show(FrameworkElement panel) { var w = new Window { Content = panel, Width = 900, Height = 700, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 }; w.Show(); Drain(w); return w; }
                TextBlock Presence(FrameworkElement p) => One<TextBlock>(p, t => (string?)t.Tag == "secret-presence", "presence line");
                TextBlock Status(FrameworkElement p, string tag) => One<TextBlock>(p, t => (string?)t.Tag == tag, tag + " line");
                PasswordBox Secret(FrameworkElement p) => One<PasswordBox>(p, _ => true, "secret box");
                Button Btn(FrameworkElement p, string label) => One<Button>(p, b => b.Content as string == label, "'" + label + "' button");
                void Click(Window w, ButtonBase b) { b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(w); }
                bool NoValueOnScreen(FrameworkElement p, string value) => !Descendants(p).OfType<TextBlock>().Any(t => t.Text.Contains(value)) && !Descendants(p).OfType<TextBox>().Any(t => t.Text.Contains(value));

                // Trendyol: a fresh form -- no secret yet.
                var panel = TrendyolPanel.Create(root); var window = Show(panel);
                try
                {
                    var boxes = Descendants(panel).OfType<TextBox>().ToList(); var supplier = boxes[0]; var key = boxes[1]; var agent = boxes[2];
                    var secret = Secret(panel); var status = Status(panel, "trendyol-status");
                    Assert.AreEqual(SecretField.EmptyText, Presence(panel).Text); Assert.IsTrue(secret.IsTabStop && supplier.IsTabStop); StringAssert.StartsWith(AutomationProperties.GetName(secret), "API secret");
                    supplier.Text = "123"; key.Text = "key-1"; agent.Text = "123 - SelfIntegration";
                    Click(window, Btn(panel, "Şifreli kaydet"));
                    var slot = One<TextBlock>(panel, t => t != status && t.Text.Contains("Gizli değer gerekli"), "validation slot");
                    Assert.AreEqual(Visibility.Visible, slot.Visibility, "A missing secret is refused in the row's slot."); StringAssert.Contains(status.Text, "Gizli değer gerekli"); Assert.IsNull(trendyolStore.Load());

                    // A validation failure keeps what was typed and writes nothing.
                    secret.Password = "s3cr3t-one"; supplier.Text = "abc";
                    Click(window, Btn(panel, "Şifreli kaydet"));
                    StringAssert.Contains(status.Text, "sayısal olmalı"); Assert.AreEqual("s3cr3t-one", secret.Password, "The typed secret survives the failure."); Assert.IsNull(trendyolStore.Load());

                    // A valid save encrypts it, empties the box, and only presence remains on screen.
                    supplier.Text = "123";
                    Click(window, Btn(panel, "Şifreli kaydet"));
                    StringAssert.Contains(status.Text, "kaydedildi"); Assert.AreEqual("s3cr3t-one", trendyolStore.Load()!.ApiSecret);
                    Assert.AreEqual("", secret.Password); Assert.AreEqual(SecretField.SavedText, Presence(panel).Text); Assert.IsTrue(NoValueOnScreen(panel, "s3cr3t"));
                }
                finally { window.Close(); }

                // A rebuilt form: presence, an empty box, the value nowhere; an empty box on save keeps the secret; a new value replaces it.
                panel = TrendyolPanel.Create(root); window = Show(panel);
                try
                {
                    var secret = Secret(panel); var status = Status(panel, "trendyol-status");
                    Assert.AreEqual(SecretField.SavedText, Presence(panel).Text); Assert.AreEqual("", secret.Password); Assert.IsTrue(NoValueOnScreen(panel, "s3cr3t"));
                    Assert.AreEqual("key-1", Descendants(panel).OfType<TextBox>().ToList()[1].Text, "A non-secret field is loaded as before.");
                    Descendants(panel).OfType<TextBox>().ToList()[1].Text = "key-2";
                    Click(window, Btn(panel, "Şifreli kaydet"));
                    StringAssert.Contains(status.Text, "kaydedildi"); Assert.AreEqual("s3cr3t-one", trendyolStore.Load()!.ApiSecret, "Left empty, the box keeps the stored secret."); Assert.AreEqual("key-2", trendyolStore.Load()!.ApiKey);
                    secret.Password = "s3cr3t-two";
                    Click(window, Btn(panel, "Şifreli kaydet"));
                    Assert.AreEqual("s3cr3t-two", trendyolStore.Load()!.ApiSecret, "A typed value replaces the stored secret."); Assert.AreEqual("", secret.Password);
                }
                finally { window.Close(); }

                // Ozon: the same standard on its form.
                var ozonStore = new OzonSettingsStore(Path.Combine(root, "ozon.bin"));
                var ozon = OzonPanel.Create(root); window = Show(ozon);
                try
                {
                    var clientId = One<TextBox>(ozon, _ => true, "client id box"); var key = Secret(ozon); var status = Status(ozon, "ozon-status");
                    Assert.AreEqual(SecretField.EmptyText, Presence(ozon).Text); Assert.IsTrue(key.IsTabStop); StringAssert.StartsWith(AutomationProperties.GetName(key), "API key");
                    clientId.Text = "12345"; key.Password = "ozon-key-0123456789";
                    Click(window, Btn(ozon, "Ayarları güvenli kaydet")); WaitUntil(window, () => status.Text.Contains("kaydedildi") || status.Text.Contains("girin"), "the Ozon save");
                    StringAssert.Contains(status.Text, "kaydedildi"); Assert.AreEqual("ozon-key-0123456789", ozonStore.Load()!.ApiKey); Assert.AreEqual("", key.Password); Assert.AreEqual(SecretField.SavedText, Presence(ozon).Text);
                }
                finally { window.Close(); }
                ozon = OzonPanel.Create(root); window = Show(ozon);
                try
                {
                    var status = Status(ozon, "ozon-status");
                    Assert.AreEqual(SecretField.SavedText, Presence(ozon).Text); Assert.AreEqual("", Secret(ozon).Password); Assert.IsTrue(NoValueOnScreen(ozon, "ozon-key"));
                    Click(window, Btn(ozon, "Ayarları güvenli kaydet")); WaitUntil(window, () => status.Text.Contains("kaydedildi") || status.Text.Contains("girin"), "the second Ozon save");
                    Assert.AreEqual("ozon-key-0123456789", ozonStore.Load()!.ApiKey, "Left empty, the box keeps the stored key.");
                    Click(window, Btn(ozon, "Yerel bağlantıyı sil")); WaitUntil(window, () => status.Text.Contains("silindi"), "the delete");
                    Assert.IsNull(ozonStore.Load()); Assert.AreEqual(SecretField.EmptyText, Presence(ozon).Text);
                }
                finally { window.Close(); }
            }
            finally
            {
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    try { if (Directory.Exists(root)) Directory.Delete(root, true); break; }
                    catch (IOException) { Thread.Sleep(300); }
                    catch (UnauthorizedAccessException) { Thread.Sleep(300); }
                }
            }
        });
    }

    static void WaitUntil(Window window, Func<bool> condition, string what)
    {
        for (var i = 0; i < 400; i++) { Drain(window); if (condition()) return; Thread.Sleep(25); }
        Assert.Fail($"Timed out waiting for {what}.");
    }

    static void Drain(Window window) { window.UpdateLayout(); for (var i = 0; i < 4; i++) window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { })); }

    static T One<T>(DependencyObject root, Func<T, bool> predicate, string what) where T : DependencyObject
    {
        var hits = Descendants(root).OfType<T>().Where(predicate).ToList();
        Assert.AreEqual(1, hits.Count, $"Expected exactly one {what} in the visual tree, found {hits.Count}.");
        return hits[0];
    }

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
