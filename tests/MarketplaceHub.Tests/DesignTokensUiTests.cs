using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #857 on the real main window: it resolves the tokens on its own (this host has no Application, as a window
// built by a tool would not), its shared implicit styles read them, the main screens measure with the same DIP
// values as before, and the shared blocks (form rows, the settings shell, the connection forms' compact buttons)
// take their spacing from the same source.
[TestClass]
public sealed class DesignTokensUiTests
{
    [TestMethod]
    public void TheMainWindowResolvesTheTokensAndItsScreensKeepTheirMeasures()
    {
        var root = Path.Combine(Path.GetTempPath(), "design-tokens-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            MainWindow window = null;
            try
            {
                Directory.CreateDirectory(root);
                window = new MainWindow(root); window.Show(); Drain(window);
                Assert.AreEqual(DesignTokens.SpacePage, (double)window.FindResource("SpacePage"), "The window merges the token file itself.");

                // The implicit styles read the tokens.
                Assert.AreEqual(DesignTokens.ButtonPadding, Setter(Style(window, typeof(Button)), Control.PaddingProperty)); Assert.AreEqual(DesignTokens.ControlMargin, Setter(Style(window, typeof(Button)), FrameworkElement.MarginProperty));
                Assert.AreEqual(DesignTokens.InputPadding, Setter(Style(window, typeof(TextBox)), Control.PaddingProperty)); Assert.AreEqual(DesignTokens.ControlMinHeight, Setter(Style(window, typeof(TextBox)), FrameworkElement.MinHeightProperty));
                Assert.AreEqual(DesignTokens.InputPadding, Setter(Style(window, typeof(PasswordBox)), Control.PaddingProperty)); Assert.AreEqual(DesignTokens.ControlMinHeight, Setter(Style(window, typeof(ComboBox)), FrameworkElement.MinHeightProperty));
                Assert.AreEqual(DesignTokens.RowHeight, Setter(Style(window, typeof(DataGrid)), DataGrid.RowHeightProperty)); Assert.AreEqual(DesignTokens.HeaderPadding, Setter(Style(window, typeof(DataGridColumnHeader)), Control.PaddingProperty));
                Assert.AreEqual(DesignTokens.CardPadding, Setter(Style(window, typeof(GroupBox)), Control.PaddingProperty)); Assert.AreEqual(DesignTokens.TabPadding, Setter(Style(window, typeof(TabItem)), Control.PaddingProperty));
                var navigation = (Style)window.FindResource("NavigationItem"); Assert.AreEqual(DesignTokens.NavigationItemPadding, Setter(navigation, Control.PaddingProperty)); Assert.AreEqual(DesignTokens.NavigationItemMargin, Setter(navigation, FrameworkElement.MarginProperty));

                // The main screens measure with the same values: a real button and input in the header, the product grid, the settings shell.
                var back = (Button)window.FindName("BackButton"); Assert.AreEqual(DesignTokens.ButtonPadding, back.Padding);
                var search = (TextBox)window.FindName("GlobalSearchBox"); Assert.AreEqual(DesignTokens.ControlMinHeight, search.MinHeight); Assert.IsTrue(search.ActualHeight >= DesignTokens.ControlMinHeight);
                Assert.AreEqual(DesignTokens.ControlMinHeight, ((TextBox)window.FindName("NavigationSearchBox")).Height);
                // A grid that sets no row height of its own (the product list's density picks one deliberately) takes the token through the implicit style.
                Navigate(window, "locale-settings"); Drain(window);
                var grid = Descendants(window).OfType<DataGrid>().First(g => g.IsVisible && g.ReadLocalValue(DataGrid.RowHeightProperty) == DependencyProperty.UnsetValue); Assert.AreEqual(DesignTokens.RowHeight, grid.RowHeight);
                Navigate(window, "products"); Drain(window);
                var products = Descendants(window).OfType<DataGrid>().First(g => g.IsVisible); Assert.AreNotEqual(DependencyProperty.UnsetValue, products.ReadLocalValue(DataGrid.RowHeightProperty), "The product list's row height is a deliberate density choice, not a stray literal.");
                Navigate(window, "settings"); Drain(window);
                var categories = Descendants(window).OfType<ListBox>().Single(l => (string?)l.Tag == "settings-categories");
                var shell = (FrameworkElement)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(categories)!)!;
                Assert.AreEqual(new Thickness(DesignTokens.SpacePage), shell.Margin, "The settings shell's page margin is the page token.");
                Navigate(window, "trendyol"); Drain(window);
                // The channel page is a tab control whose connection tab is not the default; a tab's content is in the visual tree only while selected.
                var tabs = Descendants(window).OfType<TabControl>().First(t => t.Items.OfType<TabItem>().Any(i => i.Header as string == "Bağlantı")); tabs.SelectedIndex = tabs.Items.Count - 1; Drain(window);
                var save = Descendants(window).OfType<Button>().First(b => b.Content as string == "Şifreli kaydet"); Assert.AreEqual(DesignTokens.CompactButtonPadding, save.Padding);

                // Shared blocks read the same source: a form row's rhythm.
                var row = FormField.Build(new FormFieldSpec("Ad", true, "Yardım"), new TextBox());
                Assert.AreEqual(new Thickness(0, 0, 0, DesignTokens.SpaceControl), row.Root.Margin); Assert.AreEqual(new Thickness(0, 0, 0, DesignTokens.SpaceHairline), row.LabelText.Margin);

                // DPI: tokens are device-independent numbers; the window's scale does not enter them.
                var dpi = VisualTreeHelper.GetDpi(window); Assert.IsTrue(dpi.DpiScaleX > 0); Assert.AreEqual(36d, grid.RowHeight); Assert.AreEqual(36d, DesignTokens.RowHeight);
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

    static Style Style(Window window, Type target) => (Style)window.FindResource(target);
    static object Setter(Style style, DependencyProperty property) => style.Setters.OfType<Setter>().Single(s => s.Property == property).Value;
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
