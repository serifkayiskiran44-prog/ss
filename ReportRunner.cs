using System.IO;
using System.Text;

namespace TrMarketplaceHubDesktop;

public sealed record ReportRunOutcome(ReportRunState State, int Rows, string Message);

/// <summary>
/// The one report the reports workspace runs by itself (#847): the orders list as CSV through the existing
/// <see cref="ReportTemplateRenderer"/>, whose column allow-list has no customer field, so the file can never
/// carry personal data. Parameters are validated again here (the drawer's own check is not enough), the store must
/// be one the shell offers, the file is written whole then moved into place, and every outcome -- written,
/// cancelled, failed -- lands in <see cref="ReportRunStore"/> and the audit trail with the parameter summary
/// minus the query text as its note (#848: the query is the operator's own text and is never logged). Progress
/// is reported as real stage events: query (total unknown until the first page), generate, export.
/// </summary>
public static class ReportRunner
{
    public const string OrdersCsvKey = "orders-csv";
    public static readonly IReadOnlyList<string> OrdersCsvColumns = new[] { "OrderId", "ShopId", "Status", "Price", "Currency", "UpdatedUtc" };

    public static bool CanRun(ReportDefinition definition) => definition is not null && string.Equals(definition.Key, OrdersCsvKey, StringComparison.Ordinal);

    public static async Task<ReportRunOutcome> RunAsync(string? directory, ReportDefinition definition, ReportParameterSet parameters, string path, IReadOnlyCollection<string>? allowedStoreKeys, CancellationToken cancellationToken = default, IProgress<ReportRunProgressEvent>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(definition); ArgumentNullException.ThrowIfNull(parameters);
        if (!CanRun(definition)) throw new InvalidOperationException("Bu rapor sahibi ekranda üretilir; buradan çalıştırılmaz.");
        var findings = ReportParameters.Validate(parameters, definition, allowedStoreKeys, DateTime.UtcNow);
        if (!ReportParameters.IsValid(findings)) throw new InvalidOperationException(findings.First(f => f.Level == SeverityLevel.Blocking).Message);
        if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Rapor dosyası .csv ile bitmeli.", nameof(path));
        var started = DateTime.UtcNow; var runs = new ReportRunStore(directory);
        var note = ReportParameters.Summary(parameters, ReportParameters.SchemaFor(definition), includeQuery: false);
        var stage = ReportRunStage.Query;
        try
        {
            progress?.Report(new(ReportRunStage.Query, ReportRunStageStatus.Running));
            var rows = await Task.Run(() => OrdersCsvRows(directory, parameters, cancellationToken, progress), cancellationToken);
            progress?.Report(new(ReportRunStage.Query, ReportRunStageStatus.Done, rows.Count, rows.Count));
            stage = ReportRunStage.Generate; progress?.Report(new(stage, ReportRunStageStatus.Running, 0, rows.Count));
            var csv = ReportTemplateRenderer.Render(new ReportTemplate("orders", OrdersCsvColumns), rows);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(stage, ReportRunStageStatus.Done, rows.Count, rows.Count));
            stage = ReportRunStage.Export; progress?.Report(new(stage, ReportRunStageStatus.Running));
            var temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", Path.GetFileNameWithoutExtension(path) + ".tmp-" + Guid.NewGuid().ToString("N")[..8] + ".csv");
            try { File.WriteAllText(temp, csv, new UTF8Encoding(true)); File.Move(temp, path, overwrite: true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            progress?.Report(new(stage, ReportRunStageStatus.Done, 1, 1));
            var message = rows.Count == 0 ? "Aralıkta sipariş yok; yalnız başlık satırı yazıldı." : $"{rows.Count:N0} sipariş yazıldı.";
            Record(directory, runs, started, ReportRunState.Succeeded, rows.Count, note, parameters.StoreKey, message);
            return new(ReportRunState.Succeeded, rows.Count, message);
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new(stage, ReportRunStageStatus.Cancelled));
            const string message = "Rapor iptal edildi; dosya yazılmadı.";
            Record(directory, runs, started, ReportRunState.Cancelled, 0, note, parameters.StoreKey, message);
            return new(ReportRunState.Cancelled, 0, message);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            var safe = AuditStore.Sanitize(error.Message);
            progress?.Report(new(stage, ReportRunStageStatus.Failed, Note: safe));
            Record(directory, runs, started, ReportRunState.Failed, 0, note, parameters.StoreKey, safe);
            return new(ReportRunState.Failed, 0, "Rapor yazılamadı: " + safe);
        }
    }

    /// <summary>The run store row and the audit event for one terminal outcome; the audit never blocks the run.</summary>
    static void Record(string? directory, ReportRunStore runs, DateTime started, ReportRunState state, int rows, string note, string storeKey, string message)
    {
        runs.Record(OrdersCsvKey, started, state, rows, note, storeKey);
        try
        {
            var (channel, shop) = ReportParameters.SplitStore(storeKey);
            new AuditStore(directory).Append(new AuditEvent { Module = "reports", Action = "run:" + OrdersCsvKey, Outcome = state.ToString(), Detail = (note.Length > 0 ? note + " · " : "") + message, Marketplace = channel, ShopId = shop });
        }
        catch (Exception) { /* the audit trail is best effort; the run store already has the outcome */ }
    }

    /// <summary>One row per order of the parameter store, within the whole-day range (by the record's update time) and the delivery state; "Unknown" also means "no package". Reports the query stage page by page; the total is unknown until the first page answers.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> OrdersCsvRows(string? directory, ReportParameterSet parameters, CancellationToken cancellationToken = default, IProgress<ReportRunProgressEvent>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var (channel, shop) = ReportParameters.SplitStore(parameters.StoreKey); var store = new OrdersStore(directory); var rows = new List<IReadOnlyDictionary<string, object?>>();
        var from = parameters.FromUtc?.Date ?? DateTime.MinValue; var to = parameters.ToUtc?.Date ?? DateTime.MaxValue.Date;
        const int PageSize = 1000; var read = 0;
        for (var offset = 0; ; offset += PageSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = store.ReadPage(null, shop, null, parameters.Query, offset, PageSize);
            foreach (var order in page.Items)
            {
                if (!string.Equals(order.Marketplace, channel, StringComparison.OrdinalIgnoreCase)) continue;
                var day = order.UpdatedAt.UtcDateTime.Date; if (day < from || day > to) continue;
                if (parameters.DeliveryState.Length > 0 && !(order.Shipments.Any(s => s.State == parameters.DeliveryState) || (parameters.DeliveryState == "Unknown" && order.Shipments.Count == 0))) continue;
                rows.Add(new Dictionary<string, object?> { ["OrderId"] = order.OrderId, ["ShopId"] = order.ShopId, ["Status"] = order.DeliveryLabel, ["Price"] = order.Total, ["Currency"] = order.Currency, ["UpdatedUtc"] = order.UpdatedAt.UtcDateTime });
            }
            read += page.Items.Count;
            progress?.Report(new(ReportRunStage.Query, ReportRunStageStatus.Running, read, page.Total));
            if (page.Items.Count == 0 || offset + PageSize >= page.Total) break;
        }
        return rows;
    }
}
