using Microsoft.Data.Sqlite;

namespace TrMarketplaceHubDesktop;

/// <summary>Single local SQLite connection policy used by every persistent store.</summary>
public static class SqliteConnectionPolicy
{
    public const int BusyTimeoutMilliseconds = 15_000;

    public static SqliteConnection Open(string connectionString)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys=ON;PRAGMA busy_timeout=15000;PRAGMA journal_mode=WAL;PRAGMA synchronous=NORMAL;";
            pragma.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
