using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace TrMarketplaceHubDesktop.Catalog;

public partial class CatalogStore
{
    // #841: one policy row beside the stock and price policies. Off by default: nobody sees customer data until
    // the policy centre says so, and even then only for a bounded time with a reason on record.
    static void EnsurePiiRevealPolicy(SqliteConnection c, SqliteTransaction? tx) { using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "CREATE TABLE IF NOT EXISTS PiiRevealPolicy(Id INTEGER PRIMARY KEY CHECK(Id=1),Json TEXT NOT NULL)"; cmd.ExecuteNonQuery(); }

    public PiiRevealPolicy GetPiiRevealPolicy()
    {
        using var c = Open(); EnsurePiiRevealPolicy(c, null);
        using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT Json FROM PiiRevealPolicy WHERE Id=1";
        if (cmd.ExecuteScalar() is not string json) return new PiiRevealPolicy();
        try { return (JsonSerializer.Deserialize<PiiRevealPolicy>(json) ?? new PiiRevealPolicy()).Normalized(); }
        catch (JsonException) { return new PiiRevealPolicy(); }
    }

    public PiiRevealPolicy SavePiiRevealPolicy(PiiRevealPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var saved = policy.Normalized();
        using var c = Open(); using var tx = c.BeginTransaction(deferred: false); EnsurePiiRevealPolicy(c, tx);
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "INSERT INTO PiiRevealPolicy(Id,Json) VALUES(1,$json) ON CONFLICT(Id) DO UPDATE SET Json=excluded.Json";
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(saved)); cmd.ExecuteNonQuery(); tx.Commit();
        return saved;
    }
}
