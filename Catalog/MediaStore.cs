using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;

namespace TrMarketplaceHubDesktop.Catalog;

public enum MediaStatus
{
    Pending,
    Ready,
    InvalidUrl,
    Timeout,
    NotFound,
    TooLarge,
    RateLimited,
    UnsupportedFormat,
    Error
}

public sealed class ProductMediaRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ProductId { get; set; } = "";
    public string Url { get; set; } = "";
    public string NormalizedUrl { get; set; } = "";
    public string Source { get; set; } = "manual";
    public int SortOrder { get; set; }
    public bool IsPrimary { get; set; }
    public string ContentHash { get; set; } = "";
    public MediaStatus Status { get; set; } = MediaStatus.Pending;
    public string Error { get; set; } = "";
    public DateTime? LastValidatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

/// Bounded diagnostics only (id, product id, a short reason, detection time) -
/// never Url, Error, or ContentHash - for a ProductMedia row with an unparsable
/// persisted timestamp. See CatalogStore's CorruptProductRow for the same pattern.
public sealed record CorruptMediaRow(string Id, string ProductId, string Reason, DateTime DetectedUtc);

public sealed class MediaStore
{
    readonly string connectionString;

    public MediaStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MonoBridgeDesktop");
        Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "media.db") }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ProductMedia(
                Id TEXT PRIMARY KEY,
                ProductId TEXT NOT NULL,
                Url TEXT NOT NULL,
                NormalizedUrl TEXT NOT NULL,
                Source TEXT NOT NULL,
                SortOrder INTEGER NOT NULL,
                IsPrimary INTEGER NOT NULL,
                ContentHash TEXT NOT NULL,
                Status TEXT NOT NULL,
                Error TEXT NOT NULL,
                LastValidatedUtc TEXT NULL,
                UpdatedUtc TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS UX_ProductMedia_ProductUrl ON ProductMedia(ProductId, NormalizedUrl);
            CREATE INDEX IF NOT EXISTS IX_ProductMedia_ProductOrder ON ProductMedia(ProductId, SortOrder, Id);
            """;
        command.ExecuteNonQuery();
        EnsureUniqueOrderIndex(connection);
    }

    /// Build-once, with a fallback to a non-unique index if a legacy DB already
    /// has a (ProductId,SortOrder) collision - never fails startup or silently
    /// renumbers pre-existing rows. New writes (Add/SetSortOrder) additionally
    /// serialize via an IMMEDIATE transaction so they never themselves create a
    /// new collision, regardless of which index variant ended up installed. See
    /// #2594 (mirrors TaxonomyStore.EnsureUniqueNormalizedKeyIndex/
    /// SyncStore.EnsureUniqueKeyIndex).
    static void EnsureUniqueOrderIndex(SqliteConnection connection)
    {
        try { using var unique = connection.CreateCommand(); unique.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS UX_ProductMedia_ProductOrder ON ProductMedia(ProductId, SortOrder)"; unique.ExecuteNonQuery(); }
        catch (SqliteException) { /* legacy DB already has a (ProductId,SortOrder) collision - the plain IX_ProductMedia_ProductOrder index created above still covers ordered reads. */ }
    }

    SqliteConnection Open() { var connection = new SqliteConnection(connectionString); connection.Open(); using var pragma = connection.CreateCommand(); pragma.CommandText = "PRAGMA busy_timeout=15000"; pragma.ExecuteNonQuery(); return connection; }

    public IReadOnlyList<ProductMediaRecord> List(string? productId = null, string? query = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,ProductId,Url,NormalizedUrl,Source,SortOrder,IsPrimary,ContentHash,Status,Error,LastValidatedUtc,UpdatedUtc FROM ProductMedia WHERE ($product='' OR ProductId=$product) AND ($query='' OR Url LIKE $like OR Source LIKE $like OR Status LIKE $like) ORDER BY ProductId,SortOrder,Id";
        command.Parameters.AddWithValue("$product", productId?.Trim() ?? "");
        var normalizedQuery = query?.Trim() ?? "";
        command.Parameters.AddWithValue("$query", normalizedQuery);
        command.CommandText = command.CommandText.Replace("Url LIKE $like OR Source LIKE $like OR Status LIKE $like", "Url LIKE $like ESCAPE '\\' OR Source LIKE $like ESCAPE '\\' OR Status LIKE $like ESCAPE '\\'");
        command.Parameters.AddWithValue("$like", $"%{normalizedQuery.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%");
        using var reader = command.ExecuteReader();
        var rows = new List<ProductMediaRecord>();
        while (reader.Read()) if (TryRead(reader, out var row, out _)) rows.Add(row!);
        return rows;
    }

    /// Bounded diagnostics for every row whose LastValidatedUtc/UpdatedUtc failed to
    /// parse - never the raw Url/Error/ContentHash. Detection re-derives from the
    /// row's own stored text every call, so it stays stable across a restart without
    /// a separate tracking table.
    public IReadOnlyList<CorruptMediaRow> CorruptRows(string? productId = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id,ProductId,Url,NormalizedUrl,Source,SortOrder,IsPrimary,ContentHash,Status,Error,LastValidatedUtc,UpdatedUtc FROM ProductMedia WHERE ($product='' OR ProductId=$product)";
        command.Parameters.AddWithValue("$product", productId?.Trim() ?? "");
        using var reader = command.ExecuteReader();
        var rows = new List<CorruptMediaRow>();
        while (reader.Read()) if (!TryRead(reader, out _, out var corrupt)) rows.Add(corrupt!);
        return rows;
    }

    public ProductMediaRecord Add(string productId, string url, string source = "manual", int? sortOrder = null)
    {
        if (string.IsNullOrWhiteSpace(productId)) throw new InvalidOperationException("Medya için ürün kimliği zorunlu.");
        var normalized = NormalizeUrl(url);
        if (normalized.Length == 0) throw new InvalidOperationException("Geçerli bir HTTPS veya yerel file adresi girin.");
        var cleanSource = string.IsNullOrWhiteSpace(source) ? "manual" : source.Trim();
        using var connection = Open();
        // IMMEDIATE acquires SQLite's write lock up front, serializing concurrent
        // Add() calls for any product against this connection string - the
        // MAX(SortOrder)+1 read and the INSERT below can never interleave with
        // another writer's, so parallel Add() calls never allocate the same
        // logical order (#2594).
        using var transaction = connection.BeginTransaction(deferred: false);
        var order = sortOrder ?? NextOrder(connection, transaction, productId);
        var row = new ProductMediaRecord { ProductId = productId.Trim(), Url = url.Trim(), NormalizedUrl = normalized, Source = cleanSource, SortOrder = order, IsPrimary = order == 0 };
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO ProductMedia(Id,ProductId,Url,NormalizedUrl,Source,SortOrder,IsPrimary,ContentHash,Status,Error,LastValidatedUtc,UpdatedUtc) VALUES($id,$product,$url,$normalized,$source,$order,$primary,$hash,$status,$error,$validated,$updated)";
        AddParameters(command, row);
        try { command.ExecuteNonQuery(); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            using var checkUrl = connection.CreateCommand(); checkUrl.Transaction = transaction; checkUrl.CommandText = "SELECT 1 FROM ProductMedia WHERE ProductId=$product AND NormalizedUrl=$normalized"; checkUrl.Parameters.AddWithValue("$product", row.ProductId); checkUrl.Parameters.AddWithValue("$normalized", normalized);
            var byUrlAlready = checkUrl.ExecuteScalar() is not null;
            throw new InvalidOperationException(byUrlAlready ? "Bu ürün için aynı görsel zaten kayıtlı." : "Bu sıra numarası bu üründe zaten kullanılıyor.", ex);
        }
        transaction.Commit();
        return row;
    }

    public ProductMediaRecord Ensure(string productId, string url, string source = "catalog")
    {
        var normalized = NormalizeUrl(url);
        if (normalized.Length == 0) throw new InvalidOperationException("Geçersiz görsel adresi.");
        var existing = List(productId).FirstOrDefault(x => x.NormalizedUrl.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        return existing ?? Add(productId, url, source);
    }

    public void Delete(string id)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var find = connection.CreateCommand();
        find.Transaction = transaction;
        find.CommandText = "SELECT ProductId,IsPrimary FROM ProductMedia WHERE Id=$id";
        find.Parameters.AddWithValue("$id", id);
        using var reader = find.ExecuteReader();
        if (!reader.Read()) return;
        var productId = reader.GetString(0); var wasPrimary = reader.GetInt32(1) != 0;
        reader.Close();
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "DELETE FROM ProductMedia WHERE Id=$id"; command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery();
        if (wasPrimary)
        {
            using var promote = connection.CreateCommand(); promote.Transaction = transaction; promote.CommandText = "UPDATE ProductMedia SET IsPrimary=1,UpdatedUtc=$now WHERE Id=(SELECT Id FROM ProductMedia WHERE ProductId=$product ORDER BY SortOrder,Id LIMIT 1)"; promote.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)); promote.Parameters.AddWithValue("$product", productId); promote.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void SetPrimary(string id)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var find = connection.CreateCommand();
        find.Transaction = transaction;
        find.CommandText = "SELECT ProductId FROM ProductMedia WHERE Id=$id";
        find.Parameters.AddWithValue("$id", id);
        var productId = find.ExecuteScalar() as string ?? throw new InvalidOperationException("Görsel bulunamadı.");
        using var clear = connection.CreateCommand();
        clear.Transaction = transaction;
        clear.CommandText = "UPDATE ProductMedia SET IsPrimary=0,UpdatedUtc=$now WHERE ProductId=$product";
        clear.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        clear.Parameters.AddWithValue("$product", productId);
        clear.ExecuteNonQuery();
        using var set = connection.CreateCommand();
        set.Transaction = transaction;
        set.CommandText = "UPDATE ProductMedia SET IsPrimary=1,UpdatedUtc=$now WHERE Id=$id";
        set.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        set.Parameters.AddWithValue("$id", id);
        if (set.ExecuteNonQuery() != 1) throw new InvalidOperationException("Görsel bulunamadı.");
        transaction.Commit();
    }

    /// expectedUpdatedUtc, when given, is a compare-and-swap guard: the reorder
    /// only applies if the row's current persisted UpdatedUtc still matches, so
    /// a stale writer (working from a list snapshot someone else already
    /// changed) is rejected instead of silently applying its own ordering on
    /// top. The move itself is a swap, not a partial shift: whichever sibling
    /// currently holds the requested SortOrder takes over the moving row's old
    /// order, so the (ProductId,SortOrder) uniqueness invariant holds
    /// continuously - there is never an intermediate state with two rows
    /// sharing one order or a temporarily-missing order. Runs inside an
    /// IMMEDIATE transaction so two concurrent reorders for the same product
    /// can never interleave. See #2594.
    public void SetSortOrder(string id, int sortOrder, DateTime? expectedUpdatedUtc = null)
    {
        if (sortOrder < 0) throw new ArgumentOutOfRangeException(nameof(sortOrder));
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var find = connection.CreateCommand(); find.Transaction = transaction; find.CommandText = "SELECT ProductId,SortOrder,UpdatedUtc FROM ProductMedia WHERE Id=$id"; find.Parameters.AddWithValue("$id", id);
        using (var reader = find.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidOperationException("Görsel bulunamadı.");
            var productId = reader.GetString(0); var oldOrder = reader.GetInt32(1);
            if (expectedUpdatedUtc.HasValue && (!TryParseDate(reader.GetString(2), out var currentUpdated) || currentUpdated != expectedUpdatedUtc.Value)) throw new InvalidOperationException("Görsel sırası başka bir işlemde değişti; listeyi yenileyip tekrar deneyin.");
            reader.Close();
            if (oldOrder == sortOrder) { transaction.Commit(); return; }
            var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            // Three-step swap so the (ProductId,SortOrder) unique index is never
            // violated mid-transaction: move the row out to a sentinel no real
            // row ever uses (SetSortOrder rejects negative input, so -1 is safe),
            // then move any sibling occupying the target slot into the vacated
            // old slot, then finally place the moving row at the target.
            using (var park = connection.CreateCommand()) { park.Transaction = transaction; park.CommandText = "UPDATE ProductMedia SET SortOrder=-1,UpdatedUtc=$now WHERE Id=$id"; park.Parameters.AddWithValue("$now", now); park.Parameters.AddWithValue("$id", id); park.ExecuteNonQuery(); }
            using (var swap = connection.CreateCommand()) { swap.Transaction = transaction; swap.CommandText = "UPDATE ProductMedia SET SortOrder=$old,UpdatedUtc=$now WHERE ProductId=$product AND SortOrder=$target"; swap.Parameters.AddWithValue("$old", oldOrder); swap.Parameters.AddWithValue("$now", now); swap.Parameters.AddWithValue("$product", productId); swap.Parameters.AddWithValue("$target", sortOrder); swap.ExecuteNonQuery(); }
            using (var move = connection.CreateCommand()) { move.Transaction = transaction; move.CommandText = "UPDATE ProductMedia SET SortOrder=$target,UpdatedUtc=$now WHERE Id=$id"; move.Parameters.AddWithValue("$target", sortOrder); move.Parameters.AddWithValue("$now", now); move.Parameters.AddWithValue("$id", id); move.ExecuteNonQuery(); }
        }
        transaction.Commit();
    }

    public void UpdateValidation(string id, MediaValidationResult result)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ProductMedia SET Status=$status,Error=$error,ContentHash=$hash,LastValidatedUtc=$validated,UpdatedUtc=$updated WHERE Id=$id";
        command.Parameters.AddWithValue("$status", result.Status.ToString());
        command.Parameters.AddWithValue("$error", result.Error ?? "");
        command.Parameters.AddWithValue("$hash", result.ContentHash ?? "");
        command.Parameters.AddWithValue("$validated", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Görsel bulunamadı.");
    }

    public static string NormalizeUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var trimmed = value.Trim();
        if (trimmed.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var fileUri) || !fileUri.IsFile) return "";
            return new Uri(Path.GetFullPath(fileUri.LocalPath)).AbsoluteUri.ToLowerInvariant();
        }
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return "";
        if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)) return "";
        var builder = new UriBuilder(uri) { Fragment = "" };
        if (builder.Port == 443) builder.Port = -1;
        builder.Host = builder.Host.ToLowerInvariant();
        return builder.Uri.AbsoluteUri.TrimEnd('/').ToLowerInvariant();
    }

    static int NextOrder(SqliteConnection connection, SqliteTransaction transaction, string productId)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT COALESCE(MAX(SortOrder),-1)+1 FROM ProductMedia WHERE ProductId=$product"; command.Parameters.AddWithValue("$product", productId); return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    static void AddParameters(SqliteCommand command, ProductMediaRecord row)
    {
        command.Parameters.AddWithValue("$id", row.Id); command.Parameters.AddWithValue("$product", row.ProductId); command.Parameters.AddWithValue("$url", row.Url); command.Parameters.AddWithValue("$normalized", row.NormalizedUrl); command.Parameters.AddWithValue("$source", row.Source); command.Parameters.AddWithValue("$order", row.SortOrder); command.Parameters.AddWithValue("$primary", row.IsPrimary ? 1 : 0); command.Parameters.AddWithValue("$hash", row.ContentHash); command.Parameters.AddWithValue("$status", row.Status.ToString()); command.Parameters.AddWithValue("$error", row.Error); command.Parameters.AddWithValue("$validated", row.LastValidatedUtc.HasValue ? row.LastValidatedUtc.Value.ToString("O", CultureInfo.InvariantCulture) : DBNull.Value); command.Parameters.AddWithValue("$updated", row.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture));
    }

    static bool TryRead(SqliteDataReader reader, out ProductMediaRecord? row, out CorruptMediaRow? corrupt)
    {
        row = null; corrupt = null; var id = reader.GetString(0); var productId = reader.GetString(1);
        DateTime? lastValidated = null;
        if (!reader.IsDBNull(10)) { if (!TryParseDate(reader.GetString(10), out var value)) { corrupt = new(id, productId, "Malformed LastValidatedUtc timestamp", DateTime.UtcNow); return false; } lastValidated = value; }
        if (!TryParseDate(reader.GetString(11), out var updated)) { corrupt = new(id, productId, "Malformed UpdatedUtc timestamp", DateTime.UtcNow); return false; }
        row = new()
        {
            Id = id, ProductId = productId, Url = reader.GetString(2), NormalizedUrl = reader.GetString(3), Source = reader.GetString(4), SortOrder = reader.GetInt32(5), IsPrimary = reader.GetInt32(6) != 0, ContentHash = reader.GetString(7), Status = Enum.TryParse<MediaStatus>(reader.GetString(8), out var status) ? status : MediaStatus.Error, Error = reader.GetString(9), LastValidatedUtc = lastValidated, UpdatedUtc = updated
        };
        return true;
    }

    /// Only ever a format/parse failure - never conflated with a DB-busy/locked
    /// SqliteException, which is raised by the surrounding command, not this parse.
    static bool TryParseDate(string value, out DateTime result) => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out result);
}
