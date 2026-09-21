using Microsoft.Data.Sqlite;

namespace TrMarketplaceHubDesktop;

public sealed partial class OrdersStore
{
    public OrderSnapshot? Find(string marketplace, string shopId, string orderId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT marketplace,shop,id,payload FROM orders WHERE marketplace=$marketplace AND shop=$shop AND id=$id";
        command.Parameters.AddWithValue("$marketplace", marketplace);
        command.Parameters.AddWithValue("$shop", shopId);
        command.Parameters.AddWithValue("$id", orderId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (TryReadOrder(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), out var order, out var corrupt)) return order;
        throw new OrderRowCorruptException(marketplace, shopId, orderId, corrupt!.Reason);
    }
}
