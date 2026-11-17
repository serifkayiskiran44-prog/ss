#nullable enable
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class XmlWorkspaceUiTests
{
    string directory = "";

    [TestInitialize]
    public void Setup() => directory = Path.Combine(Path.GetTempPath(), "xml-workspace-ui-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    [TestMethod]
    public void WorkspaceUsesNamedCommandTableAndResizableDefinitionRegions() => InSta(() =>
    {
        var window = new MainWindow(directory); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        try
        {
            var tree = LogicalWalk((DependencyObject)window.Content).OfType<FrameworkElement>().ToArray();
            var primary = tree.Single(x => x.Name == "XmlPrimaryCommandBar");
            Assert.IsTrue(tree.Single(x => x.Name == "XmlSourcesGrid") is DataGrid);
            Assert.IsTrue(tree.Single(x => x.Name == "XmlCategoriesGrid") is DataGrid);
            Assert.IsNotNull(tree.Single(x => x.Name == "XmlCategoryCommandBar"));
            var splitter = (GridSplitter)tree.Single(x => x.Name == "XmlWorkspaceSplitter");
            Assert.AreEqual(GridResizeDirection.Rows, splitter.ResizeDirection);

            Assert.AreEqual(1, LogicalWalk(primary).OfType<Button>().Count(x => Equals(x.Content, "▶ Seçili XML'i başlat")));
            Assert.AreEqual(1, LogicalWalk(primary).OfType<Button>().Count(x => Equals(x.Content, "Ürün önizlemesi")));
            var templates = LogicalWalk(primary).OfType<Expander>().Single(x => Equals(x.Header, "Şablon işlemleri"));
            CollectionAssert.AreEquivalent(new[] { "Şablon aç", "Şablon kaydet" }, LogicalWalk(templates).OfType<Button>().Select(x => x.Content!.ToString()).ToArray());

            Assert.AreEqual("XmlDefinitionGeneral", GetField<StackPanel>(window, "sourceGeneral").Name);
            Assert.AreEqual("XmlDefinitionRules", GetField<StackPanel>(window, "sourceRules").Name);
            var actions = (FrameworkElement)Invoke(window, "BuildXmlDefinitionActions")!;
            Assert.AreEqual("XmlDefinitionActions", actions.Name);
            Assert.IsTrue(LogicalWalk(GetField<StackPanel>(window, "sourceGeneral")).OfType<Expander>().Where(x => Equals(x.Header, "XML adresi şifre istiyorsa")).All(x => !x.IsExpanded));
            Assert.IsTrue(LogicalWalk(GetField<StackPanel>(window, "sourceRules")).OfType<Expander>().All(x => !x.IsExpanded));
        }
        finally { window.Close(); }
    });

    [TestMethod]
    public void PreviewSummaryTracksSelectionTotalAndInvalidRows() => InSta(() =>
    {
        var window = new MainWindow(directory); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        try
        {
            var grid = GetField<DataGrid>(window, "preview");
            var summary = GetField<TextBlock>(window, "xmlPreviewSummary");
            grid.ItemsSource = new[]
            {
                new CatalogProduct { Sku = "OK", Name = "Valid", Currency = "TRY" },
                new CatalogProduct { Sku = "", Name = "", Currency = "TRY" }
            };
            grid.SelectedItem = grid.Items[0];

            StringAssert.Contains(summary.Text, "Seçili: 1");
            StringAssert.Contains(summary.Text, "Toplam: 2");
            StringAssert.Contains(summary.Text, "Hatalı: 1");
        }
        finally { window.Close(); }
    });

    static T GetField<T>(object target, string name) => (T)(target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target)
        ?? throw new InvalidOperationException(name));

    static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(target, args)
        ?? throw new MissingMethodException(target.GetType().Name, name);

    static IEnumerable<DependencyObject> LogicalWalk(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in LogicalWalk(child)) yield return descendant;
    }

    static void InSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher)); action(); }
            catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "STA UI test timed out.");
        if (error is not null) throw error;
    }
}
