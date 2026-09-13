using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace TrMarketplaceHubDesktop.Catalog;

/// <summary>One return/refund event for one order line, as reported by a marketplace or entered by the operator.</summary>
public sealed record OrderReturnEvent(string Marketplace, string ShopId, string OrderId, string EventKey, string Sku, int Quantity, decimal RefundAmount, string Currency, DateTimeOffset AtUtc);

/// <summary>What reconciling one event against the order, its stock receipt and the return ledger yields.</summary>
public sealed record OrderReturnReconciliation(
    string Status,
    IReadOnlyList<string> Reasons,
    int OrderedQuantity,
    int FulfilledQuantity,
    int ReturnedBefore,
    int ReturnedAfter,
    decimal OrderTotal,
    decimal RefundedBefore,
    decimal RefundedAfter,
    string OrderCurrency,
    int StockToRestore)
{
    public const string OkPartial = "OK_PARTIAL";
    public const string OkFull = "OK_FULL";
    public const string Blocked = "BLOCKED";
    public const string Duplicate = "DUPLICATE";
    public bool Allowed => Status is OkPartial or OkFull;
}

public partial class CatalogStore
{
    // #788. Returns and refunds had no reconciliation: the only stock leg was the whole-order restore in
    // OrderStockRestore, which puts back the full receipt quantity however much actually came back, and nothing
    // compared refund amounts or currencies with the order. This ledger reconciles every event against (a) the
    // ordered quantity of the SKU, (b) the fulfilled quantity -- what the stock receipt deducted, i.e. what can
    // physically come back, (c) the order total for cumulative refunds, (d) the order currency, and (e) the
    // events already applied, so the same event key is never counted or restored twice. A blocked event is
    // never written; applying one throws. Marketplace keys compare case-insensitively because the exception
    // queue lower-cases them while orders and receipts keep the source casing.
    const string ReturnLedgerSchema = "CREATE TABLE IF NOT EXISTS OrderReturnEvents(Marketplace TEXT NOT NULL,ShopId TEXT NOT NULL,OrderId TEXT NOT NULL,EventKey TEXT NOT NULL,Sku TEXT NOT NULL,Quantity INTEGER NOT NULL,RefundAmount TEXT NOT NULL,Currency TEXT NOT NULL,StockRestored INTEGER NOT NULL,AppliedUtc TEXT NOT NULL,PRIMARY KEY(Marketplace,ShopId,OrderId,EventKey))";

    public OrderReturnReconciliation PreviewOrderReturn(OrderSnapshot order, OrderReturnEvent @event)
    {
        ArgumentNullException.ThrowIfNull(order); ArgumentNullException.ThrowIfNull(@event);
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false);
        EnsureReturnLedger(c, tx);
        var result = ReconcileReturn(c, tx, order, @event);
        tx.Commit(); return result;
    }

    public OrderReturnReconciliation ApplyOrderReturn(OrderSnapshot order, OrderReturnEvent @event, bool approved)
    {
        ArgumentNullException.ThrowIfNull(order); ArgumentNullException.ThrowIfNull(@event);
        if (!approved) throw new InvalidOperationException("İade/iptal mutabakatı için açık onay gerekli.");
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false);
        EnsureReturnLedger(c, tx);
        var result = ReconcileReturn(c, tx, order, @event);
        if (result.Status == OrderReturnReconciliation.Duplicate) { tx.Commit(); return result; }
        if (!result.Allowed) throw new InvalidOperationException("İade mutabakatı engellendi: " + string.Join(", ", result.Reasons));
        if (result.StockToRestore > 0)
        {
            var matches = new List<CatalogProduct>();
            using (var find = c.CreateCommand())
            {
                find.Transaction = tx; find.CommandText = "SELECT Json FROM CatalogProducts WHERE json_extract(Json,'$.Sku')=$sku COLLATE BINARY LIMIT 2"; find.Parameters.AddWithValue("$sku", @event.Sku.Trim());
                using var reader = find.ExecuteReader(); while (reader.Read()) matches.Add(JsonSerializer.Deserialize<CatalogProduct>(reader.GetString(0))!);
            }
            if (matches.Count != 1) throw new InvalidOperationException($"SKU {@event.Sku.Trim()}: tek bir merkezi ürün eşleşmesi bulunamadı; stok geri konulamadı.");
            var product = matches[0]; product.Stock = checked(product.Stock + result.StockToRestore); product.UpdatedUtc = DateTime.UtcNow; Put(c, "CatalogProducts", product.Id, product, tx);
        }
        using var insert = c.CreateCommand(); insert.Transaction = tx;
        insert.CommandText = "INSERT INTO OrderReturnEvents VALUES($marketplace,$shop,$order,$event,$sku,$quantity,$refund,$currency,$restored,$at)";
        OrderStockIdentityParams(insert, order.Marketplace.Trim(), order.ShopId.Trim(), order.OrderId.Trim());
        insert.Parameters.AddWithValue("$event", @event.EventKey.Trim()); insert.Parameters.AddWithValue("$sku", @event.Sku.Trim()); insert.Parameters.AddWithValue("$quantity", @event.Quantity); insert.Parameters.AddWithValue("$refund", @event.RefundAmount.ToString(CultureInfo.InvariantCulture)); insert.Parameters.AddWithValue("$currency", (@event.Currency ?? "").Trim().ToUpperInvariant()); insert.Parameters.AddWithValue("$restored", result.StockToRestore); insert.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
        insert.ExecuteNonQuery(); tx.Commit(); return result;
    }

    static void EnsureReturnLedger(SqliteConnection c, SqliteTransaction tx) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = ReturnLedgerSchema; cmd.ExecuteNonQuery(); }

    /// <summary>#838: what the ledger says came back per SKU for one order -- the same rows the reconciliation counts, read-only.</summary>
    public IReadOnlyDictionary<string, int> OrderReturnsApplied(string marketplace, string shopId, string orderId)
    {
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false);
        EnsureReturnLedger(c, tx);
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT Sku,SUM(Quantity) FROM OrderReturnEvents WHERE Marketplace=$marketplace COLLATE NOCASE AND ShopId=$shop AND OrderId=$order GROUP BY Sku";
        cmd.Parameters.AddWithValue("$marketplace", marketplace ?? ""); cmd.Parameters.AddWithValue("$shop", shopId ?? ""); cmd.Parameters.AddWithValue("$order", orderId ?? "");
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        using (var reader = cmd.ExecuteReader()) while (reader.Read()) result[reader.GetString(0)] = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
        tx.Commit(); return result;
    }

    static OrderReturnReconciliation ReconcileReturn(SqliteConnection c, SqliteTransaction tx, OrderSnapshot order, OrderReturnEvent e)
    {
        var reasons = new List<string>();
        var eventKey = (e.EventKey ?? "").Trim();
        if (eventKey.Length is 0 or > 240 || eventKey.Any(char.IsControl)) reasons.Add("EVENT_KEY_INVALID");
        if (!order.Marketplace.Trim().Equals((e.Marketplace ?? "").Trim(), StringComparison.OrdinalIgnoreCase) || !order.ShopId.Trim().Equals((e.ShopId ?? "").Trim(), StringComparison.Ordinal) || !order.OrderId.Trim().Equals((e.OrderId ?? "").Trim(), StringComparison.Ordinal)) reasons.Add("ORDER_SCOPE_MISMATCH");
        var sku = (e.Sku ?? "").Trim();
        var ordered = sku.Length == 0 ? 0 : order.Items.Where(i => i.Sku.Trim().Equals(sku, StringComparison.OrdinalIgnoreCase)).Sum(i => i.Quantity);
        if (ordered <= 0) reasons.Add("SKU_NOT_IN_ORDER");
        if (e.Quantity <= 0) reasons.Add("QUANTITY_INVALID");
        if (e.RefundAmount < 0) reasons.Add("REFUND_INVALID");
        var orderCurrency = (order.Currency ?? "").Trim().ToUpperInvariant();
        var eventCurrency = (e.Currency ?? "").Trim().ToUpperInvariant();
        if (orderCurrency.Length > 0 && e.RefundAmount != 0 && eventCurrency != orderCurrency) reasons.Add("CURRENCY_MISMATCH");

        var receipt = ReadReceipt(c, tx, order);
        // Without a receipt nothing was deducted, so nothing can be put back; the quantity still reconciles against the order.
        var fulfilled = receipt is null ? ordered : receipt.Movements.Where(m => m.Sku.Trim().Equals(sku, StringComparison.OrdinalIgnoreCase)).Sum(m => m.Quantity);

        int returnedBefore = 0, restoredBefore = 0; decimal refundedBefore = 0m; var duplicate = false;
        using (var ledger = c.CreateCommand())
        {
            ledger.Transaction = tx; ledger.CommandText = "SELECT EventKey,Sku,Quantity,RefundAmount,StockRestored FROM OrderReturnEvents WHERE Marketplace=$marketplace COLLATE NOCASE AND ShopId=$shop AND OrderId=$order";
            OrderStockIdentityParams(ledger, order.Marketplace.Trim(), order.ShopId.Trim(), order.OrderId.Trim());
            using var reader = ledger.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetString(0) == eventKey) duplicate = true;
                refundedBefore += decimal.Parse(reader.GetString(3), CultureInfo.InvariantCulture);
                if (reader.GetString(1).Equals(sku, StringComparison.OrdinalIgnoreCase)) { returnedBefore += reader.GetInt32(2); restoredBefore += reader.GetInt32(4); }
            }
        }
        var total = order.Total ?? 0m;
        if (duplicate) return new(OrderReturnReconciliation.Duplicate, ["DUPLICATE_EVENT"], ordered, fulfilled, returnedBefore, returnedBefore, total, refundedBefore, refundedBefore, orderCurrency, 0);

        var cap = Math.Min(ordered, fulfilled);
        if (e.Quantity > 0 && returnedBefore + e.Quantity > cap) reasons.Add("OVER_RETURN");
        if (order.Total.HasValue && e.RefundAmount > 0 && refundedBefore + e.RefundAmount > total) reasons.Add("OVER_REFUND");
        var stockToRestore = receipt is null || FullRestoreExists(c, tx, order) ? 0 : Math.Max(0, Math.Min(e.Quantity, fulfilled - restoredBefore));
        if (reasons.Count > 0) return new(OrderReturnReconciliation.Blocked, reasons, ordered, fulfilled, returnedBefore, returnedBefore, total, refundedBefore, refundedBefore, orderCurrency, 0);
        var status = returnedBefore + e.Quantity >= ordered ? OrderReturnReconciliation.OkFull : OrderReturnReconciliation.OkPartial;
        return new(status, reasons, ordered, fulfilled, returnedBefore, returnedBefore + e.Quantity, total, refundedBefore, refundedBefore + e.RefundAmount, orderCurrency, stockToRestore);
    }

    static OrderStockReceipt? ReadReceipt(SqliteConnection c, SqliteTransaction tx, OrderSnapshot order)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT Json FROM OrderStockReceipts WHERE Marketplace=$marketplace COLLATE NOCASE AND ShopId=$shop AND OrderId=$order";
        OrderStockIdentityParams(cmd, order.Marketplace.Trim(), order.ShopId.Trim(), order.OrderId.Trim());
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<OrderStockReceipt>(json) : null;
    }

    static bool FullRestoreExists(SqliteConnection c, SqliteTransaction tx, OrderSnapshot order)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT 1 FROM OrderStockRestores WHERE Marketplace=$marketplace COLLATE NOCASE AND ShopId=$shop AND OrderId=$order";
        OrderStockIdentityParams(cmd, order.Marketplace.Trim(), order.ShopId.Trim(), order.OrderId.Trim());
        return cmd.ExecuteScalar() is not null;
    }
}
