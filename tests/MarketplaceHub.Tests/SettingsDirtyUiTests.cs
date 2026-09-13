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
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #854 on the real forms and the real settings shell sharing one edit state: editing the VAT badges the general
// category and the locale entry with the field's label; typing a Trendyol secret badges the connections category
// with "API secret (gizli)" and the value appears nowhere in the shell; the badges survive navigating between
// categories and a deep link; a save blocked by validation keeps the badge; a valid save clears it; a rebuilt
// shell and forms with a fresh state (a restart without a save) show no badge and the saved values.
[TestClass]
public sealed class SettingsDirtyUiTests
{
    [TestMethod]
    public void BadgesFollowTheFormsNameFieldsOnlyAndClearOnSaveOrRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "settings-dirty-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            try
            {
                Directory.CreateDirectory(root);
                var known = new HashSet<string>(ScreenParityAudit.RequiredRoutes.Concat(new[] { "locale-settings", "migration", "stock-policies", "policy-center", "price-policies", "readiness", "diagnostics", "channels", "shipping", "etsy", "ebay", "ozon", "joom", "amazon", "trendyol", "hepsiburada", "fruugo", "allegro", "wish" }), StringComparer.OrdinalIgnoreCase);
                Window Open(SettingsEditState state, out FrameworkElement locale, out FrameworkElement trendyol, out FrameworkElement shell, out Action<string> select)
                {
                    Action<string>? selector = null;
                    locale = LocaleSettingsPanel.Create(root, state); trendyol = TrendyolPanel.Create(root, state);
                    shell = SettingsPanel.Create(new SettingsPanel.Context(root, _ => { }, known.Contains, null, state), s => selector = s);
                    var host = new StackPanel(); host.Children.Add(locale); host.Children.Add(trendyol); host.Children.Add(shell);
                    var window = new Window { Content = new ScrollViewer { Content = host }, Width = 1200, Height = 900, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
                    window.Show(); Drain(window); select = selector!; return window;
                }
                var state = new SettingsEditState();
                var window = Open(state, out var locale, out var trendyol, out var shell, out var select);
                try
                {
                    ListBox List() => Descendants(shell).OfType<ListBox>().Single(l => (string?)l.Tag == "settings-categories");
                    ListBoxItem Item(string key) => List().Items.OfType<ListBoxItem>().Single(i => (string)i.Tag == key);
                    string ItemText(string key) => ((TextBlock)Item(key).Content).Text;
                    StackPanel Content() => Descendants(shell).OfType<StackPanel>().Single(s => (string?)s.Tag == "settings-content");
                    Border Entry(string key) => Content().Children.OfType<Border>().Single(b => (string?)b.Tag == "settings-entry" && AutomationProperties.GetAutomationId(b) == key);
                    TextBlock? Badge(string key) => Descendants(Entry(key)).OfType<TextBlock>().SingleOrDefault(t => (string?)t.Tag == "settings-dirty-badge");
                    void Click(ButtonBase b) { b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(window); }
                    var vat = Descendants(locale).OfType<TextBox>().Single(t => t.Text == "20");
                    var localeStatus = Descendants(locale).OfType<TextBlock>().Single(t => (string?)t.Tag == "locale-status");
                    var save = Descendants(locale).OfType<Button>().Single(b => b.Content as string == "Kaydet");
                    var secret = Descendants(trendyol).OfType<PasswordBox>().Single();

                    Assert.IsFalse(state.Any); Assert.AreEqual("Genel", ItemText("general")); Assert.IsNull(Badge("locale"));

                    // Edit the VAT: the general category and the locale entry carry the mark with the field's label.
                    vat.Text = "18"; Drain(window);
                    Assert.AreEqual("Genel " + SettingsPanel.DirtyMark, ItemText("general")); StringAssert.Contains((string)Item("general").ToolTip, "KDV %"); StringAssert.Contains(AutomationProperties.GetName(Item("general")), "kaydedilmemiş değişiklik");
                    Assert.IsNotNull(Badge("locale")); StringAssert.Contains(Badge("locale")!.Text, "KDV %"); StringAssert.Contains(AutomationProperties.GetName(Entry("locale")), "KDV %");
                    Assert.AreEqual("Bağlantılar", ItemText("connections"), "Only the edited section is marked.");

                    // Type a Trendyol secret: the connections category is marked by the field's label; the value is nowhere in the shell.
                    secret.Password = "s3cr3t-value-xyz"; Drain(window);
                    Assert.AreEqual("Bağlantılar " + SettingsPanel.DirtyMark, ItemText("connections")); StringAssert.Contains((string)Item("connections").ToolTip, "API secret (gizli)");
                    Assert.IsFalse(Descendants(shell).OfType<TextBlock>().Any(t => t.Text.Contains("s3cr3t")) || Descendants(shell).OfType<FrameworkElement>().Any(e => e.ToolTip is string tip && tip.Contains("s3cr3t")), "The secret's value never reaches a badge or a tooltip.");
                    Assert.AreEqual(2, state.All.Count);

                    // Navigating between categories and deep-linking keep the marks.
                    List().SelectedItem = Item("connections"); Drain(window);
                    Assert.IsNotNull(Badge("trendyol-connection")); StringAssert.Contains(Badge("trendyol-connection")!.Text, "API secret (gizli)"); Assert.IsFalse(Badge("trendyol-connection")!.Text.Contains("s3cr3t"));
                    select("general"); Drain(window);
                    Assert.IsNotNull(Badge("locale")); Assert.AreEqual("Genel " + SettingsPanel.DirtyMark, ItemText("general"));

                    // A save blocked by validation keeps the mark; a valid save clears it.
                    vat.Text = "abc"; Drain(window); Click(save);
                    StringAssert.Contains(localeStatus.Text, "KDV oranı"); Assert.IsNotNull(state.Dirty("locale")); Assert.AreEqual("Genel " + SettingsPanel.DirtyMark, ItemText("general"));
                    vat.Text = "18"; Drain(window); Click(save);
                    StringAssert.Contains(localeStatus.Text, "kaydedildi"); Assert.IsNull(state.Dirty("locale")); Assert.AreEqual("Genel", ItemText("general")); Assert.IsNull(Badge("locale"));
                    Assert.IsNotNull(state.Dirty("trendyol-connection"), "The other section's unsaved secret is still pending.");
                }
                finally { window.Close(); }

                // Restart without saving the secret: a fresh state and fresh forms show no mark and the saved VAT.
                var again = new SettingsEditState();
                var w2 = Open(again, out var locale2, out _, out var shell2, out _);
                try
                {
                    Assert.IsFalse(again.Any);
                    var list = Descendants(shell2).OfType<ListBox>().Single(l => (string?)l.Tag == "settings-categories");
                    Assert.IsTrue(list.Items.OfType<ListBoxItem>().All(i => !((TextBlock)i.Content).Text.Contains(SettingsPanel.DirtyMark)));
                    var grid = Descendants(locale2).OfType<DataGrid>().Single();
                    Assert.IsTrue(grid.Items.OfType<StoreLocaleSettings>().Any(s => s.VatRate == 18m), "The valid save persisted; the unsaved secret did not.");
                    Assert.IsNull(new TrendyolSettingsStore(Path.Combine(root, "trendyol.bin")).Load(), "No Trendyol setting was written.");
                }
                finally { w2.Close(); }
            }
            finally
            {
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    try { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
                    catch (IOException) { Thread.Sleep(300); }
                    catch (UnauthorizedAccessException) { Thread.Sleep(300); }
                }
            }
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
