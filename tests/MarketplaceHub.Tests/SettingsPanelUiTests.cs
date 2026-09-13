using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
using TrMarketplaceHubDesktop.Catalog;

// #853 on the real settings shell: the seven categories down the left as focusable, named items at a DIP width;
// the selected category's entries with wrapping labels, a secret badge on credential pages, "Aç" opening the owner
// (a channel's connection tab through the section hook) and inline hosts for what the shell owns; search across
// categories; a shell missing a route drops the entry; and, on the real window, "settings/pricing" lands on the
// pricing category with the settings route current.
[TestClass]
public sealed class SettingsPanelUiTests
{
    [TestMethod]
    public void CategoriesEntriesSecretBadgesInlineHostsAndSearchOnTheRealPanel()
    {
        var root = Path.Combine(Path.GetTempPath(), "settings-shell-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            try
            {
                Directory.CreateDirectory(root);
                var navigated = new List<string>(); var sections = new List<(string Route, string Section)>(); Action<string>? select = null;
                var known = new HashSet<string>(ScreenParityAudit.RequiredRoutes.Concat(new[] { "locale-settings", "migration", "stock-policies", "policy-center", "price-policies", "readiness", "diagnostics", "channels", "shipping", "etsy", "ebay", "ozon", "joom", "amazon", "trendyol", "hepsiburada", "fruugo", "allegro", "wish" }), StringComparer.OrdinalIgnoreCase);
                var panel = SettingsPanel.Create(new SettingsPanel.Context(root, navigated.Add, known.Contains, (r, s) => sections.Add((r, s))), s => select = s);
                var window = new Window { Content = panel, Width = 1100, Height = 800, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
                try
                {
                    window.Show(); Drain(window);
                    var list = Descendants(panel).OfType<ListBox>().Single(l => (string?)l.Tag == "settings-categories");
                    var content = Descendants(panel).OfType<StackPanel>().Single(s => (string?)s.Tag == "settings-content");
                    List<Border> Entries() => content.Children.OfType<Border>().Where(b => (string?)b.Tag == "settings-entry").ToList();
                    Border Entry(string key) => Entries().Single(b => AutomationProperties.GetAutomationId(b) == key);
                    void Click(ButtonBase b) { b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(window); }
                    void Choose(string key) { list.SelectedItem = list.Items.OfType<ListBoxItem>().Single(i => (string)i.Tag == key); Drain(window); }

                    Assert.AreEqual(7, list.Items.Count); Assert.IsTrue(list.Items.OfType<ListBoxItem>().All(i => i.Focusable && i.IsTabStop && AutomationProperties.GetName(i).Length > 0 && i.ToolTip is string));
                    Assert.AreEqual(SettingsPanel.CategoryWidth, ((Grid)VisualTreeHelper.GetParent(list)).ColumnDefinitions[0].Width.Value, "The category column is a DIP width.");
                    Assert.AreEqual("general", (string)((ListBoxItem)list.SelectedItem!).Tag, "The first category opens by default.");
                    Assert.AreEqual("Genel", content.Children.OfType<TextBlock>().Single(t => (string?)t.Tag == "settings-content-title").Text);
                    Assert.IsTrue(Descendants(Entry("backup")).OfType<Button>().Any(), "The backup entry hosts the backup panel inline."); Assert.IsFalse(Descendants(Entry("backup")).OfType<Button>().Any(b => (string?)b.Tag == "settings-open"), "...and has no 'Aç' of its own.");
                    Click(Descendants(Entry("locale")).OfType<Button>().Single(b => (string?)b.Tag == "settings-open")); Assert.AreEqual("locale-settings", navigated.Last());

                    Choose("connections");
                    Assert.AreEqual(14, Entries().Count);
                    var etsy = Entry("etsy-connection");
                    Assert.IsTrue(Descendants(etsy).OfType<TextBlock>().Any(t => (string?)t.Tag == "settings-secret-badge")); StringAssert.Contains(AutomationProperties.GetName(etsy), "gizli bilgi içerir");
                    Assert.IsFalse(Descendants(Entry("connections")).OfType<TextBlock>().Any(t => (string?)t.Tag == "settings-secret-badge"), "Only credential pages carry the badge.");
                    Click(Descendants(etsy).OfType<Button>().Single(b => (string?)b.Tag == "settings-open"));
                    Assert.AreEqual(("etsy", "connection"), sections.Last()); Assert.AreEqual("locale-settings", navigated.Last(), "A section hook opens the tab; plain navigation was not used.");
                    Assert.IsTrue(Descendants(etsy).OfType<TextBlock>().Take(3).All(t => t.TextWrapping == TextWrapping.Wrap), "Label, description and badge wrap.");
                    Assert.IsTrue(Descendants(etsy).OfType<Button>().Single(b => (string?)b.Tag == "settings-open").IsTabStop);

                    // Search spans every category and names the category; clearing returns to the selected one.
                    var search = Descendants(panel).OfType<TextBox>().Single(t => (string?)t.Tag == "settings-search");
                    search.Text = "audit"; Drain(window);
                    Assert.AreEqual(1, Entries().Count); Assert.AreEqual("diagnostics", AutomationProperties.GetAutomationId(Entries()[0])); StringAssert.StartsWith(Descendants(Entries()[0]).OfType<TextBlock>().First().Text, "Tanılama › ");
                    search.Text = "böyle bir ayar yok"; Drain(window);
                    Assert.AreEqual(0, Entries().Count); Assert.IsTrue(content.Children.OfType<TextBlock>().Any(t => (string?)t.Tag == "settings-empty"));
                    search.Text = ""; Drain(window); Assert.AreEqual(14, Entries().Count);

                    // Deep link from outside: the selector lands on the category and focuses it.
                    select!("pricing"); Drain(window);
                    Assert.AreEqual("pricing", (string)((ListBoxItem)list.SelectedItem!).Tag); Assert.AreEqual("Fiyatlandırma", content.Children.OfType<TextBlock>().Single(t => (string?)t.Tag == "settings-content-title").Text);
                    Assert.IsTrue(((ListBoxItem)list.SelectedItem!).IsKeyboardFocused, "The deep-linked category takes the keyboard.");
                }
                finally { window.Close(); }

                // A shell without the report route: the diagnostics category loses that entry.
                var fewer = SettingsPanel.Create(new SettingsPanel.Context(root, navigated.Add, key => known.Contains(key) && key != "reports"));
                var w2 = new Window { Content = fewer, Width = 1100, Height = 800, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
                try
                {
                    w2.Show(); Drain(w2);
                    var list2 = Descendants(fewer).OfType<ListBox>().Single(l => (string?)l.Tag == "settings-categories"); list2.SelectedItem = list2.Items.OfType<ListBoxItem>().Single(i => (string)i.Tag == "diagnostics"); Drain(w2);
                    var content2 = Descendants(fewer).OfType<StackPanel>().Single(s => (string?)s.Tag == "settings-content");
                    CollectionAssert.AreEqual(new[] { "diagnostics", "readiness" }, content2.Children.OfType<Border>().Where(b => (string?)b.Tag == "settings-entry").Select(b => AutomationProperties.GetAutomationId(b)).ToArray());
                }
                finally { w2.Close(); }
            }
            finally { Cleanup(root); }
        });
    }

    [TestMethod]
    public void TheShellDeepLinksToASettingsCategory()
    {
        var root = Path.Combine(Path.GetTempPath(), "settings-deeplink-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            MainWindow? window = null;
            try
            {
                var store = new CatalogStore(root);
                var source = new XmlSource { Id = Guid.NewGuid().ToString("N"), Name = "Fixture" };
                store.Import(source, new[] { new CatalogProduct { SourceId = source.Id, Sku = "A", Name = "Product A", Price = 10, Stock = 30 } });
                window = new MainWindow(root); window.Show(); Drain(window);
                var navigate = typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!;
                navigate.Invoke(window, new object[] { "settings/pricing", true }); Drain(window);
                Assert.AreEqual("settings", (string?)typeof(MainWindow).GetField("currentRoute", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window));
                var routes = (Dictionary<string, TabItem>)typeof(MainWindow).GetField("routes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var list = Descendants((DependencyObject)routes["settings"].Content).OfType<ListBox>().Single(l => (string?)l.Tag == "settings-categories");
                Assert.AreEqual("pricing", (string)((ListBoxItem)list.SelectedItem!).Tag);
                Assert.IsTrue(SettingsTaxonomy.Visible(routes.ContainsKey).SelectMany(c => c.Entries).All(e => e.Inline || routes.ContainsKey(e.Route)), "Every visible entry points at a route the real shell registers.");
                Assert.AreEqual(7, list.Items.Count, "The real shell registers every category's routes.");
                navigate.Invoke(window, new object[] { "products", true }); Drain(window);
                navigate.Invoke(window, new object[] { "settings/ghost", true }); Drain(window);
                Assert.AreEqual("products", (string?)typeof(MainWindow).GetField("currentRoute", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window), "An unknown category is not a deep link and not a route: nothing moves.");
            }
            finally { window?.Close(); Cleanup(root); }
        });
    }

    static void Cleanup(string root)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
            catch (IOException) { Thread.Sleep(300); }
            catch (UnauthorizedAccessException) { Thread.Sleep(300); }
        }
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
