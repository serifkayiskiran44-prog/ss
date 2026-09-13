using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #860 on the real main window: the sidebar toggle is a toolbar icon button (square, centred, the toolbar glyph
// size, named); collapsing the sidebar prints the navigation glyphs at the navigation size and expanding restores
// the body size; the Back button is a mixed icon-and-label button that dims while it cannot act; a toast's close
// button is an inline icon button with a spoken name and its glyph sits on the text's baseline; the import
// stepper's chips are mixed buttons whose disabled stages read as disabled.
[TestClass]
public sealed class IconStylesUiTests
{
    [TestMethod]
    public void ToolbarNavigationInlineAndStatusIconsReadTheTokensOnTheRealWindow()
    {
        var root = Path.Combine(Path.GetTempPath(), "icon-styles-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            MainWindow window = null;
            try
            {
                Directory.CreateDirectory(root);
                // The import stepper draws its chips once a source is selected; a fixture source makes that possible offline.
                new TrMarketplaceHubDesktop.Catalog.CatalogStore(root).SaveSource(new TrMarketplaceHubDesktop.Catalog.XmlSource { Id = "feed-1", Name = "Fixture feed", Location = "https://example.invalid/feed.xml" });
                window = new MainWindow(root); window.Show(); Drain(window);

                var toggle = (Button)window.FindName("SidebarToggle");
                Assert.AreEqual(DesignTokens.ToolbarButtonMinSize, toggle.MinWidth); Assert.AreEqual(DesignTokens.ToolbarButtonMinSize, toggle.MinHeight); Assert.AreEqual(DesignTokens.IconSizeToolbar, toggle.FontSize);
                Assert.AreEqual(HorizontalAlignment.Center, toggle.HorizontalContentAlignment); Assert.AreEqual(VerticalAlignment.Center, toggle.VerticalContentAlignment); Assert.IsTrue(AutomationProperties.GetName(toggle).Length > 0, "The toggle has a spoken name.");

                var list = (ListBox)window.FindName("NavigationList");
                ListBoxItem Products() => list.Items.OfType<ListBoxItem>().First(i => (i.Tag as string) == "products");
                Assert.AreEqual(DesignTokens.TextBodySize, Products().FontSize, "Expanded: the label at body size.");
                toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Drain(window);
                Assert.IsTrue(Products().Content!.ToString()!.Length <= 2, "Collapsed: a glyph of one or two characters, got '" + Products().Content + "'."); Assert.AreEqual(DesignTokens.IconSizeNavigation, Products().FontSize, "Collapsed: the glyph at the navigation size.");
                Assert.AreEqual(HorizontalAlignment.Center, Products().HorizontalContentAlignment);
                toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Drain(window);
                Assert.AreEqual(DesignTokens.TextBodySize, Products().FontSize, "Expanded again: back to the body size.");

                var back = (Button)window.FindName("BackButton");
                Assert.AreEqual("‹ Geri", back.Content); Assert.AreEqual(DesignTokens.IconButtonMinSize, back.MinHeight); Assert.AreEqual(VerticalAlignment.Center, back.VerticalContentAlignment);
                Assert.IsFalse(back.IsEnabled); Assert.AreEqual(IconStyles.DisabledOpacity, back.Opacity, "A Back that cannot act reads as disabled.");

                // A toast: its close button is an inline icon button with a spoken name; its glyph shares the text's baseline.
                typeof(MainWindow).GetMethod("Log", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(string), typeof(NotificationSeverity) }, null)!.Invoke(window, new object[] { "Simge deneme bildirimi", NotificationSeverity.Warning });
                Drain(window);
                var host = (StackPanel)window.FindName("ToastHost");
                var close = Descendants(host).OfType<Button>().First(b => b.Content?.ToString() == "✕");
                Assert.AreEqual(DesignTokens.IconButtonMinSize, close.MinWidth); Assert.AreEqual(DesignTokens.IconButtonMinSize, close.MinHeight); Assert.AreEqual(DesignTokens.IconSizeInline, close.FontSize); StringAssert.Contains(AutomationProperties.GetName(close), "kapat");
                var toastText = Descendants(host).OfType<TextBlock>().First(t => t.Inlines.OfType<Run>().Any(r => r.Text.Contains("Simge deneme")));
                var glyphRun = toastText.Inlines.OfType<Run>().First(); Assert.AreEqual(SeverityStyle.For(SeverityLevel.Warning, SeverityStyle.IsHighContrast).Glyph, glyphRun.Text.Trim()); Assert.AreEqual(IconStyles.Size(IconRole.Status), glyphRun.FontSize); Assert.AreEqual(BaselineAlignment.Baseline, glyphRun.BaselineAlignment);

                // The import stepper's chips: mixed glyph-and-label buttons; a stage that cannot be jumped to reads as disabled.
                Navigate(window, "xml"); Drain(window);
                var sources = (ListBox)typeof(MainWindow).GetField("sources", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                sources.SelectedItem = sources.Items.OfType<TrMarketplaceHubDesktop.Catalog.XmlSource>().Single(); Drain(window);
                var strip = (StackPanel)typeof(MainWindow).GetField("importStepper", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var chips = strip.Children.OfType<Button>().ToList();
                Assert.IsTrue(chips.Count > 0, "The stepper has chips."); Assert.IsTrue(chips.All(c => c.MinHeight == DesignTokens.IconButtonMinSize && c.VerticalContentAlignment == VerticalAlignment.Center), "Every chip is an icon button: " + string.Join(", ", chips.Select(c => $"{c.MinHeight}/{c.VerticalContentAlignment}")));
                Assert.IsTrue(chips.Where(c => !c.IsEnabled).All(c => c.Opacity == IconStyles.DisabledOpacity), "Disabled stages dim."); Assert.IsTrue(chips.Where(c => c.IsEnabled).All(c => c.Opacity == 1d), "Enabled chips are not dimmed.");
                Assert.IsTrue(chips.Any(c => !c.IsEnabled), "A fresh XML page has a stage that cannot be jumped to yet.");
            }
            finally
            {
                try { window?.Close(); if (window is not null) Drain(window); } catch (Exception) { }
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
                    catch (IOException) { Thread.Sleep(300); }
                    catch (UnauthorizedAccessException) { Thread.Sleep(300); }
                }
            }
        });
    }

    static void Navigate(MainWindow window, string key) => typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { key, true });
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
