using System.Text.RegularExpressions;

namespace TrMarketplaceHubDesktop;

public enum ReportResultKind { None, Ready, TrueEmpty, FilteredEmpty, QueryFailed, Cancelled, SchemaIncompatible }

/// <summary>A call to action on a result state: the key the panel wires, the label the button shows.</summary>
public sealed record ReportResultAction(string Key, string Label);

public sealed record ReportResultStateModel(ReportResultKind Kind, string Title, string Text, IReadOnlyList<ReportResultAction> Actions, SeverityLevel Level)
{
    public bool ShowsGrid => Kind == ReportResultKind.Ready;
    public ReportResultAction? Primary => Actions.Count > 0 ? Actions[0] : null;
}

/// <summary>
/// The result surface's states (#850): a listed result, a store with no orders at all (true empty), a store whose
/// orders the filters excluded (filtered empty), a query that failed, a query that was cancelled, and a result the
/// current column schema cannot show (schema incompatible) -- each with its own words and its own calls to action,
/// so "nothing here" never looks like "something broke" and vice versa. Failure text shown on screen is redacted:
/// secrets, paths, SQL fragments and connection strings are stripped and the line is short; the full sanitized
/// detail belongs to the audit trail, which is where "Tanılamaya git" leads.
/// </summary>
public static class ReportResultStates
{
    public const string ActionRetry = "retry";
    public const string ActionDiagnostics = "diagnostics";
    public const string ActionWidenRange = "widen-range";
    public const string ActionClearState = "clear-state";
    public const string ActionOpenOrders = "open-orders";
    public const string ActionResetColumns = "reset-columns";
    public const int WidenToDays = 90;
    public const int MaxUiMessageLength = 140;

    /// <summary>The state for a finished query: <paramref name="outcome"/> null means no query yet.</summary>
    public static ReportResultStateModel Compose(ReportQueryOutcome? outcome, ReportParameterSet? parameters, ReportParameterSchema? schema, IReadOnlyList<string> visibleColumns)
    {
        ArgumentNullException.ThrowIfNull(visibleColumns);
        if (outcome is null) return new(ReportResultKind.None, "", "", Array.Empty<ReportResultAction>(), SeverityLevel.Info);
        if (outcome.State == ReportRunState.Cancelled) return new(ReportResultKind.Cancelled, "Sorgu iptal edildi", "Sonuç üretilmedi; parametreler korundu.", new[] { new ReportResultAction(ActionRetry, "Yeniden çalıştır") }, SeverityLevel.Warning);
        if (outcome.State == ReportRunState.Failed || outcome.Result is null) return new(ReportResultKind.QueryFailed, "Sorgu çalıştırılamadı", SafeUiMessage(outcome.Message), new[] { new ReportResultAction(ActionRetry, "Yeniden dene"), new ReportResultAction(ActionDiagnostics, "Tanılamaya git") }, SeverityLevel.Blocking);
        var result = outcome.Result;
        if (result.Rows.Count > 0)
        {
            var known = result.Rows[0].Keys.ToHashSet(StringComparer.Ordinal);
            var missing = visibleColumns.Where(c => !known.Contains(c)).ToList();
            if (visibleColumns.Count == 0 || missing.Count == visibleColumns.Count) return new(ReportResultKind.SchemaIncompatible, "Sonuç bu kolon düzeniyle gösterilemiyor", visibleColumns.Count == 0 ? "Görünür kolon yok." : $"Sonuçta bulunmayan kolonlar: {string.Join(", ", missing.Take(5))}.", new[] { new ReportResultAction(ActionResetColumns, "Kolonları varsayılana döndür"), new ReportResultAction(ActionRetry, "Yeniden çalıştır") }, SeverityLevel.Warning);
            return new(ReportResultKind.Ready, "", "", Array.Empty<ReportResultAction>(), SeverityLevel.Success);
        }
        if (result.StoreTotal <= 0) return new(ReportResultKind.TrueEmpty, "Bu mağazada henüz sipariş yok", "Filtreden bağımsız olarak mağazada kayıtlı sipariş bulunmuyor. Siparişler senkronla veya elle eklenince burada listelenir.", new[] { new ReportResultAction(ActionOpenOrders, "Sipariş ekranına git"), new ReportResultAction(ActionRetry, "Yeniden çalıştır") }, SeverityLevel.Info);
        var actions = new List<ReportResultAction>();
        if (schema?.DateRange == true) actions.Add(new(ActionWidenRange, $"Son {WidenToDays} güne genişlet"));
        if (schema?.DeliveryState == true && !string.IsNullOrEmpty(parameters?.DeliveryState)) actions.Add(new(ActionClearState, "Durum filtresini kaldır"));
        actions.Add(new(ActionRetry, "Yeniden çalıştır"));
        return new(ReportResultKind.FilteredEmpty, "Filtreye uyan sipariş yok", $"Mağazada {result.StoreTotal:N0} sipariş var; seçili tarih aralığı, durum veya arama metni hiçbirini kapsamıyor.", actions, SeverityLevel.Info);
    }

    /// <summary>A failure line fit for the screen: secrets redacted, paths, SQL and connection-string fragments stripped, one short sentence.</summary>
    public static string SafeUiMessage(string? message)
    {
        var text = AuditStore.Sanitize(message ?? "").Replace('\r', ' ').Replace('\n', ' ');
        text = Regex.Replace(text, @"(?:[A-Za-z]:[\\/]|\\\\|/)[^\s;,'""]*", "[yol]");
        text = Regex.Replace(text, @"(?i)\b(select|insert|update|delete|create|pragma|from|where)\b[^.;]*", "[sorgu]");
        text = Regex.Replace(text, @"(?i)\b(data source|filename|password|pwd)\s*=\s*[^;\s]+", "[bağlantı]");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length > MaxUiMessageLength) text = text[..(MaxUiMessageLength - 1)].TrimEnd() + "…";
        return text.Length == 0 ? "Ayrıntı tanılama kayıtlarında." : text;
    }
}
