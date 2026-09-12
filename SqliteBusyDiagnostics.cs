using Microsoft.Data.Sqlite;

namespace TrMarketplaceHubDesktop;

// #779: SqliteConnectionPolicy already sets PRAGMA busy_timeout on every connection, so SQLite's own engine
// already waits/retries internally before ever throwing SQLITE_BUSY/SQLITE_LOCKED. This only classifies what
// happens once that window genuinely expires (a persistent lock), so the resulting error is distinguishable
// from a corrupt file or a permissions problem -- not a second retry mechanism layered on top of the first.
public static class SqliteBusyDiagnostics
{
    // SQLITE_BUSY=5, SQLITE_LOCKED=6 (https://www.sqlite.org/rescode.html); SqliteErrorCode carries the
    // primary result code with any extended-code bits already stripped by Microsoft.Data.Sqlite.
    public static bool IsBusyOrLocked(Exception exception) => exception is SqliteException { SqliteErrorCode: 5 or 6 };

    public static string Describe(Exception exception) => IsBusyOrLocked(exception)
        ? "Veritabanı şu anda başka bir işlem tarafından kullanılıyor. Birkaç saniye sonra tekrar deneyin."
        : exception.Message;
}
