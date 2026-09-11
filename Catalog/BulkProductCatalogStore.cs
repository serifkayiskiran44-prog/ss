using System.Text.Json;
using TrMarketplaceHubDesktop;

namespace TrMarketplaceHubDesktop.Catalog;

public partial class CatalogStore
{
    internal BulkProductApplyResult ApplyBulkSnapshots(IReadOnlyList<BulkProductPreviewLine> lines, CancellationToken cancellationToken, IProgress<int>? progress)
    {
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested(); using var find = connection.CreateCommand(); find.Transaction = transaction; find.CommandText = "SELECT Json FROM CatalogProducts WHERE Id=$id"; find.Parameters.AddWithValue("$id", line.ProductId); var json = find.ExecuteScalar() as string ?? throw new InvalidOperationException($"{line.Sku} bulunamadı; işlem geri alındı."); var current = JsonSerializer.Deserialize<CatalogProduct>(json)!; if (current.UpdatedUtc != line.ExpectedUpdatedUtc) throw new InvalidOperationException($"{line.Sku} önizlemeden sonra değişti; tüm toplu işlem iptal edildi.");
        }
        var count = 0; foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested(); var after = JsonSerializer.Deserialize<CatalogProduct>(JsonSerializer.Serialize(line.AfterSnapshot))!; Valid(after); after.UpdatedUtc = DateTime.UtcNow; Put(connection, "CatalogProducts", after.Id, after, transaction); progress?.Report(++count * 100 / lines.Count);
        }
        transaction.Commit(); return new(lines.Count, 0, 0);
    }
}
