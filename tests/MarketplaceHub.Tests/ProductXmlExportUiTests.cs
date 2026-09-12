using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

[TestClass]
public sealed class ProductXmlExportUiTests
{
    [TestMethod]
    public void ExportWritesAllMatchingProductsToTheChosenFile()
    {
        Run(f =>
        {
            var path = Path.Combine(f.Root, "export.xml");
            f.InvokeExport(path);

            var doc = XDocument.Load(path);
            var skus = doc.Root!.Elements("Product").Elements("Sku").Select(x => x.Value).ToList();
            CollectionAssert.AreEquivalent(new[] { "A", "B" }, skus);
        });
    }

    [TestMethod]
    public void ExportWithNoMatchingProductsThrowsInsteadOfWritingAnEmptyFile()
    {
        Run(f =>
        {
            var path = Path.Combine(f.Root, "export.xml");
            var thrown = Assert.ThrowsException<InvalidOperationException>(() => f.InvokeExport(path, search: "does-not-exist-anywhere"));
            StringAssert.Contains(thrown.Message, "yok");
            Assert.IsFalse(File.Exists(path));
        });
    }

    static void Run(Action<Fixture> test)
    {
        Exception failure = null;
        var thread = new Thread(() => { try { using var f = new Fixture(); test(f); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure != null) throw new AssertFailedException(failure.ToString());
    }

    sealed class Fixture : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "xml-export-ui-" + Guid.NewGuid().ToString("N"));
        public MainWindow Window;

        public Fixture()
        {
            var store = new CatalogStore(Root);
            var source = new XmlSource { Id = "fixture", Name = "Fixture" };
            store.Import(source, new[] {
                new CatalogProduct { SourceId = source.Id, Sku = "A", Name = "Product A", Price = 10, Stock = 10 },
                new CatalogProduct { SourceId = source.Id, Sku = "B", Name = "Product B", Price = 20, Stock = 5 }
            });
            Window = new MainWindow(Root); Window.Show();
            typeof(MainWindow).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(Window, new object[] { "products", true });
        }

        public void InvokeExport(string path, string search = "")
        {
            if (search.Length > 0)
            {
                var box = (TextBox)typeof(MainWindow).GetField("search", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Window);
                box.Text = search;
            }
            // ExportProductsToXmlFileAsync awaits Task.Run internally; without a running Dispatcher message loop
            // the continuation would resume on a thread-pool thread and violate WPF's dispatcher affinity.
            // Install a DispatcherSynchronizationContext and pump a nested frame until it completes (same
            // pattern as SchedulerDecoupleTests.InvokeScheduledAsync).
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Window.Dispatcher));
            var method = typeof(MainWindow).GetMethod("ExportProductsToXmlFileAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            var task = (Task)method.Invoke(Window, new object[] { path });
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());
            Dispatcher.PushFrame(frame);
            if (task.IsFaulted) throw task.Exception!.InnerException ?? task.Exception!;
        }

        public void Dispose()
        {
            Window.Close();
            for (var attempt = 0; attempt < 30; attempt++)
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { Directory.Delete(Root, true); break; }
                catch (IOException) { Thread.Sleep(300); }
                catch (UnauthorizedAccessException) { Thread.Sleep(300); }
            }
        }
    }
}
