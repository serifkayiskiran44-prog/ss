using System.IO;
using System.Text;

namespace TrMarketplaceHubDesktop;

/// <summary>A report's rows as produced by the query, ready for the result grid and for an export of the chosen columns. <paramref name="StoreTotal"/> is how many orders the store holds regardless of the filters (#850: it tells "nothing matched" from "nothing exists").</summary>
public sealed record ReportResult(ReportDefinition Definition, ReportParameterSet Parameters, IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, DateTime CreatedUtc, int StoreTotal = 0);

public sealed record ReportQueryOutcome(ReportRunState State, ReportResult? Result, string Message);

public sealed record ReportRunOutcome(ReportRunState State, int Rows, string Message);

/// <summary>
/// The one report the reports workspace runs by itself (#847): the orders list. <see cref="QueryAsync"/> reads the
/// store's orders for the validated parameters and builds the result rows (stages query, generate); <see
/// cref="ExportAsync"/> writes the chosen columns of a result as CSV through the existing <see
/// cref="ReportTemplateRenderer"/>, whose column allow-list has no customer field (stage export). Every outcome --
/// listed, written, cancelled, failed -- lands in <see cref="ReportRunStore"/> and the audit trail with the
/// parameter summary minus the query text as its note (#848: the operator's own search text is never logged).
/// The classified tracking column (#849) is masked in the rows themselves, so the grid and the file can only ever
/// show the masked form.
/// </summary>
public static class ReportRunner
{
    public const string OrdersCsvKey = "orders-csv";

    public static bool CanRun(ReportDefinition definition) => definition is not null && string.Equals(definition.Key, OrdersCsvKey, StringComparison.Ordinal);

    public static async Task<ReportQueryOutcome> QueryAsync(string? directory, ReportDefinition definition, ReportParameterSet parameters, IReadOnlyCollection<string>? allowedStoreKeys, CancellationToken cancellationToken = default, IProgress<ReportRunProgressEvent>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(definition); ArgumentNullException.ThrowIfNull(parameters);
        if (!CanRun(definition)) throw new InvalidOperationException("Bu rapor sahibi ekranda üretilir; buradan çalıştırılmaz.");
        var findings = ReportParameters.Validate(parameters, definition, allowedStoreKeys, DateTime.UtcNow);
        if (!ReportParameters.IsValid(findings)) throw new InvalidOperationException(findings.First(f => f.Level == SeverityLevel.Blocking).Message);
        var started = DateTime.UtcNow; var runs = new ReportRunStore(directory);
        var note = ReportParameters.Summary(parameters, ReportParameters.SchemaFor(definition), includeQuery: false);
        var stage = ReportRunStage.Query;
        try
        {
            progress?.Report(new(ReportRunStage.Query, ReportRunStageStatus.Running));
            var rows = await Task.Run(() => OrdersRows(directory, parameters, cancellationToken, progress), cancellationToken);
            progress?.Report(new(ReportRunStage.Query, ReportRunStageStatus.Done, rows.Count, rows.Count));
            stage = ReportRunStage.Generate; progress?.Report(new(stage, ReportRunStageStatus.Running, 0, rows.Count));
            cancellationToken.ThrowIfCancellationRequested();
            var storeTotal = rows.Count > 0 ? rows.Count : await Task.Run(() => StoreOrderCount(directory, parameters.StoreKey), cancellationToken);
            var result = new ReportResult(definition, parameters, rows, DateTime.UtcNow, storeTotal);
            progress?.Report(new(stage, ReportRunStageStatus.Done, rows.Count, rows.Count));
            var message = rows.Count == 0 ? "Aralıkta sipariş yok." : $"{rows.Count:N0} sipariş listelendi.";
            Record(directory, runs, started, ReportRunState.Succeeded, rows.Count, note, parameters.StoreKey, message);
            return new(ReportRunState.Succeeded, result, message);
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new(stage, ReportRunStageStatus.Cancelled));
            const string message = "Sorgu iptal edildi.";
            Record(directory, runs, started, ReportRunState.Cancelled, 0, note, parameters.StoreKey, message);
            return new(ReportRunState.Cancelled, null, message);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or Microsoft.Data.Sqlite.SqliteException)
        {
            // #850: the exception type and its sanitized text go to the run store and the audit trail (the safe diagnostics);
            // the screen gets the redacted line ReportResultStates.SafeUiMessage makes of the message.
            var safe = AuditStore.Sanitize(error.Message);
            progress?.Report(new(stage, ReportRunStageStatus.Failed, Note: safe));
            Record(directory, runs, started, ReportRunState.Failed, 0, note, parameters.StoreKey, $"{error.GetType().Name}: {safe}");
            return new(ReportRunState.Failed, null, "Sorgu çalıştırılamadı: " + safe);
        }
    }

    /// <summary>How many orders the store holds at all -- by shop id, whatever the date, state or search.</summary>
    public static int StoreOrderCount(string? directory, string storeKey)
    {
        var (_, shop) = ReportParameters.SplitStore(storeKey);
        return new OrdersStore(directory).ReadPage(null, shop, null, null, 0, 1).Total;
    }

    /// <summary>Writes the chosen columns of a result as CSV; the columns must belong to the report's schema and the renderer's allow-list.</summary>
    public static async Task<ReportRunOutcome> ExportAsync(string? directory, ReportResult result, IReadOnlyList<string> columns, string path, CancellationToken cancellationToken = default, IProgress<ReportRunProgressEvent>? progress = null, bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(result); ArgumentNullException.ThrowIfNull(columns);
        var schema = ReportColumns.SchemaFor(result.Definition).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        var chosen = columns.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.Ordinal).ToList();
        if (chosen.Count == 0) throw new ArgumentException("Dışa aktarılacak en az bir kolon seçin.", nameof(columns));
        var foreign = chosen.FirstOrDefault(c => !schema.Contains(c)); if (foreign is not null) throw new ArgumentException($"Şemada olmayan kolon: {AuditStore.Sanitize(foreign)}", nameof(columns));
        if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Rapor dosyası .csv ile bitmeli.", nameof(path));
        var started = DateTime.UtcNow; var runs = new ReportRunStore(directory);
        var note = ReportParameters.Summary(result.Parameters, ReportParameters.SchemaFor(result.Definition), includeQuery: false) + $" · CSV: {chosen.Count} kolon";
        try
        {
            progress?.Report(new(ReportRunStage.Export, ReportRunStageStatus.Running, 0, result.Rows.Count));
            var csv = ReportTemplateRenderer.Render(new ReportTemplate("orders", chosen), result.Rows);
            cancellationToken.ThrowIfCancellationRequested();
            var temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", Path.GetFileNameWithoutExtension(path) + ".tmp-" + Guid.NewGuid().ToString("N")[..8] + ".csv");
            try { File.WriteAllText(temp, csv, new UTF8Encoding(true)); ExportFiles.Commit(temp, path, overwrite); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            progress?.Report(new(ReportRunStage.Export, ReportRunStageStatus.Done, result.Rows.Count, result.Rows.Count));
            var message = result.Rows.Count == 0 ? "Aralıkta sipariş yok; yalnız başlık satırı yazıldı." : $"{result.Rows.Count:N0} sipariş yazıldı.";
            Record(directory, runs, started, ReportRunState.Succeeded, result.Rows.Count, note, result.Parameters.StoreKey, message);
            return new(ReportRunState.Succeeded, result.Rows.Count, message);
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new(ReportRunStage.Export, ReportRunStageStatus.Cancelled));
            const string message = "Dışa aktarma iptal edildi; dosya yazılmadı.";
            Record(directory, runs, started, ReportRunState.Cancelled, 0, note, result.Parameters.StoreKey, message);
            return new(ReportRunState.Cancelled, 0, message);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            var safe = AuditStore.Sanitize(error.Message);
            progress?.Report(new(ReportRunStage.Export, ReportRunStageStatus.Failed, Note: safe));
            Record(directory, runs, started, ReportRunState.Failed, 0, note, result.Parameters.StoreKey, safe);
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

    /// <summary>One row per order of the parameter store, within the whole-day range (by the record's update time) and the delivery state; "Unknown" also means "no package". The tracking column is masked here, so nothing downstream can unmask it. Reports the query stage page by page; the total is unknown until the first page answers.</summary>
    public static IReadOnlyList<IReadOnlyDictionary<string, object?>> OrdersRows(string? directory, ReportParameterSet parameters, CancellationToken cancellationToken = default, IProgress<ReportRunProgressEvent>? progress = null)
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
                var tracking = string.Join(", ", order.Shipments.Select(s => OrderShippingSection.MaskTracking(s.TrackingNumber)).Where(t => t != "—"));
                rows.Add(new Dictionary<string, object?> { ["OrderId"] = order.OrderId, ["ShopId"] = order.ShopId, ["Status"] = order.DeliveryLabel, ["Price"] = order.Total, ["Currency"] = order.Currency, ["UpdatedUtc"] = order.UpdatedAt.UtcDateTime, ["Tracking"] = tracking });
            }
            read += page.Items.Count;
            progress?.Report(new(ReportRunStage.Query, ReportRunStageStatus.Running, read, page.Total));
            if (page.Items.Count == 0 || offset + PageSize >= page.Total) break;
        }
        return rows;
    }
}
