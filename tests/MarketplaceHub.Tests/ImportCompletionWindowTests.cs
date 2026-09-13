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

// #828 in the real XML page: a full apply ends in a Success summary with the store's own counts, the duration,
// the source revision and a route action; re-previewing and applying the same complete feed ends in No-op from
// the store's AlreadyApplied result. The summary is on the page -- the window does not leave it.
[TestClass]
public sealed class ImportCompletionWindowTests
{
    [TestMethod]
    public void ApplyEndsInASuccessSummaryAndTheSameFeedAgainEndsInNoOp()
    {
        var root = Path.Combine(Path.GetTempPath(), "completion-" + Guid.NewGuid().ToString("N"));
        Exception failure = null;
        var thread = new Thread(() =>
        {
            MainWindow window = null;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try
            {
                Directory.CreateDirectory(root);
                var feed = Path.Combine(root, "feed.xml");
                File.WriteAllText(feed, "<Products>" + string.Concat(Enumerable.Range(1, 3).Select(i => $"<Product><Code>S{i}</Code><Title>Ürün {i}</Title><Cost>4</Cost><Stock>30</Stock><Desc>Uzun bir açıklama metni.</Desc><Img>https://cdn.example.com/{i}.jpg</Img></Product>")) + "</Products>");
                var store = new CatalogStore(root);
                store.SaveSource(new XmlSource { Id = Guid.NewGuid().ToString("N"), Name = "Fixture feed", Location = feed, ItemPath = "/Products/Product", PriceMode = "Simple", ExchangeRate = 1, AutoFx = false, Currency = "TRY", CostCurrency = "TRY",
                    Fields = new Dictionary<string, string> { ["Sku"] = "Code", ["Name"] = "Title", ["Cost"] = "Cost", ["Stock"] = "Stock", ["Description"] = "Desc", ["ImageUrls"] = "Img" } });
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

                window = new MainWindow(root); window.Show();
                typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { "xml", true });
                Drain(window);
                var sources = (ListBox)typeof(MainWindow).GetField("sources", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                sources.SelectedItem = sources.Items.OfType<XmlSource>().Single(); Drain(window);
                var preview = (DataGrid)typeof(MainWindow).GetField("preview", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                var panel = (Border)typeof(MainWindow).GetField("importCompletionPanel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                Assert.AreEqual(Visibility.Collapsed, panel.Visibility, "No import has finished: no summary.");

                Run(window, "InspectAsync"); Run(window, "PreviewAsync");
                preview.SelectAll(); Drain(window);
                Run(window, "ImportAsync"); Drain(window);

                var first = (ImportCompletion)typeof(MainWindow).GetField("lastImportCompletion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                Assert.IsNotNull(first, "The terminal result composed a summary.");
                Assert.AreEqual(ImportOutcome.Success, first.Outcome);
                Assert.AreEqual(3, first.Added); Assert.AreEqual(3, first.Applied); Assert.AreEqual(0, first.Rejected);
                Assert.IsTrue(first.Duration > TimeSpan.Zero, "A real apply took time.");
                StringAssert.Contains(first.Revision, "Fixture feed"); StringAssert.Contains(first.Revision, "eşleme rev."); StringAssert.Contains(first.Revision, "akış ");
                Assert.IsFalse(first.Revision.Contains(feed) || first.Revision.Contains(root), "The source address never appears.");
                Assert.AreEqual(Visibility.Visible, panel.Visibility);
                var texts = ((StackPanel)panel.Child).Children.OfType<TextBlock>().Select(t => t.Text).ToList();
                Assert.IsTrue(texts.Any(t => t.Contains("Aktarım tamamlandı")), string.Join(" | ", texts));
                Assert.IsTrue(texts.Any(t => t.Contains("3 uygulandı")), string.Join(" | ", texts));
                var buttons = ((StackPanel)panel.Child).Children.OfType<WrapPanel>().Single().Children.OfType<Button>().ToList();
                Assert.IsTrue(buttons.Any(b => (string)b.Content == "Ürün havuzuna git"), "The success next action is the product pool.");
                Assert.AreEqual(3, store.Products().Count, "The store really has the rows.");

                Run(window, "PreviewAsync"); preview.SelectAll(); Drain(window);
                Run(window, "ImportAsync"); Drain(window);
                var second = (ImportCompletion)typeof(MainWindow).GetField("lastImportCompletion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
                Assert.AreEqual(ImportOutcome.NoOp, second.Outcome, "The same complete feed is AlreadyApplied in the store: a no-op, not a success.");
                Assert.AreEqual(0, second.Applied);
                Assert.IsTrue(((StackPanel)panel.Child).Children.OfType<TextBlock>().Any(t => t.Text.Contains("Değişiklik yok")));
                Assert.AreEqual(3, store.Products().Count);
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

    static void Run(MainWindow window, string method)
    {
        var task = (Task)typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
        for (var i = 0; i < 400 && !task.IsCompleted; i++) { Drain(window); Thread.Sleep(25); }
        Assert.IsTrue(task.IsCompletedSuccessfully, method + " must finish: " + task.Exception?.GetBaseException().Message);
    }

    static void Drain(Window window) { window.UpdateLayout(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); }
}
