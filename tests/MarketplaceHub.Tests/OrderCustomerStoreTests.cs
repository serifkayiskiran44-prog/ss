using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;
using TrMarketplaceHubDesktop.Catalog;

// #841: customer contact data round-trips through its own table and never touches the order snapshot -- the
// payload, ReadAll and the transfer export stay free of it; an empty customer deletes the row; the PII reveal
// policy persists, clamps and defaults to off.
[TestClass]
public sealed class OrderCustomerStoreTests
{
    [TestMethod]
    public void CustomerDataLivesBesideTheOrderNeverInsideItAndThePolicyPersists()
    {
        var root = Path.Combine(Path.GetTempPath(), "order-customer-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new OrdersStore(root);
            store.SaveManual(new OrderSnapshot { Marketplace = "etsy", ShopId = "S1", OrderId = "o-1", RawStatus = "paid", Items = new List<OrderItem> { new() { Title = "Kupa", Sku = "K1", Quantity = 1 } } });
            Assert.IsNull(store.ReadCustomer("etsy", "S1", "o-1"), "No customer until one is saved.");
            store.SaveCustomer(new OrderCustomer("etsy", "S1", "o-1", "Ayşe Yılmaz", "ayse@example.com", "+90 532 123 45 67", "Bağdat Cad. 12"));
            var read = store.ReadCustomer("Etsy", "S1", "o-1");
            Assert.IsNotNull(read); Assert.AreEqual("Ayşe Yılmaz", read!.Name); Assert.AreEqual("ayse@example.com", read.Email);

            var payload = JsonSerializer.Serialize(store.ReadAll().Single());
            Assert.IsFalse(payload.Contains("Yılmaz") || payload.Contains("example.com") || payload.Contains("532") || payload.Contains("Bağdat"), "The snapshot carries no customer data: " + payload);
            var export = OrderTransferCodec.Export(store.ReadAll().Select(o => new OrderTransferRow(o.Marketplace, o.ShopId, o.OrderId, o.Source, o.UpdatedAt, o.Total ?? 0m)));
            Assert.IsFalse(export.Contains("Yılmaz") || export.Contains("example.com"), "The transfer export carries none either.");
            using (var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "orders.db") }.ToString()))
            {
                c.Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT payload FROM orders";
                Assert.IsFalse(((string)cmd.ExecuteScalar()!).Contains("ayse@"), "…nor does the orders table itself.");
            }

            store.SaveCustomer(new OrderCustomer("etsy", "S1", "o-1", "", "", "", ""));
            Assert.IsNull(store.ReadCustomer("etsy", "S1", "o-1"), "An empty customer deletes the row.");
            Assert.ThrowsException<ArgumentException>(() => store.SaveCustomer(new OrderCustomer("", "S1", "o-1", "x", "", "", "")));

            var catalog = new CatalogStore(root);
            var policy = catalog.GetPiiRevealPolicy();
            Assert.IsFalse(policy.Allowed, "Off by default."); Assert.AreEqual(30, policy.RevealSeconds); Assert.IsTrue(policy.RequireReason);
            var saved = catalog.SavePiiRevealPolicy(new PiiRevealPolicy(true, 2, false));
            Assert.AreEqual(PiiRevealPolicy.MinSeconds, saved.RevealSeconds, "Seconds are clamped on save.");
            var again = new CatalogStore(root).GetPiiRevealPolicy();
            Assert.IsTrue(again.Allowed); Assert.AreEqual(PiiRevealPolicy.MinSeconds, again.RevealSeconds); Assert.IsFalse(again.RequireReason);
        }
        finally
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                try { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); break; }
                catch (IOException) { System.Threading.Thread.Sleep(300); }
                catch (UnauthorizedAccessException) { System.Threading.Thread.Sleep(300); }
            }
        }
    }
}
