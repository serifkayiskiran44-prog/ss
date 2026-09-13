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
using TrMarketplaceHubDesktop.Catalog;

// #845 on the real matrix panel: the bulk command refuses an empty selection on the error surface; a whole store
// column (60 products) opens the drawer with that store as target, lists a page and says how many more; apply
// without the approval box does nothing; the approved apply writes local plans only and the panel refreshes; a
// second preview of the same products reports them all as already identical and refuses. On the drawer itself: a
// store no longer offered, a matrix that changed since the preview, an already applied preview and Cancel write
// nothing; a clean apply writes and closes.
[TestClass]
public sealed class ChannelMatrixBulkUiTests
{
    [TestMethod]
    public void TheBulkCommandPreviewsTheSelectionAppliesLocalPlansWithApprovalAndEveryGuardRefusesWithoutWriting()
    {
        var root = Path.Combine(Path.GetTempPath(), "matrix-bulk-" + Guid.NewGuid().ToString("N"));
        RunSta(() =>
        {
            try
            {
                Directory.CreateDirectory(root);
                var catalog = new CatalogStore(root);
                var source = new XmlSource { Id = Guid.NewGuid().ToString("N"), Name = "seed", Location = "https://seed.example.com/f.xml", ItemPath = "/p", PriceMode = "Simple", ExchangeRate = 1, AutoFx = false, Currency = "TRY", CostCurrency = "TRY", Fields = new Dictionary<string, string> { ["Sku"] = "s", ["Name"] = "n" } };
                catalog.SaveSource(source);
                catalog.Import(source, Enumerable.Range(0, 60).Select(i => new CatalogProduct { Sku = $"S{i:D2}", Name = $"Ürün {i:D2}", Price = 5, Currency = "TRY", Stock = 3, SourceId = source.Id, SourceKind = "xml", Description = "Uzun bir açıklama metni.", ImageUrls = $"https://cdn.example.com/{i}.jpg" }).ToList());
                var connections = new MarketplaceConnectionStore(root);
                connections.Save("etsy", "S1", "Etsy S1", true); connections.Save("trendyol", "T1", "Trendyol T1", true);
                SqliteConnection.ClearAllPools();
                var plans = new ChannelProductsStore(root);

                var panel = ChannelListingMatrixPanel.Create(root, null, () => new[] { DashboardStoreFilter.KeyFor("etsy", "S1"), DashboardStoreFilter.KeyFor("trendyol", "T1") });
                var window = new Window { Content = panel, Width = 1200, Height = 800, WindowStartupLocation = WindowStartupLocation.Manual, Left = 0, Top = 0 };
                try
                {
                    window.Show();
                    var matrix = Descendants(panel).OfType<DataGrid>().Single(g => (string)g.Tag == "channel-matrix");
                    WaitUntil(window, () => matrix.Items.Count > 0, "matrix load");
                    Assert.AreEqual(60, matrix.Items.Count);
                    var bulk = Descendants(panel).OfType<Button>().Single(b => (string?)b.Tag == "channel-matrix-bulk");
                    string PanelStatus() => Descendants(panel).OfType<TextBlock>().Select(t => t.Text).FirstOrDefault(t => t.Contains(" satır · ")) ?? "";
                    DataGridColumn EtsyColumn() => matrix.Columns.Single(c => c.Header is string h && h.StartsWith("Etsy", StringComparison.OrdinalIgnoreCase));

                    // Empty selection: refused on the error surface, no drawer opens.
                    matrix.SelectedCells.Clear();
                    bulk.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(window);
                    Assert.IsTrue(Descendants(panel).OfType<Border>().Any(b => AutomationProperties.GetName(b).Contains("Seçim boş")), "An empty selection is refused before any drawer opens.");
                    Assert.AreEqual(0, window.OwnedWindows.Count);

                    // Large selection: the whole Etsy column -> the drawer targets Etsy, lists 50 of 60 and says "… ve 10 daha".
                    foreach (var item in matrix.Items) matrix.SelectedCells.Add(new DataGridCellInfo(item, EtsyColumn()));
                    Assert.AreEqual(60, matrix.SelectedCells.Count, "Extended cell selection lets a whole store column be picked.");
                    var failure = DriveModal(window, () => bulk.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)), new Func<Window, bool>[]
                    {
                        d => Reason(d).Text == "Hedef kategori girin, sonra Önizle.",
                        d =>
                        {
                            Assert.AreEqual("Etsy · S1", ((ChannelMatrixBulkTarget)Target(d).SelectedItem!).Label, "The store every selected cell shares is the default target.");
                            Assert.IsFalse(Approve(d).IsEnabled, "Nothing to approve before a preview exists.");
                            Category(d).Text = "Kupa"; PreviewButton(d).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); return true;
                        },
                        d => Summary(d).Text.StartsWith("Hedef: Etsy · S1 · 60 seçili ürün", StringComparison.Ordinal),
                        d =>
                        {
                            Assert.AreEqual("Hedef: Etsy · S1 · 60 seçili ürün · 60 uygulanacak · 0 zaten aynı · 0 engelli", Summary(d).Text);
                            Assert.AreEqual(ChannelMatrixBulk.MaxLinesShown, Lines(d).Items.Count); StringAssert.StartsWith(More(d).Text, "… ve 10 daha"); Assert.AreEqual(Visibility.Visible, More(d).Visibility);
                            StringAssert.Contains((string)Lines(d).Items[0]!, "plan yok → ilan=; kategori=Kupa");
                            Assert.IsTrue(Approve(d).IsEnabled && Approve(d).IsChecked != true);
                            ApplyButton(d).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                            Assert.AreEqual("Uygulamak için onay kutusunu işaretleyin.", Status(d).Text); Assert.AreEqual(0, plans.List().Count, "No approval, no write.");
                            Approve(d).IsChecked = true; ApplyButton(d).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); return true;
                        },
                    });
                    if (failure is not null) throw failure;
                    var written = plans.List("etsy", "S1");
                    Assert.AreEqual(60, written.Count, "Every selected product got a local plan in the target store."); Assert.IsTrue(written.All(p => p.TargetCategory == "Kupa" && p.ListingId == ""));
                    Assert.AreEqual(0, plans.List("trendyol", "T1").Count, "The other store is untouched.");
                    WaitUntil(window, () => PanelStatus().StartsWith("Son toplu plan: 60 yazıldı · 0 atlandı · 0 engelli", StringComparison.Ordinal), "panel refresh after apply");

                    // The same products again: every line is already identical -> nothing to apply, approval stays off, Cancel closes.
                    matrix.SelectedCells.Clear(); foreach (var item in matrix.Items) matrix.SelectedCells.Add(new DataGridCellInfo(item, EtsyColumn()));
                    failure = DriveModal(window, () => bulk.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)), new Func<Window, bool>[]
                    {
                        d => Reason(d).Text == "Hedef kategori girin, sonra Önizle.",
                        d => { Category(d).Text = "Kupa"; PreviewButton(d).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); return true; },
                        d => Summary(d).Text.StartsWith("Hedef: Etsy · S1 · 60 seçili ürün", StringComparison.Ordinal),
                        d =>
                        {
                            Assert.AreEqual("Hedef: Etsy · S1 · 60 seçili ürün · 0 uygulanacak · 60 zaten aynı · 0 engelli", Summary(d).Text);
                            StringAssert.Contains(Reason(d).Text, "Uygulanacak değişiklik yok"); Assert.IsFalse(Approve(d).IsEnabled);
                            ApplyButton(d).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); StringAssert.Contains(Status(d).Text, "Uygulanacak değişiklik yok");
                            CancelButton(d).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); return true;
                        },
                    });
                    if (failure is not null) throw failure;
                    Assert.AreEqual(60, plans.List().Count, "A no-change preview and Cancel wrote nothing.");

                    // The drawer's own guards, on a drawer built for three of those products with a changeable world around it.
                    var etsy = new ChannelMatrixColumn("etsy|S1", "etsy", "Etsy", "S1"); var trendyol = new ChannelMatrixColumn("trendyol|T1", "trendyol", "Trendyol", "T1");
                    var three = matrix.Items.OfType<ChannelMatrixRow>().Take(3).ToList();
                    var allowed = new HashSet<string> { "etsy|S1", "trendyol|T1" }; var revisionNow = "rev-A"; var applied = new HashSet<Guid>(); BulkProductApplyResult? result = null;
                    Window Drawer() => ChannelMatrixBulkDrawer.Build(null, new ChannelMatrixBulkDrawer.Context(three, new[] { etsy, trendyol }, etsy, "rev-A", () => revisionNow, () => allowed, catalog, plans, applied), r => result = r);
                    void PreviewTabak(Window d)
                    {
                        d.Show(); WaitUntil(d, () => Reason(d).Text == "Hedef kategori girin, sonra Önizle.", "loaded preview");
                        Category(d).Text = "Tabak"; PreviewButton(d).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        WaitUntil(d, () => Summary(d).Text.StartsWith("Hedef: Etsy · S1 · 3 seçili ürün · 3 uygulanacak", StringComparison.Ordinal), "preview");
                        Approve(d).IsChecked = true;
                    }
                    int Tabak() => plans.List("etsy", "S1").Count(p => p.TargetCategory == "Tabak");

                    var wrongStore = Drawer(); PreviewTabak(wrongStore);
                    allowed.Remove("etsy|S1");
                    ApplyButton(wrongStore).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(wrongStore);
                    StringAssert.Contains(Status(wrongStore).Text, "yanlış mağazaya"); Assert.AreEqual(0, Tabak(), "A store the shell no longer offers is refused at apply time."); Assert.IsTrue(wrongStore.IsVisible); Assert.AreNotEqual(true, Approve(wrongStore).IsChecked);
                    wrongStore.Close(); allowed.Add("etsy|S1");

                    var stale = Drawer(); PreviewTabak(stale);
                    revisionNow = "rev-B";
                    ApplyButton(stale).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(stale);
                    StringAssert.Contains(Status(stale).Text, "bayat revizyon"); Assert.AreEqual(0, Tabak(), "A matrix that changed since the preview is refused.");
                    stale.Close(); revisionNow = "rev-A";

                    var repeated = Drawer(); PreviewTabak(repeated);
                    applied.Add(((BulkProductPreview)repeated.Tag).Id);
                    ApplyButton(repeated).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(repeated);
                    StringAssert.Contains(Status(repeated).Text, "zaten uygulandı"); Assert.AreEqual(0, Tabak(), "An already applied preview never applies twice.");
                    repeated.Close(); applied.Clear();

                    var cancelled = Drawer(); PreviewTabak(cancelled);
                    CancelButton(cancelled).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); Drain(window);
                    Assert.IsFalse(cancelled.IsVisible, "Cancel closes."); Assert.AreEqual(0, Tabak(), "Cancel writes nothing."); Assert.IsNull(result);

                    var clean = Drawer(); PreviewTabak(clean);
                    ApplyButton(clean).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                    WaitUntil(window, () => !clean.IsVisible, "apply to finish and close");
                    Assert.AreEqual(3, Tabak(), "The clean apply wrote the three local plans."); Assert.AreEqual(60, plans.List("etsy", "S1").Count, "...by updating, not duplicating.");
                    Assert.IsNotNull(result); Assert.AreEqual(3, result!.Applied); Assert.AreEqual(1, applied.Count, "The applied preview is remembered.");
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

    static T Tagged<T>(Window d, string tag) where T : FrameworkElement => Descendants(d).OfType<T>().Single(e => (string?)e.Tag == tag);
    static TextBlock Summary(Window d) => Tagged<TextBlock>(d, "channel-matrix-bulk-summary");
    static TextBlock Reason(Window d) => Tagged<TextBlock>(d, "channel-matrix-bulk-reason");
    static TextBlock Status(Window d) => Tagged<TextBlock>(d, "channel-matrix-bulk-status");
    static TextBlock More(Window d) => Tagged<TextBlock>(d, "channel-matrix-bulk-more");
    static ItemsControl Lines(Window d) => Tagged<ItemsControl>(d, "channel-matrix-bulk-lines");
    static ComboBox Target(Window d) => Tagged<ComboBox>(d, "channel-matrix-bulk-target");
    static TextBox Category(Window d) => Tagged<TextBox>(d, "channel-matrix-bulk-category");
    static CheckBox Approve(Window d) => Tagged<CheckBox>(d, "channel-matrix-bulk-approve");
    static Button PreviewButton(Window d) => Tagged<Button>(d, "channel-matrix-bulk-preview");
    static Button ApplyButton(Window d) => Descendants(d).OfType<Button>().Single(b => (string?)b.Content == ChannelMatrixBulkDrawer.ApplyLabel);
    static Button CancelButton(Window d) => Descendants(d).OfType<Button>().Single(b => (string?)b.Content == ChannelMatrixBulkDrawer.CancelLabel);

    /// <summary>Drives the modal dialog <paramref name="open"/> shows: polls inside ShowDialog's nested loop until the owner has a visible owned window, runs the steps in order (a step returns true when done), then waits for the dialog to close. A failing step closes the dialog and its exception is returned.</summary>
    static Exception? DriveModal(Window owner, Action open, IReadOnlyList<Func<Window, bool>> steps, int timeoutMs = 30000)
    {
        Exception? failure = null; Window? dialog = null; var index = 0; var deadline = Environment.TickCount64 + timeoutMs;
        void Pump()
        {
            if (failure is not null) return;
            try
            {
                if (Environment.TickCount64 > deadline) throw new TimeoutException($"Dialog step {index} did not complete in time.");
                dialog ??= owner.OwnedWindows.OfType<Window>().FirstOrDefault(w => w.IsVisible);
                if (dialog is not null)
                {
                    if (!dialog.IsVisible) { if (index < steps.Count) throw new AssertFailedException($"The dialog closed before step {index}."); return; }
                    if (index < steps.Count && steps[index](dialog)) index++;
                }
            }
            catch (Exception ex) { failure = ex; try { dialog?.Close(); } catch (InvalidOperationException) { } return; }
            owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { Thread.Sleep(25); Pump(); }));
        }
        owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Pump));
        open();
        return failure;
    }

    static void WaitUntil(Window window, Func<bool> condition, string what)
    {
        for (var i = 0; i < 400; i++) { Drain(window); if (condition()) return; Thread.Sleep(25); }
        Assert.Fail($"Timed out waiting for {what}.");
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
