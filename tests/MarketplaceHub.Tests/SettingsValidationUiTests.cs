using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #856 on the real settings shell and the real forms sharing one edit state over a temporary directory: the banner
// at the top lists every issue (blocking first, the taxonomy's order inside a level) with a focusable row and a
// deep link that selects the owning category and opens the owner's section; it wraps instead of clipping in a
// narrow window; fixing a connection on its real form re-checks and drops the row while the typed values appear
// nowhere; a save conflict on the real locale form (the row saved elsewhere between load and save) is refused,
// writes nothing and lands on the banner; Escape dismisses; reloading the row resolves it and the banner says so
// once; a clean save afterwards leaves no banner.
[TestClass]
public sealed class SettingsValidationUiTests
{
    [TestMethod]
    public void TheBannerListsIssuesDeepLinksToTheOwnerAndReportsResolution()
    {
        var root = Path.Combine(Path.GetTempPath(), "settings-banner-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            try
            {
                Directory.CreateDirectory(root);
                var known = new HashSet<string>(ScreenParityAudit.RequiredRoutes.Concat(new[] { "locale-settings", "migration", "stock-policies", "policy-center", "price-policies", "readiness", "diagnostics", "channels", "shipping", "etsy", "ebay", "ozon", "joom", "amazon", "trendyol", "hepsiburada", "fruugo", "allegro", "wish" }), StringComparer.OrdinalIgnoreCase);
                // Seed: Trendyol verified once in the registry but its credentials are gone; a locale row an older build wrote with a currency no longer supported.
                var registry = new MarketplaceConnectionStore(root); registry.RecordTest(registry.List().Single(c => c.Channel == "trendyol" && c.ShopId == "default").Id, true);
                _ = new LocaleSettingsStore(root); SettingsValidationTests.InsertLocale(root, "etsy", "default", "XXX");

                var state = new SettingsEditState(); var opened = new List<string>(); var navigated = new List<string>();
                var locale = LocaleSettingsPanel.Create(root, state); var trendyol = TrendyolPanel.Create(root, state);
                var shell = SettingsPanel.Create(new SettingsPanel.Context(root, r => navigated.Add(r), known.Contains, (route, section) => opened.Add(route + "/" + section), state, () => SettingsValidation.Collect(root, state, DateTime.UtcNow)));
                var host = new StackPanel(); host.Children.Add(locale); host.Children.Add(trendyol); host.Children.Add(shell);
                var window = new Window { Content = new ScrollViewer { Content = host }, Width = 1100, Height = 900, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
                window.Show(); Drain(window);
                try
                {
                    Border? BannerOrNull() => Descendants(shell).OfType<Border>().SingleOrDefault(b => (string?)b.Tag == "settings-banner");
                    Border Banner() => BannerOrNull() ?? throw new AssertFailedException("The banner is not on screen.");
                    TextBlock Headline() => Descendants(Banner()).OfType<TextBlock>().Single(t => (string?)t.Tag == "settings-banner-headline");
                    List<Border> Rows() => Descendants(Banner()).OfType<Border>().Where(b => (string?)b.Tag == "settings-issue").ToList();
                    string Id(DependencyObject o) => AutomationProperties.GetAutomationId(o);
                    Button Go(Border row) => Descendants(row).OfType<Button>().Single(b => (string?)b.Tag == "settings-issue-open");
                    ListBox List() => Descendants(shell).OfType<ListBox>().Single(l => (string?)l.Tag == "settings-categories");
                    string Selected() => (string)((ListBoxItem)List().SelectedItem).Tag;
                    void Click(ButtonBase b) { b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(window); }
                    var localeStatus = Descendants(locale).OfType<TextBlock>().Single(t => (string?)t.Tag == "locale-status");
                    var localeBoxes = Descendants(locale).OfType<TextBox>().ToList(); var vat = localeBoxes[2];
                    var save = Descendants(locale).OfType<Button>().Single(b => b.Content as string == "Kaydet"); var load = Descendants(locale).OfType<Button>().Single(b => b.Content as string == "Seçileni yükle");
                    var grid = Descendants(locale).OfType<DataGrid>().Single();

                    // Two blocking issues: the general category's entry before the connections entry; rows and links reachable by keyboard and named.
                    StringAssert.Contains(Headline().Text, "2 ayar sorunu"); StringAssert.Contains(Headline().Text, "2 engelleyici");
                    var rows = Rows(); CollectionAssert.AreEqual(new[] { "locale", "trendyol-connection" }, rows.Select(Id).ToArray());
                    Assert.IsTrue(rows.All(r => r.Focusable && Go(r).IsTabStop)); StringAssert.Contains(AutomationProperties.GetName(Go(rows[1])), "Trendyol"); StringAssert.Contains(AutomationProperties.GetName(rows[1]), "Hata");
                    Assert.IsTrue(Descendants(Banner()).OfType<TextBlock>().Where(t => !InButton(t)).All(t => t.TextWrapping == TextWrapping.Wrap), "Every line wraps, so a narrow or scaled window grows the banner instead of clipping it.");

                    // A narrow window (a scaled display): the banner and every line stay inside the shell.
                    window.Width = 520; Drain(window);
                    Assert.IsTrue(Banner().ActualWidth <= shell.ActualWidth + 1, $"banner {Banner().ActualWidth} vs shell {shell.ActualWidth}");
                    Assert.IsTrue(Descendants(Banner()).OfType<TextBlock>().All(t => t.ActualWidth <= Banner().ActualWidth + 1));
                    window.Width = 1100; Drain(window);

                    // The deep link selects the owning category and opens the owner's section; the locale entry navigates to its page.
                    Click(Go(rows[1])); Assert.AreEqual("connections", Selected()); CollectionAssert.AreEqual(new[] { "trendyol/connection" }, opened);
                    Click(Go(Rows()[0])); Assert.AreEqual("general", Selected()); CollectionAssert.AreEqual(new[] { "locale-settings" }, navigated);

                    // Fixing Trendyol on its real form: the save re-checks and the banner drops the row; the typed values appear nowhere in the shell.
                    var boxes = Descendants(trendyol).OfType<TextBox>().ToList(); boxes[0].Text = "123"; boxes[1].Text = "key-1"; boxes[2].Text = "123 - SelfIntegration"; Descendants(trendyol).OfType<PasswordBox>().Single().Password = "s3cr3t-value";
                    Click(Descendants(trendyol).OfType<Button>().Single(b => b.Content as string == "Şifreli kaydet")); Drain(window);
                    StringAssert.Contains(Headline().Text, "1 ayar sorunu"); CollectionAssert.AreEqual(new[] { "locale" }, Rows().Select(Id).ToArray());
                    Assert.IsFalse(Descendants(shell).OfType<TextBlock>().Any(t => TextOf(t).Contains("s3cr3t") || TextOf(t).Contains("key-1")) || Descendants(shell).OfType<FrameworkElement>().Any(e => e.ToolTip is string tip && (tip.Contains("s3cr3t") || tip.Contains("key-1"))));

                    // A save conflict on the real locale form: the row loaded at version 1 is saved elsewhere (version 2) before the form saves.
                    grid.SelectedItem = grid.Items.OfType<StoreLocaleSettings>().Single(s => s.Channel == "etsy" && s.ShopId == "default"); Drain(window); Click(load);
                    new LocaleSettingsStore(root).Save(new StoreLocaleSettings { Channel = "etsy", ShopId = "default", Currency = "USD", CultureName = "tr-TR", VatRate = 20, DatePattern = "dd.MM.yyyy" });
                    vat.Text = "18"; Drain(window); Click(save);
                    StringAssert.Contains(localeStatus.Text, "çakışması");
                    var stored = new LocaleSettingsStore(root).Get("etsy", "default")!; Assert.AreEqual("USD", stored.Currency); Assert.AreEqual(2, stored.Version); Assert.AreEqual(20m, stored.VatRate, "The conflicting save wrote nothing.");
                    StringAssert.Contains(Headline().Text, "1 ayar sorunu"); var conflictRow = Rows().Single(); Assert.AreEqual("locale", Id(conflictRow));
                    var rowTexts = Descendants(conflictRow).OfType<TextBlock>().Select(TextOf).ToList();
                    Assert.IsTrue(rowTexts.Any(t => t.Contains("Kayıt çakışması")) && rowTexts.Any(t => t.Contains("sürüm 1 → 2")), "The other writer's row is valid, so only the conflict remains: " + string.Join(" | ", rowTexts));
                    Assert.IsTrue(grid.Items.OfType<StoreLocaleSettings>().Any(s => s.Version == 2), "The grid shows the row that won.");

                    // Escape inside the banner dismisses it for now.
                    conflictRow.Focus(); Drain(window);
                    conflictRow.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent }); Drain(window);
                    Assert.IsNull(BannerOrNull());

                    // Reloading the row resolves the conflict: the banner reports the resolution once; a clean save afterwards leaves no banner.
                    grid.SelectedItem = grid.Items.OfType<StoreLocaleSettings>().Single(s => s.Channel == "etsy" && s.ShopId == "default"); Drain(window); Click(load);
                    var resolved = Descendants(Banner()).OfType<TextBlock>().Single(t => (string?)t.Tag == "settings-banner-resolved"); StringAssert.Contains(resolved.Text, "giderildi"); Assert.AreEqual(0, Rows().Count);
                    vat.Text = "18"; Drain(window); Click(save);
                    StringAssert.Contains(localeStatus.Text, "kaydedildi"); Assert.AreEqual(3, new LocaleSettingsStore(root).Get("etsy", "default")!.Version); Assert.IsNull(BannerOrNull());
                }
                finally { window.Close(); }
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

    // A TextBlock built from inline runs does not carry them in Text; the range over its content does.
    static string TextOf(TextBlock block) => new System.Windows.Documents.TextRange(block.ContentStart, block.ContentEnd).Text;

    static bool InButton(DependencyObject node) { for (var p = VisualTreeHelper.GetParent(node); p is not null; p = VisualTreeHelper.GetParent(p)) if (p is Button) return true; return false; }

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
