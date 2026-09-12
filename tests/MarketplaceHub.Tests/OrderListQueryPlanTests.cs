using System;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TrMarketplaceHubDesktop;

// #781 (PERFORMANCE: Order list query plan). OrdersStore.ReadPage filters on marketplace+shop (and
// optionally status) while always sorting by json_extract(payload,'$.UpdatedAt') DESC. The existing indexes
// were single-purpose (marketplace+shop alone; status alone; UpdatedAt alone) -- none of them let SQLite
// satisfy a "filter by store, sorted by date" query (the single most common shape: viewing one store's
// order list) from one index walk, so it falls back to a separate sort step (a real cost at scale, not a
// micro-optimization). EXPLAIN QUERY PLAN is the deterministic, machine-speed-independent way to prove this
// -- it names "USE TEMP B-TREE FOR ORDER BY" when a query needs a separate sort, and it does or doesn't
// regardless of how fast the test machine is, unlike a wall-clock timing assertion.
[TestClass]
public sealed class OrderListQueryPlanTests
{
    // Mirrors OrdersStore.ReadPage's actual WHERE/ORDER BY shape for the common "one store's order list"
    // case (marketplace+shop filter, no status/free-text filter) -- kept in sync by inspection, since the
    // production SQL is a private string, not because EXPLAIN QUERY PLAN needs a public seam of its own.
    const string StoreScopedOrderedQuery =
        "SELECT payload FROM orders WHERE marketplace=$m AND shop=$s ORDER BY json_extract(payload,'$.UpdatedAt') DESC LIMIT 100";

    static string QueryPlan(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
        cmd.Parameters.AddWithValue("$m", "etsy"); cmd.Parameters.AddWithValue("$s", "shop-1");
        using var reader = cmd.ExecuteReader();
        var lines = new System.Collections.Generic.List<string>();
        while (reader.Read()) lines.Add(reader.GetString(3)); // columns: id, parent, notused, detail
        return string.Join(" | ", lines);
    }

    [TestMethod]
    public void FilteringByStoreAndSortingByDateUsesAnIndexInsteadOfASeparateSortStep()
    {
        var root = Path.Combine(Path.GetTempPath(), "order-query-plan-" + Guid.NewGuid().ToString("N"));
        try
        {
            _ = new OrdersStore(root); // creates orders.db with the real schema/indexes.
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "orders.db") }.ToString());
            connection.Open();

            var plan = QueryPlan(connection, StoreScopedOrderedQuery);

            Assert.IsFalse(plan.Contains("TEMP B-TREE", StringComparison.OrdinalIgnoreCase),
                $"Filtering by store and sorting by date should be satisfied by one index walk, not a separate sort step. Actual plan: {plan}");
            StringAssert.Contains(plan.ToUpperInvariant(), "USING INDEX");
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public void RealisticOrderVolumeStillReturnsTheCorrectPageAfterTheIndexChange()
    {
        var root = Path.Combine(Path.GetTempPath(), "order-query-plan-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new OrdersStore(root);
            var orders = Enumerable.Range(0, 5000).Select(i => new OrderSnapshot
            {
                Marketplace = i % 3 == 0 ? "etsy" : "trendyol",
                ShopId = i % 2 == 0 ? "shop-1" : "shop-2",
                OrderId = "order-" + i,
                UpdatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(i),
                Items = [new() { Sku = "SKU-" + i, Title = "Item", Quantity = 1 }]
            }).ToList();
            store.SaveBatch(orders);

            var page = store.ReadPage(marketplace: "etsy", shopId: "shop-1", limit: 50);

            Assert.IsTrue(page.Total > 0);
            Assert.AreEqual(50, page.Items.Count);
            Assert.IsTrue(page.Items.All(o => o.Marketplace == "etsy" && o.ShopId == "shop-1"));
            // Newest first: the first item's UpdatedAt must be >= every other returned item's.
            Assert.IsTrue(page.Items.Zip(page.Items.Skip(1)).All(pair => pair.First.UpdatedAt >= pair.Second.UpdatedAt));
        }
        finally { Cleanup(root); }
    }

    static void Cleanup(string root) { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
