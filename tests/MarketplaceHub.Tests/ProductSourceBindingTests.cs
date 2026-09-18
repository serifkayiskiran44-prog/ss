using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class ProductSourceBindingTests
{
    const string Feed = "<Items><Item><sku>SKU-1</sku><name>Product</name><price>10</price><stock>4</stock></Item></Items>";

    static string NewRoot() => Path.Combine(Path.GetTempPath(), "product-source-binding-" + Guid.NewGuid().ToString("N"));

    static XmlSource Source(string id, bool enabled = true) => new()
    {
        Id = id,
        Name = id,
        Location = "https://example.test/" + id + ".xml",
        Enabled = enabled,
        ItemPath = "/Items/Item",
        Currency = "USD",
        MarkupPercent = 0,
        SafetyStock = 0,
        MaximumStock = 100,
        Fields = new()
        {
            ["Sku"] = "sku",
            ["Name"] = "name",
            ["Cost"] = "price",
            ["Stock"] = "stock"
        }
    };

    static void Cleanup(string root)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    sealed class PausedXmlHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Resume.Task.WaitAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent(Feed) };
        }
    }

    static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    static object Invoke(object target, string name, params object[] arguments) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);

    static Task InvokeTask(object target, string name)
    {
        try { return (Task)Invoke(target, name)!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            return Task.FromException(error.InnerException);
        }
    }

    static void InSta(Func<string, Task> action)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            var root = NewRoot();
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            try
            {
                var task = action(root);
                _ = task.ContinueWith(_ => dispatcher.BeginInvokeShutdown(DispatcherPriority.Background), TaskScheduler.Default);
                Dispatcher.Run();
                task.GetAwaiter().GetResult();
            }
            catch (Exception error) { failure = error; }
            finally
            {
                dispatcher.InvokeShutdown();
                for (var attempt = 0; attempt < 20; attempt++)
                {
                    SqliteConnection.ClearAllPools();
                    try { if (Directory.Exists(root)) Directory.Delete(root, true); break; }
                    catch (IOException) when (attempt < 19) { Thread.Sleep(50); }
                    catch (Exception error) { failure ??= error; break; }
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
    }

    [TestMethod]
    public void ProductCanUseSeparateContentPriceAndOnlineStockSources()
    {
        var root = NewRoot();
        try
        {
            var catalog = new CatalogStore(root);
            catalog.SaveSource(Source("xml-a"));
            catalog.SaveSource(Source("xml-b"));
            var product = catalog.CreateManual(new CatalogProduct { Sku = "SKU-1", Name = "Product", Price = 10, Stock = 4, Currency = "USD" });
            var bindings = new ProductSourceBindingStore(root);

            bindings.Save(new(product.Id, ProductFieldGroup.Content, ProductSourceKind.Xml, "xml-a", true, 0, DateTime.MinValue), 0);
            bindings.Save(new(product.Id, ProductFieldGroup.Price, ProductSourceKind.Xml, "xml-b", true, 0, DateTime.MinValue), 0);
            bindings.Save(new(product.Id, ProductFieldGroup.OnlineStock, ProductSourceKind.Manual, "", true, 0, DateTime.MinValue), 0);

            var saved = bindings.Get(product.Id).OrderBy(x => x.Group).ToArray();
            Assert.AreEqual(3, saved.Length);
            Assert.AreEqual((ProductFieldGroup.Content, ProductSourceKind.Xml, "xml-a"), (saved[0].Group, saved[0].Kind, saved[0].SourceId));
            Assert.AreEqual((ProductFieldGroup.Price, ProductSourceKind.Xml, "xml-b"), (saved[1].Group, saved[1].Kind, saved[1].SourceId));
            Assert.AreEqual((ProductFieldGroup.OnlineStock, ProductSourceKind.Manual, ""), (saved[2].Group, saved[2].Kind, saved[2].SourceId));

            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ProductChannelBindings'";
            Assert.AreEqual(0L, (long)command.ExecuteScalar()!, "Changing a product source must not create a marketplace binding table or row.");
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void SaveValidatesXmlSourceAndUsesCompareAndSwapVersion()
    {
        var root = NewRoot();
        try
        {
            var catalog = new CatalogStore(root);
            catalog.SaveSource(Source("xml-a"));
            var product = catalog.CreateManual(new CatalogProduct { Sku = "SKU-1", Name = "Product", Price = 10, Stock = 4, Currency = "USD" });
            var store = new ProductSourceBindingStore(root);

            var first = store.Save(new(product.Id, ProductFieldGroup.Price, ProductSourceKind.Xml, "xml-a", true, 0, DateTime.MinValue), 0);
            Assert.AreEqual(1L, first.Version);

            Assert.ThrowsException<InvalidOperationException>(() =>
                store.Save(first with { Kind = ProductSourceKind.Manual, SourceId = "" }, 0));
            Assert.ThrowsException<InvalidOperationException>(() =>
                store.Save(new(product.Id, ProductFieldGroup.Content, ProductSourceKind.Xml, "missing", true, 0, DateTime.MinValue), 0));

            var current = store.Get(product.Id).Single();
            Assert.AreEqual(ProductSourceKind.Xml, current.Kind);
            Assert.AreEqual("xml-a", current.SourceId);
            Assert.AreEqual(1L, current.Version);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void MigrationCreatesDeterministicBindingsWithoutRewritingCatalogProduct()
    {
        var root = NewRoot();
        try
        {
            var catalog = new CatalogStore(root);
            var source = Source("xml-a");
            catalog.SaveSource(source);
            catalog.Import(source, XmlCatalog.Preview(Feed, source));
            var product = catalog.Products().Single();
            product.PriceSource = "manual";
            catalog.SaveProduct(product);
            var before = JsonSerializer.Serialize(catalog.Products().Single());

            var store = new ProductSourceBindingStore(root);
            Assert.AreEqual(3, store.MigrateFromCatalog());
            Assert.AreEqual(0, store.MigrateFromCatalog(), "The migration must be restart-safe and preserve existing choices.");

            var migrated = store.Get(product.Id).ToDictionary(x => x.Group);
            Assert.AreEqual((ProductSourceKind.Xml, "xml-a"), (migrated[ProductFieldGroup.Content].Kind, migrated[ProductFieldGroup.Content].SourceId));
            Assert.AreEqual((ProductSourceKind.Manual, ""), (migrated[ProductFieldGroup.Price].Kind, migrated[ProductFieldGroup.Price].SourceId));
            Assert.AreEqual((ProductSourceKind.Xml, "xml-a"), (migrated[ProductFieldGroup.OnlineStock].Kind, migrated[ProductFieldGroup.OnlineStock].SourceId));
            Assert.AreEqual(before, JsonSerializer.Serialize(catalog.Products().Single()), "Migration must not rewrite legacy catalog values.");
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void MigrationFallsBackToManualWhenRecordedXmlSourceIsCorrupt()
    {
        var root = NewRoot();
        try
        {
            var catalog = new CatalogStore(root);
            var product = catalog.CreateManual(new CatalogProduct { Sku = "SKU-1", Name = "Product", Price = 10, Stock = 4, Currency = "USD" });
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO Sources(Id,Json) VALUES('corrupt','{');
                    UPDATE CatalogProducts
                    SET Json=json_set(Json,'$.SourceId','corrupt','$.SourceKind','xml','$.PriceSource','xml','$.StockSource','xml','$.MediaSource','xml')
                    WHERE Id=$product
                    """;
                command.Parameters.AddWithValue("$product", product.Id);
                command.ExecuteNonQuery();
            }

            var store = new ProductSourceBindingStore(root);
            Assert.AreEqual(3, store.MigrateFromCatalog());
            Assert.IsTrue(store.Get(product.Id).All(binding => binding.Kind == ProductSourceKind.Manual && binding.SourceId == ""));
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void RefreshPreviewRejectsDeletedOrDisabledXmlSources()
    {
        var root = NewRoot();
        try
        {
            var catalog = new CatalogStore(root);
            var disabled = Source("disabled", enabled: false);
            catalog.SaveSource(disabled);

            Assert.ThrowsException<InvalidOperationException>(() => XmlCatalog.Preview(Feed, disabled));
            Assert.ThrowsException<InvalidOperationException>(() => XmlCatalog.Preview(Feed, disabled, catalog));
            Assert.ThrowsException<InvalidOperationException>(() => XmlCatalog.Preview(Feed, Source("deleted"), catalog));
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void MainWindowManualPreviewRejectsSourceDeletedAfterItWasLoaded()
    {
        InSta(async root =>
        {
            var catalog = new CatalogStore(root);
            var source = Source(Guid.NewGuid().ToString("N"));
            catalog.SaveSource(source);
            var window = new MainWindow(root);
            try
            {
                Invoke(window, "SetSource", source);
                SetField(window, "xml", Feed);
                SetField(window, "loadedLocation", source.Location);
                using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString()))
                {
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "DELETE FROM Sources WHERE Id=$id";
                    command.Parameters.AddWithValue("$id", source.Id);
                    command.ExecuteNonQuery();
                }

                var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => InvokeTask(window, "PreviewAsync"));
                StringAssert.Contains(error.Message, "silinmiş");
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void MainWindowScheduledRefreshDoesNotReenableSourceDisabledDuringRead()
    {
        InSta(async root =>
        {
            var window = new MainWindow(root);
            var handler = new PausedXmlHandler();
            SetField(window, "http", new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) });
            var catalog = new CatalogStore(root);
            var source = Source(Guid.NewGuid().ToString("N"));
            source.AutoImport = true;
            source.IntervalMinutes = 1;
            catalog.SaveSource(source);
            try
            {
                var scheduled = InvokeTask(window, "ScheduledAsync");
                await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var current = catalog.Sources().Single(item => item.Id == source.Id);
                current.Enabled = false;
                catalog.SaveSource(current);
                handler.Resume.TrySetResult();

                await scheduled;

                Assert.IsFalse(catalog.Sources().Single(item => item.Id == source.Id).Enabled, "A stale scheduler object must not re-enable the source.");
                Assert.IsFalse(catalog.Products().Any(item => item.SourceId == source.Id), "A source disabled during read must not reach catalog import.");
            }
            finally
            {
                handler.Resume.TrySetResult();
                window.Close();
            }
        });
    }
}
