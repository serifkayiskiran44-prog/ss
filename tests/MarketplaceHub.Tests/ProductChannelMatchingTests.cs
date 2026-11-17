using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class ProductChannelMatchingTests
{
    string directory = "";
    CatalogStore catalog = null!;
    MarketplaceConnection connection = null!;
    FakeRemoteSnapshotProvider remote = null!;
    FakeCreationPreviewHandoff handoff = null!;

    [TestInitialize]
    public void Setup()
    {
        directory = Path.Combine(Path.GetTempPath(), "product-channel-matching-" + Guid.NewGuid().ToString("N"));
        catalog = new CatalogStore(directory);
        connection = new MarketplaceConnectionStore(directory).Save("trendyol", "101", "Trendyol", true);
        remote = new(connection.Id, connection.ShopId);
        handoff = new();
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    [TestMethod]
    public void ExactNonblankBarcodeIsTheOnlyAutomaticMatcher()
    {
        var exact = Create("SKU-A", "BAR-A");
        var skuOnly = Create("SKU-B", "BAR-B");
        var missing = Create("SKU-C", "");
        remote.Rows =
        [
            Row("r1", "OTHER", "BAR-A"),
            Row("r2", "SKU-B", "different"),
            Row("r3", "SKU-C", "")
        ];

        var preview = Service().PreviewMatch(connection.Id, [exact.Id, skuOnly.Id, missing.Id], remote.Rows);

        Assert.AreEqual(ProductChannelMatchOutcome.Matched, preview.Rows.Single(x => x.ProductId == exact.Id).Outcome);
        Assert.IsFalse(preview.Rows.Single(x => x.ProductId == exact.Id).Reviewed);
        Assert.AreEqual(ProductChannelMatchOutcome.NewListingCandidate, preview.Rows.Single(x => x.ProductId == skuOnly.Id).Outcome);
        Assert.AreEqual(ProductChannelMatchOutcome.NewListingCandidate, preview.Rows.Single(x => x.ProductId == missing.Id).Outcome);
    }

    [TestMethod]
    public void DuplicateLocalOrRemoteBarcodesAreConflictsAndNeverApply()
    {
        var localA = Create("SKU-A", "DUP");
        var localB = InsertLegacyDuplicate("SKU-B", "DUP");
        remote.Rows = [Row("r1", "A", "DUP"), Row("r2", "B", "DUP")];

        var preview = Service().PreviewMatch(connection.Id, [localA.Id, localB.Id], remote.Rows);

        Assert.IsTrue(preview.Rows.All(x => x.Outcome == ProductChannelMatchOutcome.Conflict));
        Assert.ThrowsException<InvalidOperationException>(() => Service().ReviewMatches(preview.Id,
            [new(localA.Id, "r1")]));
        Assert.AreEqual(0, new ProductChannelBindingStore(directory).List().Count);
    }

    [TestMethod]
    public void ManualSkuChoiceMustBeExplicitReviewedAndAccountScoped()
    {
        var product = Create("SKU-A", "LOCAL-BAR");
        remote.Rows = [Row("r1", "SKU-A", "REMOTE-BAR")];
        var service = Service();
        var preview = service.PreviewMatch(connection.Id, [product.Id], remote.Rows);

        Assert.AreEqual(ProductChannelMatchOutcome.NewListingCandidate, preview.Rows.Single().Outcome);
        Assert.AreEqual(0, new ProductChannelBindingStore(directory).List().Count);

        var reviewed = service.ReviewMatches(preview.Id, [new(product.Id, "r1")]);
        var receipt = service.ApplyMatches(reviewed.Id);

        Assert.AreEqual(1, receipt.AppliedBindings.Count);
        Assert.AreEqual("r1", new ProductChannelBindingStore(directory).Get(product.Id, connection.Id)!.RemoteId);
        Assert.ThrowsException<InvalidOperationException>(() => service.ApplyMatches(reviewed.Id));
    }

    [TestMethod]
    public void WrongAccountRemoteRowsAreRejectedBeforePreviewIsStored()
    {
        var product = Create("SKU-A", "BAR-A");
        remote.Rows = [new("another-connection", connection.ShopId, "r1", "SKU-A", "BAR-A")];

        Assert.ThrowsException<InvalidOperationException>(() => Service().PreviewMatch(connection.Id, [product.Id], remote.Rows));
        Assert.AreEqual(0, new ProductChannelBindingStore(directory).Previews().Count);
    }

    [TestMethod]
    public void NewListingCandidateOnlyHandsOffToCreationPreviewAndNeverCreatesBinding()
    {
        var product = Create("SKU-A", "BAR-A");
        remote.Rows = [];
        var service = Service();
        var preview = service.PreviewMatch(connection.Id, [product.Id], remote.Rows);

        var receipt = service.ApplyMatches(preview.Id);

        CollectionAssert.AreEqual(new[] { product.Id }, receipt.NewListingCandidates.ToArray());
        CollectionAssert.AreEqual(new[] { product.Id }, handoff.ProductIds.ToArray());
        Assert.AreEqual(0, handoff.RemoteWrites);
        Assert.AreEqual(0, new ProductChannelBindingStore(directory).List().Count);
    }

    [TestMethod]
    public void StaleNewListingPreviewDoesNotReachCreationHandoff()
    {
        var product = Create("SKU-A", "BAR-A");
        remote.Rows = [];
        var service = Service();
        var preview = service.PreviewMatch(connection.Id, [product.Id], remote.Rows);
        product.Name = "Changed";
        catalog.SaveProduct(product);

        Assert.ThrowsException<InvalidOperationException>(() => service.ApplyMatches(preview.Id));
        Assert.AreEqual(0, handoff.ProductIds.Count);
    }

    [TestMethod]
    public void CatalogConnectionBindingAndRemoteSnapshotChangesInvalidateReviewedPreview()
    {
        AssertStale(service =>
        {
            var changed = catalog.Products().Single();
            changed.Name = "Changed";
            catalog.SaveProduct(changed);
        });
        AssertStale(service => new MarketplaceConnectionStore(directory).SetEnabled(connection.Id, false));
        AssertStale(service =>
        {
            var store = new ProductChannelBindingStore(directory);
            var binding = store.Get(catalog.Products().Single().Id, connection.Id)!;
            store.Save(binding with { ManageStock = false }, binding.Version);
        }, seedBinding: true);
        AssertStale(service => remote.Rows = [Row("r1", "SKU-A", "BAR-A") with { State = "changed" }]);
    }

    [TestMethod]
    public void ApplyRequiresCurrentSnapshotProviderAndRejectsChangedSnapshot()
    {
        var product = Create("SKU-A", "BAR-A");
        remote.Rows = [Row("r1", "SKU-A", "BAR-A")];
        var service = Service();
        var preview = service.PreviewMatch(connection.Id, [product.Id], remote.Rows);
        var reviewed = service.ReviewMatches(preview.Id, [new(product.Id, "r1")]);

        Assert.ThrowsException<InvalidOperationException>(() => new ProductChannelMatchService(directory, null, handoff).ApplyMatches(reviewed.Id));
        Assert.IsNull(new ProductChannelBindingStore(directory).Get(product.Id, connection.Id));
        Assert.AreEqual(0, handoff.ProductIds.Count);

        remote.Rows = [Row("r1", "SKU-A", "BAR-A") with { State = "changed" }];
        Assert.ThrowsException<InvalidOperationException>(() => service.ApplyMatches(reviewed.Id));
        Assert.IsNull(new ProductChannelBindingStore(directory).Get(product.Id, connection.Id));
    }

    [TestMethod]
    public void ReturnedPreviewMutationCannotChangePersistedReviewOrReceipt()
    {
        var product = Create("SKU-A", "BAR-A");
        remote.Rows = [Row("r1", "SKU-A", "BAR-A")];
        var service = Service();
        var preview = service.PreviewMatch(connection.Id, [product.Id], remote.Rows);
        var reviewed = service.ReviewMatches(preview.Id, [new(product.Id, "r1")]);
        var callerRows = reviewed.Rows as List<ProductChannelMatchRow>;
        callerRows?.Clear();

        var receipt = service.ApplyMatches(reviewed.Id);
        var persisted = new ProductChannelBindingStore(directory).Receipt(reviewed.Id)!;

        Assert.AreEqual(1, receipt.AppliedBindings.Count);
        Assert.AreEqual(1, persisted.AppliedBindings.Count);
        Assert.AreNotSame(receipt, persisted);
    }

    void AssertStale(Action<ProductChannelMatchService> mutate, bool seedBinding = false)
    {
        Reset();
        var product = Create("SKU-A", "BAR-A");
        remote.Rows = [Row("r1", "SKU-A", "BAR-A")];
        if (seedBinding)
        {
            var store = new ProductChannelBindingStore(directory);
            store.Save(new(product.Id, connection.Id, "old", "OLD-SKU", "OLD-BAR", true, true, true, "", "", "Active", 0, default), 0);
        }
        var service = Service();
        var preview = service.PreviewMatch(connection.Id, [product.Id], remote.Rows);
        var reviewed = service.ReviewMatches(preview.Id, [new(product.Id, "r1")]);
        mutate(service);

        Assert.ThrowsException<InvalidOperationException>(() => service.ApplyMatches(reviewed.Id));
    }

    void Reset()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        catalog = new CatalogStore(directory);
        connection = new MarketplaceConnectionStore(directory).Save("trendyol", "101", "Trendyol", true);
        remote = new(connection.Id, connection.ShopId);
        handoff = new();
    }

    CatalogProduct Create(string sku, string barcode) => catalog.CreateManual(new() { Sku = sku, Barcode = barcode, Name = sku, Price = 10, Stock = 1, Currency = "TRY" });

    CatalogProduct InsertLegacyDuplicate(string sku, string barcode)
    {
        var value = new CatalogProduct { Id = Guid.NewGuid().ToString("N"), Sku = sku, Barcode = barcode, Name = sku, Price = 10, Stock = 1, Currency = "TRY", UpdatedUtc = DateTime.UtcNow };
        using var connection = new SqliteConnection("Data Source=" + Path.Combine(directory, "catalog.db"));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO CatalogProducts(Id,Json) VALUES($id,$json)";
        command.Parameters.AddWithValue("$id", value.Id);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(value));
        command.ExecuteNonQuery();
        return value;
    }

    ProductChannelRemoteRow Row(string id, string sku, string barcode) => new(connection.Id, connection.ShopId, id, sku, barcode);
    ProductChannelMatchService Service() => new(directory, remote, handoff);

    sealed class FakeRemoteSnapshotProvider(string connectionId, string shopId) : IProductChannelRemoteSnapshotProvider
    {
        public IReadOnlyList<ProductChannelRemoteRow> Rows { get; set; } = [];
        public ProductChannelRemoteSnapshot Read(MarketplaceConnection connection) => new(connectionId, shopId, Rows);
    }

    sealed class FakeCreationPreviewHandoff : IProductChannelCreationPreviewHandoff
    {
        public IReadOnlyList<string> ProductIds { get; private set; } = [];
        public int RemoteWrites { get; private set; }
        public string Preview(MarketplaceConnection connection, IReadOnlyList<string> productIds)
        {
            ProductIds = productIds.ToArray();
            return "creation-preview";
        }
    }
}
