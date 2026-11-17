using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

namespace MarketplaceHub.Tests;

[TestClass]
public sealed class ManualStoreSaleTests
{
    string root = null!;

    [TestInitialize]
    public void Setup() => root = Path.Combine(Path.GetTempPath(), "manual-store-sale-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    [TestMethod]
    public void ApprovedPreviewDeductsOnlyExactPhysicalLocationAndReturnsDurableReceipt()
    {
        var (product, inventory, physical) = SetupInventory();
        var service = new ManualSaleService(root);

        var preview = service.Preview(product.Id, physical.Id, 2);
        var receipt = service.Apply(preview, approved: true);
        using (var connection = new SqliteConnection("Data Source=" + Path.Combine(root, "orders.db")))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM orders WHERE marketplace='Yerel' AND shop=$shop AND id=$id";
            command.Parameters.AddWithValue("$shop", physical.Id); command.Parameters.AddWithValue("$id", receipt.ReceiptId);
            command.ExecuteNonQuery();
        }
        var duplicate = new ManualSaleService(root).Apply(preview, approved: true);

        Assert.AreEqual(5, preview.QuantityBefore);
        Assert.AreEqual(3, preview.QuantityAfter);
        Assert.AreEqual(3, inventory.GetBalance(product.Id, physical.Id).Quantity);
        Assert.AreEqual(10, inventory.GetBalance(product.Id, InventoryLocationStore.OnlineLocationId).Quantity);
        Assert.IsFalse(receipt.AlreadyApplied);
        Assert.IsTrue(duplicate.AlreadyApplied);
        Assert.AreEqual(receipt.ReceiptId, duplicate.ReceiptId);
        Assert.AreEqual(1, inventory.Movements(product.Id).Count(movement => movement.Kind == InventoryMovementKind.ManualSale));
        var order = new OrdersStore(root).ReadAll().Single();
        Assert.AreEqual("Yerel", order.Marketplace);
        Assert.AreEqual(physical.Id, order.ShopId);
        Assert.AreEqual(receipt.ReceiptId, order.OrderId);
        Assert.AreEqual(product.Id, order.Items.Single().ProductId);
        Assert.AreEqual(2, order.Items.Single().Quantity);
    }

    [TestMethod]
    public void PreviewRequiresPositiveQuantityEnabledPhysicalLocationAndExplicitApproval()
    {
        var (product, inventory, physical) = SetupInventory();
        var service = new ManualSaleService(root);

        Assert.ThrowsException<ArgumentOutOfRangeException>(() => service.Preview(product.Id, physical.Id, 0));
        Assert.ThrowsException<InvalidOperationException>(() => service.Preview(product.Id, InventoryLocationStore.OnlineLocationId, 1));
        var preview = service.Preview(product.Id, physical.Id, 1);
        Assert.ThrowsException<InvalidOperationException>(() => service.Apply(preview, approved: false));

        Assert.AreEqual(5, inventory.GetBalance(product.Id, physical.Id).Quantity);
    }

    [TestMethod]
    public void BalanceChangeAfterPreviewFailsClosedWithoutReceipt()
    {
        var (product, inventory, physical) = SetupInventory();
        var service = new ManualSaleService(root);
        var preview = service.Preview(product.Id, physical.Id, 2);
        inventory.SetBalance(product.Id, physical.Id, 4, preview.BalanceVersion);

        Assert.ThrowsException<InvalidOperationException>(() => service.Apply(preview, approved: true));

        Assert.AreEqual(4, inventory.GetBalance(product.Id, physical.Id).Quantity);
        Assert.IsNull(service.Receipt(preview.Id));
        Assert.AreEqual(0, inventory.Movements(product.Id).Count(movement => movement.Kind == InventoryMovementKind.ManualSale));
    }

    [TestMethod]
    public void LocationGenerationChangeAfterPreviewFailsClosed()
    {
        var (product, inventory, physical) = SetupInventory();
        var service = new ManualSaleService(root);
        var preview = service.Preview(product.Id, physical.Id, 2);
        using (var connection = new SqliteConnection("Data Source=" + Path.Combine(root, "catalog.db")))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "UPDATE InventoryLocations SET Name='Renamed',Version=Version+1 WHERE Id=$id";
            command.Parameters.AddWithValue("$id", physical.Id); command.ExecuteNonQuery();
        }

        Assert.ThrowsException<InvalidOperationException>(() => service.Apply(preview, approved: true));

        Assert.AreEqual(5, inventory.GetBalance(product.Id, physical.Id).Quantity);
        Assert.IsNull(service.Receipt(preview.Id));
    }

    [TestMethod]
    public void PhysicalSaleOrderCanNeverEnterOnlineStockDecisionFlow()
    {
        var (product, inventory, physical) = SetupInventory();
        var sale = new ManualSaleService(root);
        var preview = sale.Preview(product.Id, physical.Id, 1);
        sale.Apply(preview, approved: true);
        var physicalOrder = new OrdersStore(root).ReadAll().Single();

        Assert.ThrowsException<InvalidOperationException>(() =>
            new OrderStockDecisionService(new CatalogStore(root)).CreatePreview(physicalOrder));

        Assert.AreEqual(10, inventory.GetBalance(product.Id, InventoryLocationStore.OnlineLocationId).Quantity);
        Assert.AreEqual(4, inventory.GetBalance(product.Id, physical.Id).Quantity);
    }

    [TestMethod]
    public void StockPreviewRejectsDuplicateSkuAndCannotRetargetAfterSkuChange()
    {
        var catalog = new CatalogStore(root);
        var first = catalog.CreateManual(new() { Sku = "EXACT", Name = "First", Stock = 10, Currency = "TRY" });
        var service = new OrderStockDecisionService(catalog);
        var order = new OrderSnapshot
        {
            Marketplace = "Local web", ShopId = "online", OrderId = "LOCAL-1",
            Items = [new() { Title = "Product", Sku = "EXACT", Quantity = 2 }]
        };
        var exactPreview = service.CreatePreview(order);
        using (var connection = new SqliteConnection("Data Source=" + Path.Combine(root, "catalog.db")))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "UPDATE CatalogProducts SET Json=json_set(Json,'$.Sku','CHANGED') WHERE Id=$id";
            command.Parameters.AddWithValue("$id", first.Id); command.ExecuteNonQuery();
        }
        var replacement = catalog.CreateManual(new() { Sku = "EXACT", Name = "Replacement", Stock = 9, Currency = "TRY" });

        Assert.ThrowsException<InvalidOperationException>(() => service.ApplyApproved(exactPreview, approved: true));

        var inventory = new InventoryLocationStore(root);
        Assert.AreEqual(10, inventory.GetBalance(first.Id, InventoryLocationStore.OnlineLocationId).Quantity);
        Assert.AreEqual(9, inventory.GetBalance(replacement.Id, InventoryLocationStore.OnlineLocationId).Quantity);

        var duplicate = catalog.CreateManual(new() { Sku = "DUPLICATE-OTHER", Name = "Duplicate", Stock = 3, Currency = "TRY" });
        using (var connection = new SqliteConnection("Data Source=" + Path.Combine(root, "catalog.db")))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "UPDATE CatalogProducts SET Json=json_set(Json,'$.Sku','EXACT') WHERE Id=$id";
            command.Parameters.AddWithValue("$id", duplicate.Id); command.ExecuteNonQuery();
        }
        var duplicateOrder = order.Copy(); duplicateOrder.OrderId = "LOCAL-2";
        Assert.ThrowsException<InvalidOperationException>(() => service.CreatePreview(duplicateOrder));
    }

    [TestMethod]
    public void OnlineStockReceiptCarriesTheExactPreviewedProductIdentityAndVersions()
    {
        var catalog = new CatalogStore(root);
        var product = catalog.CreateManual(new() { Sku = "RECEIPT-EXACT", Name = "Receipt product", Stock = 6, Currency = "TRY" });
        var service = new OrderStockDecisionService(catalog);
        var order = new OrderSnapshot
        {
            Marketplace = "Local web", ShopId = "online", OrderId = "RECEIPT-1",
            Items = [new() { Title = "Receipt product", Sku = product.Sku, Quantity = 2 }]
        };

        var preview = service.CreatePreview(order);
        var result = service.ApplyApproved(preview, approved: true);

        var previewed = preview.Lines.Single();
        var receipted = result.Receipt.ResolvedLines!.Single();
        Assert.AreEqual(product.Id, previewed.ProductId);
        Assert.AreEqual(previewed, receipted);
        Assert.AreEqual(4, new InventoryLocationStore(root).GetBalance(product.Id, InventoryLocationStore.OnlineLocationId).Quantity);
    }

    [TestMethod]
    public void PersistedPreviewIsImmutableAndCallerCannotAlterItsTarget()
    {
        var (product, _, physical) = SetupInventory();
        var service = new ManualSaleService(root);
        var preview = service.Preview(product.Id, physical.Id, 1);
        var altered = preview with { Quantity = 2 };

        Assert.ThrowsException<InvalidOperationException>(() => service.Apply(altered, approved: true));

        using var connection = new SqliteConnection("Data Source=" + Path.Combine(root, "catalog.db"));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ManualSalePreviews SET Quantity=2 WHERE Id=$id";
        command.Parameters.AddWithValue("$id", preview.Id);
        Assert.ThrowsException<SqliteException>(() => command.ExecuteNonQuery());
    }

    [TestMethod]
    public void ProductSourceModelExposesPreviewedTransferEntryPoint()
    {
        var (product, inventory, physical) = SetupInventory();
        var model = new ProductSourceModel(root, product.Id);

        var preview = model.PreviewTransfer(InventoryLocationStore.OnlineLocationId, physical.Id, 3);
        var receipt = model.ApplyTransfer(preview);

        Assert.AreEqual(7, inventory.GetBalance(product.Id, InventoryLocationStore.OnlineLocationId).Quantity);
        Assert.AreEqual(8, inventory.GetBalance(product.Id, physical.Id).Quantity);
        Assert.AreEqual(preview.Id, receipt.ReceiptId);
    }

    (CatalogProduct Product, InventoryLocationStore Inventory, InventoryLocation Physical) SetupInventory()
    {
        var catalog = new CatalogStore(root);
        var product = catalog.CreateManual(new() { Sku = "LOCAL", Name = "Product", Stock = 10, Currency = "TRY" });
        var inventory = new InventoryLocationStore(root);
        var physical = inventory.CreatePhysicalStore("physical-1", "Main shop");
        inventory.SetBalance(product.Id, physical.Id, 5, 0);
        return (product, inventory, physical);
    }
}
