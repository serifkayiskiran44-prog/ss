using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #831 in the real XML page with a 300-row preview: the toolbar reads the selection's counts and warnings, apply is
// enabled for a fresh clean selection and stays where it is while the grid scrolls to the last row; an empty
// selection disables apply with the reason; a draft change makes the preview stale -- the toolbar says so, apply
// is disabled, and the apply's own revision check still refuses; recomputing makes it fresh again.
[TestClass]
public sealed class PreviewActionToolbarWindowTests
{
    [TestMethod]
    public void TheToolbarStaysPutReadsTheSelectionAndMirrorsTheRevisionCheck()
    {
        var root = Path.Combine(Path.GetTempPath(), "toolbar-" + Guid.NewGuid().ToString("N"));
        Exception failure = null;
        var thread = new Thread(() =>
        {
            MainWindow window = null;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try
            {
                Directory.CreateDirectory(root);
                var feed = Path.Combine(root, "feed.xml");
                File.WriteAllText(feed, "<Products>" + string.Concat(Enumerable.Range(1, 300).Select(i => $"<Product><Code>S{i:D3}</Code><Title>Ürün {i:D3}</Title><Cost>4</Cost><Stock>30</Stock>{(i % 10 == 0 ? "" : "<Desc>Uzun bir açıklama metni.</Desc>")}<Img>https://cdn.example.com/{i}.jpg</Img></Product>")) + "</Products>");
                var store = new CatalogStore(root);
                store.SaveSource(new XmlSource { Id = Guid.NewGuid().ToString("N"), Name = "Fixture feed", Location = feed, ItemPath = "/Products/Product", PriceMode = "Simple", ExchangeRate = 1, AutoFx = false, Currency = "TRY", CostCurrency = "TRY",
                    Fields = new Dictionary<string, string> { ["Sku"] = "Code", ["Name"] = "Title", ["Cost"] = "Cost", ["Stock"] = "Stock", ["Description"] = "Desc", ["ImageUrls"] = "Img" } });
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

                window = new MainWindow(root) { Width = 1100, Height = 700 }; window.Show();
                typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { "xml", true });
                Drain(window);
                var sources = (ListBox)Field("sources"); sources.SelectedItem = sources.Items.OfType<XmlSource>().Single(); Drain(window);
                var preview = (DataGrid)Field("preview"); var toolbar = (Border)Field("previewToolbar"); var apply = (Button)Field("previewApplyButton"); var recompute = (Button)Field("previewRecomputeButton");
                object Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                PreviewToolbarModel Model() => (PreviewToolbarModel)Field("lastPreviewToolbar");

                Assert.IsFalse(apply.IsEnabled, "Nothing read yet: no apply."); StringAssert.Contains(Model().StatusText, "XML okunmadı");
                Run("InspectAsync"); Assert.IsFalse(apply.IsEnabled); StringAssert.Contains(Model().StatusText, "hesaplanmadı");
                Run("PreviewAsync");
                preview.SelectAll(); Drain(window);
                var m = Model();
                Assert.AreEqual("Önizleme güncel", m.StatusText);
                Assert.IsTrue(apply.IsEnabled, m.ApplyReason);
                Assert.AreEqual("300 / 300 seçili · etkilenen 300 (300 yeni, 0 değişen) · 0 aynı", m.CountsText);
                Assert.AreEqual("0 engel · 30 uyarı (seçili)", m.ValidationText, "Every tenth row lacks a description: a warning, applied anyway.");
                StringAssert.Contains((string)apply.Content, "(300)");

                // Long grid: scroll to the last row -- the toolbar has not moved and is still visible.
                var before = toolbar.TransformToAncestor(window).Transform(new Point(0, 0));
                preview.ScrollIntoView(preview.Items[299]); Drain(window);
                var after = toolbar.TransformToAncestor(window).Transform(new Point(0, 0));
                Assert.AreEqual(before, after, "The toolbar is docked above the scrolling grid, not inside it.");
                Assert.IsTrue(toolbar.IsVisible && toolbar.ActualHeight > 0);
                Assert.IsTrue(apply.Focusable && recompute.Focusable, "Actions are keyboard-reachable.");

                preview.UnselectAll(); Drain(window);
                Assert.IsFalse(apply.IsEnabled); StringAssert.Contains(Model().ApplyReason, "satır seçin"); Assert.AreEqual("seçim yok", Model().ValidationText);
                preview.SelectAll(); Drain(window); Assert.IsTrue(apply.IsEnabled);

                // A draft change (markup) invalidates the preview: the toolbar says stale, apply is off, and the apply itself still refuses.
                var draft = (XmlSource)Field("source"); draft.MarkupPercent += 1;
                typeof(MainWindow).GetMethod("RefreshImportStepper", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null); Drain(window);
                Assert.IsFalse(apply.IsEnabled, "A stale preview cannot be applied from the toolbar.");
                StringAssert.Contains(Model().StatusText, "Önizleme geçersiz"); Assert.AreEqual(SeverityLevel.Warning, Model().StatusLevel);
                var refused = (Task)typeof(MainWindow).GetMethod("ImportAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
                for (var i = 0; i < 200 && !refused.IsCompleted; i++) { Drain(window); Thread.Sleep(25); }
                Assert.IsTrue(refused.IsFaulted, "The apply's own revision check is untouched.");
                StringAssert.Contains(refused.Exception!.GetBaseException().Message, "önizleme geçersiz");
                Assert.AreEqual(0, new CatalogStore(root).Products().Count, "Nothing was written.");

                Assert.IsTrue(recompute.IsEnabled);
                Run("PreviewAsync"); preview.SelectAll(); Drain(window);
                Assert.IsTrue(apply.IsEnabled, Model().ApplyReason); Assert.AreEqual("Önizleme güncel", Model().StatusText);

                void Run(string method)
                {
                    var task = (Task)typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
                    for (var i = 0; i < 400 && !task.IsCompleted; i++) { Drain(window); Thread.Sleep(25); }
                    Assert.IsTrue(task.IsCompletedSuccessfully, method + " must finish: " + task.Exception?.GetBaseException().Message);
                    Drain(window);
                }
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                try { window?.Close(); if (window is not null) Drain(window); } catch (Exception) { }
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                    try { Directory.Delete(root, true); break; }
                    catch (IOException) { Thread.Sleep(300); }
                    catch (UnauthorizedAccessException) { Thread.Sleep(300); }
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new AssertFailedException(failure.ToString());
    }

    static void Drain(Window window) { window.UpdateLayout(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
}
