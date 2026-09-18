using Microsoft.Data.Sqlite;

namespace TrMarketplaceHubDesktop.Catalog;

public partial class CatalogStore
{
    void InitializeSequenceIds(SqliteConnection connection)
    {
        using var tx = connection.BeginTransaction();
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS CatalogSequenceIds(Kind TEXT NOT NULL, EntityKey TEXT NOT NULL, Number INTEGER NOT NULL, PRIMARY KEY(Kind,EntityKey), UNIQUE(Kind,Number));";
            cmd.ExecuteNonQuery();
        }
        // Backfill existing catalog once without changing its immutable integration keys.
        foreach (var product in ReadProductsSafe(connection, tx).Healthy.Where(p => p.LocalNumber == 0))
            Put(connection, "CatalogProducts", product.Id, product, tx);
        tx.Commit();
    }

    static long SequenceId(SqliteConnection c, SqliteTransaction? tx, string kind, string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return 0;
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("$kind", kind); cmd.Parameters.AddWithValue("$key", key.Trim().ToUpperInvariant());
        cmd.CommandText = "INSERT OR IGNORE INTO CatalogSequenceIds(Kind,EntityKey,Number) SELECT $kind,$key,COALESCE(MAX(Number),0)+1 FROM CatalogSequenceIds WHERE Kind=$kind; SELECT Number FROM CatalogSequenceIds WHERE Kind=$kind AND EntityKey=$key;";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    static void AssignSequenceIds(SqliteConnection c, SqliteTransaction? tx, CatalogProduct product)
    {
        product.LocalNumber = SequenceId(c, tx, "product", product.Id);
        product.BrandNumber = SequenceId(c, tx, "brand", product.Brand);
        product.CategoryNumber = SequenceId(c, tx, "category", product.Category);
    }
}
