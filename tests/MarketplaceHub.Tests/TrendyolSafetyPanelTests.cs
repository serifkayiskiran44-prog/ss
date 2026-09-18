using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;
using TrMarketplaceHubDesktop.Trendyol;

namespace MarketplaceHub.Tests;

[TestClass]
public class TrendyolSafetyPanelTests
{
    [TestMethod]
    public void MultipleBrandsRequireFreshSharedValuesAndNeverCarrySingleBrandDrafts()
    {
        InSta(dir =>
        {
            CreateCatalog(dir);
            var panel = new TrendyolWorkspacePanel(dir);
            var brands = Find<ListBox>(panel, "TrendyolSafetyBrands");
            var manufacturer = Find<TextBox>(panel, "TrendyolSafetyField1198");
            var save = Find<Button>(panel, "TrendyolSaveBrandSafety");
            Assert.IsFalse(save.IsEnabled, "Saving requires a selected brand and a nonempty field.");
            brands.SelectedItem = "Alpha";
            manufacturer.Text = "Alpha producer";
            Assert.IsTrue(save.IsEnabled);
            Click(save);

            brands.SelectedItems.Add("Beta");
            Assert.AreEqual("", manufacturer.Text, "A single brand's saved values must not become shared edits.");
            Assert.IsFalse(save.IsEnabled);
            manufacturer.Text = "Shared producer";
            Assert.IsTrue(save.IsEnabled);
            brands.SelectedItems.Add("Gamma");
            Assert.AreEqual("", manufacturer.Text, "Changing the target set must clear the previous shared draft.");
            Assert.IsFalse(save.IsEnabled);

            brands.UnselectAll();
            brands.SelectedItem = "Alpha";
            Assert.AreEqual("Alpha producer", manufacturer.Text, "Selecting one brand loads its persisted values.");
            brands.SelectedItem = "Beta";
            Assert.AreEqual("", manufacturer.Text, "A different brand must not inherit the previous brand's fields.");
            Assert.AreEqual(0, new TrendyolWorkspaceStore(dir).Receipts("123").Count);
        });
    }

    [TestMethod]
    public void SavingSelectedBrandsPreservesBlankFieldsAndExcludedBrandWithoutDispatch()
    {
        InSta(dir =>
        {
            CreateCatalog(dir);
            var store = new TrendyolWorkspaceStore(dir);
            var cached = store.Load("123");
            cached.Products.Add(new("LIVE", "A-1", "Published title", 100, 3, 25, 30, true));
            store.Save(cached);
            var panel = new TrendyolWorkspacePanel(dir);
            var brands = Find<ListBox>(panel, "TrendyolSafetyBrands");
            var manufacturer = Find<TextBox>(panel, "TrendyolSafetyField1198");
            var email = Find<TextBox>(panel, "TrendyolSafetyField1294");
            var address = Find<TextBox>(panel, "TrendyolSafetyField1296");
            var save = Find<Button>(panel, "TrendyolSaveBrandSafety");
            foreach (var brand in new[] { "Alpha", "Beta", "Gamma" })
            {
                brands.SelectedItem = brand;
                manufacturer.Text = brand + " producer";
                email.Text = brand.ToLowerInvariant() + "@example.test";
                address.Text = brand + " address";
                Click(save);
            }

            brands.UnselectAll();
            brands.SelectedItems.Add("Alpha");
            brands.SelectedItems.Add("Beta");
            manufacturer.Text = "Shared producer";
            email.Text = "   ";
            Assert.AreEqual("", address.Text);
            var review = Find<DataGrid>(panel, "TrendyolSafetyReview");
            Assert.IsTrue(review.IsReadOnly);
            Assert.AreEqual(2, review.Items.Count);
            var summary = Find<TextBlock>(panel, "TrendyolSafetySelectionSummary").Text;
            StringAssert.Contains(summary, "2 marka");
            StringAssert.Contains(summary, "3 ürün");
            Click(save);

            var reopened = new TrendyolWorkspacePanel(dir);
            var reopenedBrands = Find<ListBox>(reopened, "TrendyolSafetyBrands");
            foreach (var brand in new[] { "Alpha", "Beta", "Gamma" })
            {
                reopenedBrands.SelectedItem = brand;
                Assert.AreEqual(brand == "Gamma" ? "Gamma producer" : "Shared producer", Find<TextBox>(reopened, "TrendyolSafetyField1198").Text);
                Assert.AreEqual(brand.ToLowerInvariant() + "@example.test", Find<TextBox>(reopened, "TrendyolSafetyField1294").Text);
                Assert.AreEqual(brand + " address", Find<TextBox>(reopened, "TrendyolSafetyField1296").Text);
            }
            Assert.AreEqual(0, store.Receipts("123").Count, "Local brand settings must never dispatch marketplace writes.");
            Assert.AreEqual("Published title", store.Load("123").Products.Single().Title);
            Assert.AreEqual(0, store.Load("123").Profiles.Count, "Saving brand defaults must not mass-edit individual product profiles.");
            Assert.AreEqual(4, new CatalogStore(dir).Products().Count);
        });
    }

    [TestMethod]
    public void SelectAllIncludesEveryNormalizedBrandAndClearRemovesTheWriteTargets()
    {
        InSta(dir =>
        {
            CreateCatalog(dir);
            new CatalogStore(dir).CreateManual(new() { Sku = "A-3", Name = "Alpha three", Brand = "alpha", Currency = "TRY" });
            var panel = new TrendyolWorkspacePanel(dir);
            var brands = Find<ListBox>(panel, "TrendyolSafetyBrands");
            var search = Find<TextBox>(panel, "TrendyolSafetyBrandSearch");
            search.Text = "Beta";
            Assert.AreEqual(1, brands.Items.Count);
            Click(Find<Button>(panel, "TrendyolSafetySelectAll"));
            Assert.AreEqual("", search.Text, "Selecting all brands must not silently mean only search results.");
            Assert.AreEqual(3, brands.SelectedItems.Count, "Different capitalization must not duplicate a brand.");
            StringAssert.Contains(Find<TextBlock>(panel, "TrendyolSafetySelectionSummary").Text, "5 ürün");
            Find<TextBox>(panel, "TrendyolSafetyField1116").Text = "Use as directed";
            Assert.IsTrue(Find<Button>(panel, "TrendyolSaveBrandSafety").IsEnabled);
            Click(Find<Button>(panel, "TrendyolSafetyClearSelection"));
            Assert.AreEqual(0, brands.SelectedItems.Count);
            Assert.AreEqual(0, Find<DataGrid>(panel, "TrendyolSafetyReview").Items.Count);
            Assert.IsFalse(Find<Button>(panel, "TrendyolSaveBrandSafety").IsEnabled);
        });
    }

    [TestMethod]
    public void StaleBrandDraftCannotOverwriteNewerWorkspaceSettings()
    {
        InSta(dir =>
        {
            CreateCatalog(dir);
            var panel = new TrendyolWorkspacePanel(dir);
            Find<ListBox>(panel, "TrendyolSafetyBrands").SelectedItem = "Alpha";
            Find<TextBox>(panel, "TrendyolSafetyField1198").Text = "Stale producer";
            var store = new TrendyolWorkspaceStore(dir);
            var newer = store.Load("123");
            newer.Templates.Add(new("delivery", "New delivery", "", null, null, null));
            store.Save(newer);
            var revision = store.Load("123").Revision;
            Click(Find<Button>(panel, "TrendyolSaveBrandSafety"));
            Assert.AreEqual(revision, store.Load("123").Revision);
            Assert.AreEqual("New delivery", store.Load("123").Templates.Single().Name);
            var reopened = new TrendyolWorkspacePanel(dir);
            Find<ListBox>(reopened, "TrendyolSafetyBrands").SelectedItem = "Alpha";
            Assert.AreEqual("", Find<TextBox>(reopened, "TrendyolSafetyField1198").Text);
        });
    }

    [TestMethod]
    public void RemovingBrandFieldRequiresExplicitFieldAndBrandSelections()
    {
        InSta(dir =>
        {
            CreateCatalog(dir);
            var panel = new TrendyolWorkspacePanel(dir);
            var brands = Find<ListBox>(panel, "TrendyolSafetyBrands");
            var field = Find<ComboBox>(panel, "TrendyolSafetyClearField");
            var remove = Find<Button>(panel, "TrendyolClearBrandSafetyField");
            Assert.AreEqual(-1, field.SelectedIndex, "A destructive field choice must never be selected automatically.");
            Assert.IsFalse(remove.IsEnabled);
            brands.SelectedItem = "Alpha";
            Assert.IsFalse(remove.IsEnabled, "Selecting a brand alone does not choose the field to remove.");
            field.SelectedItem = field.Items.Cast<TrendyolSafetyField>().Single(f => f.Id == 1198);
            Assert.IsTrue(remove.IsEnabled);
            field.SelectedIndex = -1;
            Assert.IsFalse(remove.IsEnabled);
            field.SelectedItem = field.Items.Cast<TrendyolSafetyField>().Single(f => f.Id == 1116);
            brands.UnselectAll();
            Assert.IsFalse(remove.IsEnabled, "Removing the last target must disable the destructive action.");
            Assert.AreEqual(0, new TrendyolWorkspaceStore(dir).Load("123").BrandSafetyTemplates.Count, "Choosing removal targets is not a write.");
        });
    }

    [TestMethod]
    public void SelectingBrandShowsManufacturerFieldsAfterScrollingToImporters()
    {
        InSta(dir =>
        {
            CreateCatalog(dir);
            var store = new TrendyolWorkspaceStore(dir);
            store.SaveBrandSafety("123", store.Load("123").Revision, new[] { "Alpha" },
                new() { [1198] = "Alpha producer", [1294] = "producer@example.test", [1296] = "Factory address" });
            var panel = new TrendyolWorkspacePanel(dir);
            Find<TabControl>(panel, "TrendyolSections").SelectedIndex = 1;
            Find<TabControl>(panel, "TrendyolSettingsSections").SelectedIndex = 3;
            panel.Measure(new Size(1140, 600));
            panel.Arrange(new Rect(0, 0, 1140, 600));
            panel.UpdateLayout();
            var manufacturer = Find<TextBox>(panel, "TrendyolSafetyField1198");
            var scroll = Walk(panel).OfType<ScrollViewer>().Single(s => Walk(s).Contains(manufacturer));
            scroll.ScrollToBottom();
            panel.UpdateLayout();
            Assert.IsTrue(scroll.VerticalOffset > 0, "Reproduce the form scrolled to importer fields.");

            Find<ListBox>(panel, "TrendyolSafetyBrands").SelectedItem = "Alpha";
            panel.UpdateLayout();

            Assert.AreEqual("Alpha producer", manufacturer.Text);
            Assert.AreEqual(0d, scroll.VerticalOffset, "Selecting a brand must reveal the saved manufacturer fields.");
            var y = manufacturer.TranslatePoint(new Point(0, 0), scroll).Y;
            Assert.IsTrue(y >= 0 && y + manufacturer.ActualHeight <= scroll.ViewportHeight,
                "The saved manufacturer must be inside the visible form.");
            Assert.AreEqual(0, store.Receipts("123").Count);
        });
    }

    [TestMethod]
    public void SavedBrandCoverageRemainsVisibleWhenEmptyOrMultipleBrandsAreSelected()
    {
        InSta(dir =>
        {
            CreateCatalog(dir);
            var store = new TrendyolWorkspaceStore(dir);
            store.SaveBrandSafety("123", store.Load("123").Revision, new[] { "Alpha" },
                new() { [1198] = "Alpha producer", [1294] = "producer@example.test" });
            var panel = new TrendyolWorkspacePanel(dir);
            var coverage = Walk(panel).OfType<TextBlock>().SingleOrDefault(t => t.Name == "TrendyolSafetyCoverage");
            Assert.IsNotNull(coverage, "The screen needs a saved-record summary independent of blank bulk inputs.");
            StringAssert.Contains(coverage.Text, "1 / 3");
            var brands = Find<ListBox>(panel, "TrendyolSafetyBrands");
            brands.SelectedItem = "Beta";
            var heading = Find<TextBlock>(panel, "TrendyolSafetyFormHeading");
            StringAssert.Contains(heading.Text, "Beta");
            StringAssert.Contains(heading.Text, "kayıtlı bilgi yok");
            brands.SelectedItem = "Alpha";
            StringAssert.Contains(heading.Text, "2 kayıtlı alan");
            brands.SelectedItems.Add("Beta");
            StringAssert.Contains(coverage.Text, "1 / 3");
            Assert.AreEqual("", Find<TextBox>(panel, "TrendyolSafetyField1198").Text,
                "Saved brand values must not become shared values for unrelated brands.");
            StringAssert.Contains(heading.Text, "2 marka");
            Assert.IsFalse(Find<Button>(panel, "TrendyolSaveBrandSafety").IsEnabled);
            Assert.AreEqual(0, store.Receipts("123").Count);
        });
    }

    [TestMethod]
    public void BrandListRemainsFullyVisibleAtDesktopPanelSize()
    {
        InSta(dir =>
        {
            CreateCatalog(dir);
            var panel = new TrendyolWorkspacePanel(dir);
            Find<TabControl>(panel, "TrendyolSections").SelectedIndex = 1;
            Find<TabControl>(panel, "TrendyolSettingsSections").SelectedIndex = 3;
            Click(Find<Button>(panel, "TrendyolSafetySelectAll"));
            panel.Measure(new Size(1140, 600));
            panel.Arrange(new Rect(0, 0, 1140, 600));
            panel.UpdateLayout();
            var brands = Find<ListBox>(panel, "TrendyolSafetyBrands");
            var slot = LayoutInformation.GetLayoutSlot(brands);
            Assert.IsTrue(brands.ActualHeight >= 120, $"The brand list has only {brands.ActualHeight} px height.");
            Assert.IsTrue(slot.Height >= brands.ActualHeight + brands.Margin.Top + brands.Margin.Bottom - 1,
                $"The brand list is clipped: {brands.ActualHeight} px content in a {slot.Height} px layout slot.");
            var bottom = brands.TranslatePoint(new Point(0, brands.ActualHeight), panel).Y;
            Assert.IsTrue(bottom <= panel.ActualHeight - 40,
                $"The brand list extends below the settings area: {bottom} px in a {panel.ActualHeight} px panel.");
        });
    }

    static void CreateCatalog(string dir)
    {
        new TrendyolSettingsStore(Path.Combine(dir, "trendyol.bin")).Save(new("123", "test-key", "test-secret", "123 - Self Integration"));
        var catalog = new CatalogStore(dir);
        catalog.CreateManual(new() { Sku = "A-1", Name = "Alpha one", Brand = "Alpha", Currency = "TRY" });
        catalog.CreateManual(new() { Sku = "A-2", Name = "Alpha two", Brand = "Alpha", Currency = "TRY" });
        catalog.CreateManual(new() { Sku = "B-1", Name = "Beta one", Brand = "Beta", Currency = "TRY" });
        catalog.CreateManual(new() { Sku = "G-1", Name = "Gamma one", Brand = "Gamma", Currency = "TRY" });
    }

    static T Find<T>(DependencyObject root, string name) where T : FrameworkElement => Walk(root).OfType<T>().Single(e => e.Name == name);
    static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var item in Walk(child)) yield return item;
    }

    static void InSta(Action<string> action)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            var dir = Path.Combine(Path.GetTempPath(), "trendyol-safety-ui-" + Guid.NewGuid().ToString("N"));
            try { action(dir); }
            catch (Exception ex) { failure = ex; }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw failure;
    }
}
