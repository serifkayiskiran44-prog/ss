using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #858 on the real main window: the header's page title and breadcrumb, the window's base face and size, the
// dashboard's section title and KPI figures, the settings shell's title, a channel form's title and the pricing
// formula's monospace box all read the typography tokens; a long Turkish page title wraps in the header at the
// window's minimum width instead of being clipped.
[TestClass]
public sealed class TextStylesUiTests
{
    [TestMethod]
    public void TheMainScreensReadTheTypographyTokensAndALongTitleWraps()
    {
        var root = Path.Combine(Path.GetTempPath(), "text-styles-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            MainWindow window = null;
            try
            {
                Directory.CreateDirectory(root);
                window = new MainWindow(root); window.Show(); Drain(window);
                Assert.AreEqual(DesignTokens.FontFamilyBody.Source, window.FontFamily.Source); Assert.AreEqual(DesignTokens.TextBodySize, window.FontSize);
                var pageTitle = (TextBlock)window.FindName("PageTitle"); Assert.AreEqual(DesignTokens.TextPageTitleSize, pageTitle.FontSize); Assert.AreEqual(DesignTokens.FontWeightTitle, pageTitle.FontWeight);
                var breadcrumb = (TextBlock)window.FindName("BreadcrumbText"); Assert.AreEqual(DesignTokens.TextCaptionSize, breadcrumb.FontSize);

                // Lookups stay inside the content area: the sidebar lists the same titles at body size.
                var content = (TabControl)window.FindName("ModuleTabs");
                Navigate(window, "dashboard"); Drain(window);
                var dashboardTitle = Find(Descendants(content).OfType<TextBlock>(), t => t.Text == "Genel bakış", "the dashboard title"); Assert.AreEqual(DesignTokens.TextSectionTitleSize, dashboardTitle.FontSize); Assert.AreEqual(DesignTokens.FontWeightTitle, dashboardTitle.FontWeight);
                // The cards land after the dashboard's asynchronous refresh.
                bool Kpi(TextBlock t) => t.FontWeight == DesignTokens.FontWeightKpi && t.FontSize == DesignTokens.TextKpiSize && t.Text.Length > 0;
                WaitUntil(window, () => Descendants(content).OfType<TextBlock>().Any(Kpi), "the dashboard's KPI cards");

                Navigate(window, "settings"); Drain(window);
                var settingsTitle = Find(Descendants(content).OfType<TextBlock>(), t => t.Text == "Ayarlar", "the settings title"); Assert.AreEqual(DesignTokens.TextSectionTitleSize, settingsTitle.FontSize);
                var settingsHint = Find(Descendants(content).OfType<TextBlock>(), t => t.Text.StartsWith("Var olan ayarlar tek ağaçta", StringComparison.Ordinal), "the settings hint"); Assert.AreEqual(DesignTokens.TextBodySize, settingsHint.FontSize); Assert.AreEqual(TextStyles.MutedColor, ((SolidColorBrush)settingsHint.Foreground).Color);

                Navigate(window, "trendyol"); Drain(window);
                var tabs = Find(Descendants(content).OfType<TabControl>(), t => t.Items.OfType<TabItem>().Any(i => i.Header as string == "Bağlantı"), "the channel tab control"); tabs.SelectedIndex = tabs.Items.Count - 1; Drain(window);
                var channelTitle = Find(Descendants(content).OfType<TextBlock>(), t => t.Text == "Trendyol bağlantısı", "the Trendyol title"); Assert.AreEqual(DesignTokens.TextSectionTitleSize, channelTitle.FontSize);

                Navigate(window, "xml"); Drain(window);
                // The pricing formula lives on the XML page's third settings tab; a tab's content exists only while selected.
                var xmlTabs = Find(Descendants(content).OfType<TabControl>(), t => t.Items.OfType<TabItem>().Any(i => (i.Header as string ?? "").StartsWith("3", StringComparison.Ordinal)), "the XML settings tabs");
                xmlTabs.SelectedItem = xmlTabs.Items.OfType<TabItem>().First(i => (i.Header as string ?? "").StartsWith("3", StringComparison.Ordinal)); Drain(window);
                var formula = Find(Descendants(content).OfType<TextBox>(), b => b.FontFamily.Source.StartsWith("Consolas", StringComparison.Ordinal), "the monospace formula box"); Assert.AreEqual(DesignTokens.TextMonoSize, formula.FontSize);

                // A long Turkish page title at the window's minimum width wraps inside its column instead of being clipped.
                window.Width = window.MinWidth; Drain(window);
                var shortHeight = pageTitle.ActualHeight;
                pageTitle.Text = "Şüpheli işlemlerin çözümlenmesi, iade uzlaşması ve kargo istisnaları için kanal bazlı yapılandırma özeti ve denetim raporu"; Drain(window);
                Assert.AreEqual(TextWrapping.Wrap, pageTitle.TextWrapping); Assert.IsTrue(pageTitle.ActualHeight >= shortHeight * 2, $"{pageTitle.ActualHeight} vs one line {shortHeight}");
                Assert.IsTrue(pageTitle.ActualWidth <= ((FrameworkElement)VisualTreeHelper.GetParent(pageTitle)!).ActualWidth + 0.5);
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

    static T Find<T>(IEnumerable<T> items, Func<T, bool> predicate, string what) { var hit = items.FirstOrDefault(predicate); if (hit is null) Assert.Fail($"Could not find {what}."); return hit!; }

    static void Navigate(MainWindow window, string key) => typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { key, true });

    static void Drain(Window window) { window.UpdateLayout(); for (var i = 0; i < 4; i++) window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { })); }

    static void WaitUntil(Window window, Func<bool> condition, string what)
    {
        for (var i = 0; i < 400; i++) { Drain(window); if (condition()) return; Thread.Sleep(25); }
        Assert.Fail($"Timed out waiting for {what}.");
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
