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

// #832 in the real XML page: the row diff of a previewed row against the pool product it matches by SKU renders
// one focusable border per field whose glyph and word say changed / removed / added / unchanged, the pool's
// description with a pasted token is masked, a long value is capped, and a row with no match is all "eklendi".
[TestClass]
public sealed class FieldDiffWindowTests
{
    [TestMethod]
    public void TheRowDiffReadsWithoutColourMasksValuesAndIsKeyboardReachable()
    {
        var root = Path.Combine(Path.GetTempPath(), "rowdiff-" + Guid.NewGuid().ToString("N"));
        Exception failure = null;
        var thread = new Thread(() =>
        {
            MainWindow window = null;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try
            {
                Directory.CreateDirectory(root);
                var feed = Path.Combine(root, "feed.xml");
                File.WriteAllText(feed, "<Products>" +
                    "<Product><Code>S001</Code><Title>Yeni başlık</Title><Brand>Acme</Brand><Cost>4</Cost><Stock>30</Stock><Img>https://cdn.example.com/1.jpg</Img></Product>" +
                    "<Product><Code>S002</Code><Title>Yepyeni</Title><Cost>4</Cost><Stock>30</Stock><Desc>Uzun bir açıklama metni.</Desc><Img>https://cdn.example.com/2.jpg</Img></Product>" +
                    "</Products>");
                var store = new CatalogStore(root);
                var sourceId = Guid.NewGuid().ToString("N");
                var xmlSource = new XmlSource { Id = sourceId, Name = "Fixture feed", Location = feed, ItemPath = "/Products/Product", PriceMode = "Simple", ExchangeRate = 1, AutoFx = false, Currency = "TRY", CostCurrency = "TRY",
                    Fields = new Dictionary<string, string> { ["Sku"] = "Code", ["Name"] = "Title", ["Brand"] = "Brand", ["Cost"] = "Cost", ["Stock"] = "Stock", ["Description"] = "Desc", ["ImageUrls"] = "Img" } };
                store.SaveSource(xmlSource);
                // The pool is seeded through the store's own import (SaveProduct only updates): S001 exists with an old title, a description carrying a pasted token, another price and stock.
                store.Import(xmlSource, new List<CatalogProduct> { new() { Sku = "S001", Name = "Eski başlık", Description = "Eski açıklama token=SECRETXYZ123 " + new string('x', 600), Price = 10, Currency = "TRY", Cost = 4, CostCurrency = "TRY", Stock = 5, SourceId = sourceId, SourceKind = "xml" } });
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

                window = new MainWindow(root); window.Show();
                typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { "xml", true });
                Drain(window);
                var sources = (ListBox)Field("sources"); sources.SelectedItem = sources.Items.OfType<XmlSource>().Single(); Drain(window);
                var preview = (DataGrid)Field("preview"); var diffButton = (Button)Field("previewDiffButton");
                object Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                Window Build() => (Window)typeof(MainWindow).GetMethod("BuildPreviewRowDiff", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;

                Assert.IsFalse(diffButton.IsEnabled, "No preview, no diff.");
                Run("InspectAsync"); Run("PreviewAsync");
                preview.SelectAll(); Drain(window);
                Assert.IsFalse(diffButton.IsEnabled, "The diff is one row against one product.");
                preview.UnselectAll(); preview.SelectedItem = preview.Items.OfType<CatalogProduct>().Single(x => x.Sku == "S001"); Drain(window);
                Assert.IsTrue(diffButton.IsEnabled);

                var dialog = Build();
                var borders = Descendants((Panel)dialog.Content).OfType<Border>().Where(b => b.Tag is DiffKind).ToList();
                Assert.AreEqual(FieldDiff.ProductFields.Count, borders.Count, "One border per field, unchanged ones included.");
                Assert.IsTrue(borders.All(b => b.Focusable && System.Windows.Input.KeyboardNavigation.GetIsTabStop(b)), "Every row is keyboard-reachable.");
                string Text(Border b) => string.Join(" | ", Descendants(b).OfType<TextBlock>().Select(t => t.Text));
                Border Row(string field) => borders.Single(b => Text(b).Contains(" " + field + " · "));
                StringAssert.Contains(Text(Row("Başlık")), "△ Başlık · değişti"); StringAssert.Contains(Text(Row("Başlık")), "Önce: Eski başlık"); StringAssert.Contains(Text(Row("Başlık")), "Sonra: Yeni başlık");
                StringAssert.Contains(Text(Row("Açıklama")), "－ Açıklama · kaldırıldı"); StringAssert.Contains(Text(Row("Açıklama")), "(kısaltıldı)", "A 600-character value is capped and says so.");
                Assert.IsFalse(Text(Row("Açıklama")).Contains("SECRETXYZ123"), Text(Row("Açıklama")));
                StringAssert.Contains(Text(Row("Marka")), "＋ Marka · eklendi"); Assert.IsFalse(Text(Row("Marka")).Contains("Önce:"), "An added field has no before.");
                StringAssert.Contains(Text(Row("Fiyat")), "değişti"); StringAssert.Contains(Text(Row("Stok")), "△ Stok · değişti");
                StringAssert.Contains(Text(Row("Barkod")), "＝ Barkod · aynı");
                Assert.AreEqual(DiffKind.Changed, (DiffKind)Row("Başlık").Tag); Assert.AreEqual(DiffKind.Removed, (DiffKind)Row("Açıklama").Tag);
                Assert.IsTrue(Row("Açıklama").BorderThickness.Left > Row("Barkod").BorderThickness.Left, "A removal is a heavier border than an unchanged row.");
                var name = System.Windows.Automation.AutomationProperties.GetName(Row("Başlık"));
                StringAssert.Contains(name, "Başlık: değişti");
                Assert.AreEqual(1, new CatalogStore(root).Products().Count, "The diff wrote nothing.");
                dialog.Close();

                preview.SelectedItem = preview.Items.OfType<CatalogProduct>().Single(x => x.Sku == "S002"); Drain(window);
                var fresh = Build();
                var freshBorders = Descendants((Panel)fresh.Content).OfType<Border>().Where(b => b.Tag is DiffKind).ToList();
                Assert.IsTrue(freshBorders.All(b => (DiffKind)b.Tag is DiffKind.Added or DiffKind.Unchanged), "No match: every present field is added, the empty ones unchanged.");
                StringAssert.Contains(string.Join(" ", Descendants((Panel)fresh.Content).OfType<TextBlock>().Select(t => t.Text)), "eşleşen ürün yok");
                fresh.Close();

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

    // The dialog is built, not shown: only the logical tree exists, so walk that (content, children, child).
    static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is not DependencyObject d) continue;
            yield return d;
            foreach (var g in Descendants(d)) yield return g;
        }
    }

    static void Drain(Window window) { window.UpdateLayout(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
}
