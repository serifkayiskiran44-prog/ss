using Microsoft.Data.Sqlite;
using System.IO;

namespace TrMarketplaceHubDesktop;

/// <summary>Small, transaction-safe guard shared by the local SQLite stores.</summary>
public static class SchemaVersion
{
    public const int Current = 1;

    public static void Ensure(SqliteConnection connection, int target = Current)
    {
        if (target < 0) throw new ArgumentOutOfRangeException(nameof(target));
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA quick_check(1); PRAGMA user_version;";
        using var reader = check.ExecuteReader();
        if (!reader.Read() || !string.Equals(reader.GetString(0), "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Yerel SQLite verisi bozuk; veri güvenliği için açılış durduruldu.");
        reader.NextResult();
        if (!reader.Read()) throw new InvalidDataException("Yerel SQLite şema sürümü okunamadı.");
        var current = reader.GetInt32(0);
        if (current > target) throw new InvalidDataException($"Yerel SQLite şeması ({current}) bu uygulama sürümünden daha yeni.");
        if (current == target) return;
        using var tx = connection.BeginTransaction();
        using var migrate = connection.CreateCommand();
        migrate.Transaction = tx;
        migrate.CommandText = $"PRAGMA user_version={target}";
        migrate.ExecuteNonQuery();
        tx.Commit();
    }
}
