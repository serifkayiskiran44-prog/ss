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

    sealed class TcmbHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var today = DateTime.UtcNow.ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture);
            var body = $"<Tarih_Date Tarih=\"{today}\"><Currency Kod=\"USD\"><Unit>1</Unit><ForexSelling>34.5</ForexSelling><ForexBuying>34</ForexBuying></Currency></Tarih_Date>";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    sealed class StaticXmlHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    static T GetField<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

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

    static async Task PreviewAndSelectFirstAsync(MainWindow window, XmlSource source)
    {
        Invoke(window, "SetSource", source);
        SetField(window, "xml", Feed);
        SetField(window, "loadedLocation", source.Location);
        await InvokeTask(window, "PreviewAsync");
        var preview = GetField<System.Windows.Controls.DataGrid>(window, "preview");
        Assert.IsTrue(preview.Items.Count > 0, "Preview must produce a selectable product row.");
        preview.SelectedItems.Add(preview.Items[0]);
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
    public async Task ManualAndScheduledXmlRefreshUpdateOnlyTheirOwnedGroupsWithoutDuplicatingIdentity()
    {
        var root = NewRoot();
        try
        {
            var catalog = new CatalogStore(root);
            var content = Source(Guid.NewGuid().ToString("N"));
            var price = Source(Guid.NewGuid().ToString("N"));
            content.UpdateName = true;
            price.UpdateName = true;
            content.Fields["SourceProductId"] = "ref";
            price.Fields["SourceProductId"] = "ref";
            catalog.SaveSource(content); catalog.SaveSource(price);
            var initialFeed = Feed.Replace("<sku>", "<ref>content-ref</ref><sku>");
            catalog.Import(content, XmlCatalog.Preview(initialFeed, content));
            var product = catalog.Products().Single();
            var bindings = new ProductSourceBindingStore(root);
            bindings.MigrateFromCatalog();
            var priceBinding = bindings.Get(product.Id).Single(x => x.Group == ProductFieldGroup.Price);
            bindings.Save(priceBinding with { SourceId = price.Id }, priceBinding.Version);
            var stockBinding = bindings.Get(product.Id).Single(x => x.Group == ProductFieldGroup.OnlineStock);
            bindings.Save(stockBinding with { Kind = ProductSourceKind.Manual, SourceId = "" }, stockBinding.Version);
            var inventory = new InventoryLocationStore(root);
            var online = inventory.GetBalance(product.Id, InventoryLocationStore.OnlineLocationId);
            inventory.SetBalance(product.Id, InventoryLocationStore.OnlineLocationId, 7, online.Version);

            var contentFeed = initialFeed.Replace("Product", "Content A").Replace("<price>10", "<price>91").Replace("<stock>4", "<stock>91");
            catalog.ImportIfSourceCurrent(content, CatalogStore.SourceConfigRevision(content), XmlCatalog.Preview(contentFeed, content, catalog));
            var afterContent = catalog.Products().Single();
            Assert.AreEqual("Content A", afterContent.Name);
            Assert.AreEqual(10m, afterContent.Price);
            Assert.AreEqual(7, afterContent.Stock);

            var priceFeed = Feed.Replace("<sku>", "<ref>price-ref</ref><sku>").Replace("Product", "Content B").Replace("<price>10", "<price>23").Replace("<stock>4", "<stock>23");
            using (var scheduled = new ScheduledXmlImportBackgroundJob(root, new HttpClient(new StaticXmlHandler(priceFeed))))
                await scheduled.ExecuteSourceAsync(price.Id, CancellationToken.None);
            var afterPrice = catalog.Products().Single();
            Assert.AreEqual("Content A", afterPrice.Name);
            Assert.AreEqual(23m, afterPrice.Price);
            Assert.AreEqual(7, afterPrice.Stock);
            Assert.AreEqual("content-ref", afterPrice.SourceProductId, "A price-only owner must not replace the content identity.");
            Assert.AreEqual(content.Id, afterPrice.SourceId, "A price-only owner must not replace the content source.");
            Assert.AreEqual(1, catalog.Products().Count);
            Assert.AreEqual(7, inventory.GetBalance(product.Id, InventoryLocationStore.OnlineLocationId).Quantity);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task ReassignedContentOwnerAdoptsItsIdentityOnceThenScheduledRefreshUsesIt()
    {
        var root = NewRoot();
        try
        {
            var catalog = new CatalogStore(root);
            var sourceA = Source(Guid.NewGuid().ToString("N"));
            var sourceB = Source(Guid.NewGuid().ToString("N"));
            sourceA.UpdateName = sourceB.UpdateName = true;
            sourceA.Fields["SourceProductId"] = sourceB.Fields["SourceProductId"] = "ref";
            catalog.SaveSource(sourceA); catalog.SaveSource(sourceB);
            catalog.Import(sourceA, XmlCatalog.Preview(Feed.Replace("<sku>", "<ref>a-ref</ref><sku>"), sourceA));
            var original = catalog.Products().Single();
            var bindings = new ProductSourceBindingStore(root);
            var content = bindings.Get(original.Id).Single(binding => binding.Group == ProductFieldGroup.Content);
            bindings.Save(content with { SourceId = sourceB.Id }, content.Version, original.UpdatedUtc, CatalogStore.SourceConfigRevision(sourceB));

            var firstB = Feed.Replace("<sku>", "<ref>b-ref</ref><sku>").Replace("Product", "B first");
            catalog.ImportIfSourceCurrent(sourceB, CatalogStore.SourceConfigRevision(sourceB), XmlCatalog.Preview(firstB, sourceB, catalog));
            var adopted = catalog.Products().Single();
            Assert.AreEqual(original.Id, adopted.Id);
            Assert.AreEqual("b-ref", adopted.SourceProductId);
            Assert.AreEqual(sourceB.Id, adopted.SourceId);
            Assert.AreEqual("B first", adopted.Name);

            var nextB = firstB.Replace("B first", "B scheduled");
            using (var scheduled = new ScheduledXmlImportBackgroundJob(root, new HttpClient(new StaticXmlHandler(nextB))))
                await scheduled.ExecuteSourceAsync(sourceB.Id, CancellationToken.None);
            var refreshed = catalog.Products().Single();
            Assert.AreEqual(original.Id, refreshed.Id);
            Assert.AreEqual("b-ref", refreshed.SourceProductId);
            Assert.AreEqual("B scheduled", refreshed.Name);
            Assert.AreEqual(1, catalog.Products().Count);

            var conflictingB = nextB.Replace("b-ref", "b-other");
            Assert.ThrowsException<InvalidOperationException>(() =>
                catalog.ImportIfSourceCurrent(sourceB, CatalogStore.SourceConfigRevision(sourceB), XmlCatalog.Preview(conflictingB, sourceB, catalog)));
            Assert.AreEqual("b-ref", catalog.Products().Single().SourceProductId, "Only the first import after reassignment may adopt the new owner's identity.");
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task ReassignedContentOwnerCannotHijackOldProductThroughAnotherSuppliersReusedLocalId()
    {
        var root = NewRoot();
        try
        {
            var catalog = new CatalogStore(root);
            var sourceA = Source(Guid.NewGuid().ToString("N"));
            var sourceB = Source(Guid.NewGuid().ToString("N"));
            sourceA.UpdateName = sourceB.UpdateName = true;
            sourceA.Fields["SourceProductId"] = sourceB.Fields["SourceProductId"] = "ref";
            catalog.SaveSource(sourceA); catalog.SaveSource(sourceB);
            var aFeed = Feed.Replace("<sku>", "<ref>shared-local-id</ref><sku>");
            catalog.Import(sourceA, XmlCatalog.Preview(aFeed, sourceA));
            var productA = catalog.Products().Single();
            var bindings = new ProductSourceBindingStore(root);
            var content = bindings.Get(productA.Id).Single(binding => binding.Group == ProductFieldGroup.Content);
            bindings.Save(content with { SourceId = sourceB.Id }, content.Version, productA.UpdatedUtc, CatalogStore.SourceConfigRevision(sourceB));

            var firstB = aFeed.Replace("SKU-1", "SKU-2").Replace("Product", "Supplier B");
            catalog.ImportIfSourceCurrent(sourceB, CatalogStore.SourceConfigRevision(sourceB), XmlCatalog.Preview(firstB, sourceB, catalog));
            var afterManual = catalog.Products();
            Assert.AreEqual(2, afterManual.Count, "A source-local ID reused by another supplier must not identify A's product.");
            var unchangedA = afterManual.Single(product => product.Id == productA.Id);
            Assert.AreEqual("SKU-1", unchangedA.Sku);
            Assert.AreEqual(sourceA.Id, unchangedA.SourceId);
            Assert.AreEqual("shared-local-id", unchangedA.SourceProductId);
            var productB = afterManual.Single(product => product.Sku == "SKU-2");
            Assert.AreEqual(sourceB.Id, productB.SourceId);
            Assert.AreEqual("shared-local-id", productB.SourceProductId);

            var nextB = firstB.Replace("Supplier B", "Supplier B scheduled");
            using (var scheduled = new ScheduledXmlImportBackgroundJob(root, new HttpClient(new StaticXmlHandler(nextB))))
                await scheduled.ExecuteSourceAsync(sourceB.Id, CancellationToken.None);
            var afterScheduled = catalog.Products();
            Assert.AreEqual(2, afterScheduled.Count);
            Assert.AreEqual(productB.Id, afterScheduled.Single(product => product.Sku == "SKU-2").Id, "Subsequent B reads must follow B's own persisted local identity.");
            Assert.AreEqual("Supplier B scheduled", afterScheduled.Single(product => product.Id == productB.Id).Name);
            Assert.AreEqual("SKU-1", afterScheduled.Single(product => product.Id == productA.Id).Sku);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void ContentOwnershipReassignmentRejectsStaleSourceRevision()
    {
        var root = NewRoot();
        try
        {
            var catalog = new CatalogStore(root);
            var sourceA = Source(Guid.NewGuid().ToString("N"));
            var sourceB = Source(Guid.NewGuid().ToString("N"));
            catalog.SaveSource(sourceA); catalog.SaveSource(sourceB);
            catalog.Import(sourceA, XmlCatalog.Preview(Feed, sourceA));
            var product = catalog.Products().Single();
            var store = new ProductSourceBindingStore(root);
            var content = store.Get(product.Id).Single(binding => binding.Group == ProductFieldGroup.Content);
            var staleRevision = CatalogStore.SourceConfigRevision(sourceB);
            sourceB.MarkupPercent = 15;
            catalog.SaveSource(sourceB);

            Assert.ThrowsException<InvalidOperationException>(() =>
                store.Save(content with { SourceId = sourceB.Id }, content.Version, product.UpdatedUtc, staleRevision));
            var unchanged = store.Get(product.Id).Single(binding => binding.Group == ProductFieldGroup.Content);
            Assert.AreEqual(sourceA.Id, unchanged.SourceId);
            Assert.AreEqual(content.Version, unchanged.Version);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void ReassignedContentOwnerFailsClosedWhenGlobalIdentityIsAmbiguous()
    {
        var root = NewRoot();
        try
        {
            var catalog = new CatalogStore(root);
            var sourceA = Source(Guid.NewGuid().ToString("N"));
            var sourceB = Source(Guid.NewGuid().ToString("N"));
            sourceA.Fields["SourceProductId"] = sourceB.Fields["SourceProductId"] = "ref";
            catalog.SaveSource(sourceA); catalog.SaveSource(sourceB);
            catalog.Import(sourceA, XmlCatalog.Preview(Feed.Replace("<sku>", "<ref>a-ref</ref><sku>"), sourceA));
            var product = catalog.Products().Single();
            var store = new ProductSourceBindingStore(root);
            var content = store.Get(product.Id).Single(binding => binding.Group == ProductFieldGroup.Content);
            store.Save(content with { SourceId = sourceB.Id }, content.Version);
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString()))
            {
                connection.Open();
                var duplicate = JsonSerializer.Deserialize<CatalogProduct>(JsonSerializer.Serialize(product))!;
                duplicate.Id = Guid.NewGuid().ToString("N"); duplicate.SourceId = ""; duplicate.SourceKind = "manual"; duplicate.SourceProductId = ""; duplicate.Stock = 0;
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO CatalogProducts(Id,Json) VALUES($id,$json)";
                command.Parameters.AddWithValue("$id", duplicate.Id);
                command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(duplicate));
                command.ExecuteNonQuery();
            }

            var incoming = Feed.Replace("<sku>", "<ref>a-ref</ref><sku>");
            Assert.ThrowsException<InvalidOperationException>(() =>
                catalog.ImportIfSourceCurrent(sourceB, CatalogStore.SourceConfigRevision(sourceB), XmlCatalog.Preview(incoming, sourceB, catalog)));
            Assert.AreEqual(2, catalog.Products().Count);
            Assert.IsTrue(catalog.Products().Any(item => item.Id == product.Id && item.SourceProductId == "a-ref"));
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
            Assert.AreEqual(0, store.MigrateFromCatalog(), "New XML imports create their source bindings atomically.");
            Assert.AreEqual(0, store.MigrateFromCatalog(), "The migration must be restart-safe and preserve existing choices.");

            var migrated = store.Get(product.Id).ToDictionary(x => x.Group);
            Assert.AreEqual((ProductSourceKind.Xml, "xml-a"), (migrated[ProductFieldGroup.Content].Kind, migrated[ProductFieldGroup.Content].SourceId));
            Assert.AreEqual((ProductSourceKind.Xml, "xml-a"), (migrated[ProductFieldGroup.Price].Kind, migrated[ProductFieldGroup.Price].SourceId), "Persisted bindings, not later legacy provenance edits, are authoritative.");
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
    public void SourceRevisionDetectsCaseOnlyTargetCategoryChanges()
    {
        var source = Source("xml-a");
        source.CategoryRules.Add(new XmlCategoryRule
        {
            XmlCategory = "Pet Food",
            TargetCategory = "Pet Food",
            Enabled = true
        });
        var before = CatalogStore.SourceConfigRevision(source);

        source.CategoryRules[0].TargetCategory = "PET FOOD";

        Assert.AreNotEqual(before, CatalogStore.SourceConfigRevision(source),
            "A case-only target category edit changes the applied category and must invalidate an old preview.");
    }

    [TestMethod]
    public void SourceRevisionUsesCanonicalSemanticCategoryRuleOrdering()
    {
        var source = Source("xml-a");
        source.CategoryRules.Add(new XmlCategoryRule
        {
            XmlCategory = "Food",
            TargetCategory = "Pet > Food",
            Prices = new()
            {
                ["Trendyol"] = new() { SaleFormula = "x+1" },
                ["Etsy"] = new() { ListFormula = "x+2" }
            }
        });
        source.CategoryRules.Add(new XmlCategoryRule
        {
            XmlCategory = "Toys",
            TargetCategory = "Pet > Toys",
            Enabled = false
        });
        var before = CatalogStore.SourceConfigRevision(source);

        source.CategoryRules.Reverse();
        source.CategoryRules.Single(rule => rule.XmlCategory == "Food").Prices = new()
        {
            ["Etsy"] = new() { ListFormula = "x+2" },
            ["Trendyol"] = new() { SaleFormula = "x+1" }
        };

        Assert.AreEqual(before, CatalogStore.SourceConfigRevision(source),
            "Harmless list/dictionary reordering must not force a new XML preview.");
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

    [TestMethod]
    public void MainWindowScheduledRefreshRejectsSourceChangedAfterPreviewBeforeImport()
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
            var previewReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseImport = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            SetField(window, "beforeScheduledImportHook", (Func<Task>)(async () =>
            {
                previewReady.TrySetResult();
                await releaseImport.Task;
            }));
            try
            {
                var scheduled = InvokeTask(window, "ScheduledAsync");
                await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                handler.Resume.TrySetResult();
                await previewReady.Task.WaitAsync(TimeSpan.FromSeconds(5));

                var current = catalog.Sources().Single(item => item.Id == source.Id);
                current.Enabled = false;
                catalog.SaveSource(current);
                releaseImport.TrySetResult();
                await scheduled;

                Assert.IsFalse(catalog.Sources().Single(item => item.Id == source.Id).Enabled, "A scheduler import must not re-enable a source disabled after preview.");
                Assert.IsFalse(catalog.Products().Any(item => item.SourceId == source.Id), "A source changed after preview must not write catalog rows.");
            }
            finally
            {
                handler.Resume.TrySetResult();
                releaseImport.TrySetResult();
                window.Close();
            }
        });
    }

    [TestMethod]
    public void AutoFxPreviewPersistsQuoteAndAllowsFencedImport()
    {
        InSta(async root =>
        {
            var catalog = new CatalogStore(root);
            var source = Source(Guid.NewGuid().ToString("N"));
            source.PriceMode = "Formula";
            source.Formula = "x * 2";
            source.CostCurrency = "TRY";
            source.AutoFx = true;
            source.FxKind = "ForexSelling";
            catalog.SaveSource(source);
            var window = new MainWindow(root);
            try
            {
                SetField(window, "http", new HttpClient(new TcmbHandler()));
                await PreviewAndSelectFirstAsync(window, source);

                var persisted = catalog.Sources().Single(item => item.Id == source.Id);
                Assert.AreEqual(34.5m, persisted.TryPerTargetUnit, "An automatic quote must be persisted before import validation.");
                Assert.IsNotNull(persisted.FxRateDate);
                Assert.IsNotNull(persisted.FxFetchedUtc);

                var snapshot = GetField<XmlSource>(window, "source");
                var revision = GetField<string>(window, "previewRevision");
                Assert.AreEqual(revision, CatalogStore.SourceConfigRevision(persisted));
                var preview = GetField<System.Windows.Controls.DataGrid>(window, "preview");
                var rows = preview.SelectedItems.Cast<CatalogProduct>().Select(item => JsonSerializer.Deserialize<CatalogProduct>(JsonSerializer.Serialize(item))!).ToList();
                var result = catalog.ImportIfSourceCurrent(snapshot, revision, rows);
                Assert.AreEqual(1, result.Added);
                Assert.AreEqual(source.Id, catalog.Products().Single().SourceId);
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void MainWindowImportRejectsSourceDeletedAfterSuccessfulPreview()
    {
        InSta(async root =>
        {
            var catalog = new CatalogStore(root);
            var source = Source(Guid.NewGuid().ToString("N"));
            catalog.SaveSource(source);
            var window = new MainWindow(root);
            try
            {
                await PreviewAndSelectFirstAsync(window, source);
                using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "catalog.db") }.ToString()))
                {
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "DELETE FROM Sources WHERE Id=$id";
                    command.Parameters.AddWithValue("$id", source.Id);
                    command.ExecuteNonQuery();
                }

                var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => InvokeTask(window, "ImportAsync"));
                StringAssert.Contains(error.Message, "silinmiş");
                Assert.IsFalse(catalog.Sources().Any(item => item.Id == source.Id), "Import must not resurrect a deleted source.");
                Assert.AreEqual(0, catalog.Products().Count, "A deleted source must not import preview rows.");
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void MainWindowImportRejectsSourceDisabledAfterSuccessfulPreview()
    {
        InSta(async root =>
        {
            var catalog = new CatalogStore(root);
            var source = Source(Guid.NewGuid().ToString("N"));
            catalog.SaveSource(source);
            var window = new MainWindow(root);
            try
            {
                await PreviewAndSelectFirstAsync(window, source);
                var current = catalog.Sources().Single(item => item.Id == source.Id);
                current.Enabled = false;
                catalog.SaveSource(current);

                var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => InvokeTask(window, "ImportAsync"));
                StringAssert.Contains(error.Message, "devre dışı");
                Assert.IsFalse(catalog.Sources().Single(item => item.Id == source.Id).Enabled, "Import must not re-enable a disabled source.");
                Assert.AreEqual(0, catalog.Products().Count, "A disabled source must not import preview rows.");
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void MainWindowImportRejectsIndependentSourceEditAfterSuccessfulPreview()
    {
        InSta(async root =>
        {
            var catalog = new CatalogStore(root);
            var source = Source(Guid.NewGuid().ToString("N"));
            catalog.SaveSource(source);
            var window = new MainWindow(root);
            try
            {
                await PreviewAndSelectFirstAsync(window, source);
                var current = catalog.Sources().Single(item => item.Id == source.Id);
                current.MarkupPercent = 25;
                catalog.SaveSource(current);

                var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => InvokeTask(window, "ImportAsync"));
                StringAssert.Contains(error.Message, "değişti");
                Assert.AreEqual(25, catalog.Sources().Single(item => item.Id == source.Id).MarkupPercent, "Import must preserve an independent source edit.");
                Assert.AreEqual(0, catalog.Products().Count, "A stale preview must not import rows after a source edit.");
            }
            finally { window.Close(); }
        });
    }
}
