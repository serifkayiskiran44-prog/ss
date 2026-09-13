using System.Globalization;
using System.Text.Json;

namespace TrMarketplaceHubDesktop;

/// <summary>One run's parameters. Dates are whole UTC days, both ends inclusive; an empty delivery state means every state.</summary>
public sealed record ReportParameterSet(string ReportKey, string StoreKey = "", DateTime? FromUtc = null, DateTime? ToUtc = null, string DeliveryState = "", string Query = "");

/// <summary>Which parameters a report takes; nothing else is drawn or persisted for it.</summary>
public sealed record ReportParameterSchema(bool Store, bool DateRange, bool DeliveryState, bool Query)
{
    public bool Any => Store || DateRange || DeliveryState || Query;
}

public sealed record ReportParameterFinding(string Field, string Message, SeverityLevel Level);

/// <summary>The persisted shape of a saved filter: strings and ISO dates only, never a secret, never a customer field.</summary>
public sealed record ReportParameterPayload(string? Store, string? From, string? To, string? State, string? Query);

/// <summary>
/// Report parameters (#847): the typed set per report (store for store-scoped reports, a whole-day UTC date range and
/// a delivery state for order reports, a search text for lists), defaults (the first offered store, the last 30
/// days, every state), validation with one finding per field (no store, a store not offered, an inverted or
/// over-long range, an unknown state, an over-long query; a future end date only warns), and a saved-filter
/// codec that round-trips those fields and nothing else -- so a saved filter can never carry a credential or a
/// customer field, and a hostile payload loads as "unreadable", not as parameters.
/// </summary>
public static class ReportParameters
{
    public const int DefaultRangeDays = 30;
    public const int MaxRangeDays = 366;
    public const int MaxQueryLength = 120;

    public static ReportParameterSchema SchemaFor(ReportDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var orders = definition.Key is "orders" or "orders-csv" or "product-orders";
        return new(Store: definition.Scope == ReportScope.Store, DateRange: orders, DeliveryState: definition.Key is "orders" or "orders-csv", Query: definition.Key is not ("support-package" or "api-health"));
    }

    public static ReportParameterSet Defaults(ReportDefinition definition, IReadOnlyCollection<string>? allowedStoreKeys, DateTime nowUtc)
    {
        var schema = SchemaFor(definition); var today = Day(nowUtc);
        return new(definition.Key, schema.Store ? allowedStoreKeys?.FirstOrDefault() ?? "" : "", schema.DateRange ? today.AddDays(-DefaultRangeDays) : null, schema.DateRange ? today : null, "", "");
    }

    public static IReadOnlyList<ReportParameterFinding> Validate(ReportParameterSet parameters, ReportDefinition definition, IReadOnlyCollection<string>? allowedStoreKeys, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(parameters); var schema = SchemaFor(definition); var findings = new List<ReportParameterFinding>();
        if (schema.Store)
        {
            if (string.IsNullOrWhiteSpace(parameters.StoreKey)) findings.Add(new("store", "Mağaza seçin; bu rapor mağaza kapsamlıdır.", SeverityLevel.Blocking));
            else if (allowedStoreKeys is not null && !allowedStoreKeys.Contains(parameters.StoreKey, StringComparer.Ordinal)) findings.Add(new("store", "Seçilen mağaza bu oturumda sunulmuyor; yanlış mağaza için rapor üretilmez.", SeverityLevel.Blocking));
        }
        else if (!string.IsNullOrWhiteSpace(parameters.StoreKey)) findings.Add(new("store", "Bu rapor mağaza kapsamlı değil; mağaza yok sayılır.", SeverityLevel.Warning));
        if (schema.DateRange)
        {
            if (parameters.FromUtc is null || parameters.ToUtc is null) findings.Add(new("range", "Başlangıç ve bitiş tarihini girin.", SeverityLevel.Blocking));
            else if (Day(parameters.FromUtc.Value) > Day(parameters.ToUtc.Value)) findings.Add(new("range", "Başlangıç tarihi bitişten sonra olamaz.", SeverityLevel.Blocking));
            else if ((Day(parameters.ToUtc.Value) - Day(parameters.FromUtc.Value)).TotalDays > MaxRangeDays) findings.Add(new("range", $"Aralık en fazla {MaxRangeDays} gün olabilir.", SeverityLevel.Blocking));
            else if (Day(parameters.ToUtc.Value) > Day(nowUtc).AddDays(1)) findings.Add(new("to", "Bitiş tarihi gelecekte; sonuç bugüne kadar olan kayıtlardır.", SeverityLevel.Warning));
        }
        if (schema.DeliveryState && parameters.DeliveryState.Length > 0 && !OrdersRules.States.Contains(parameters.DeliveryState, StringComparer.Ordinal)) findings.Add(new("state", "Bilinmeyen teslimat durumu.", SeverityLevel.Blocking));
        if (parameters.Query.Length > MaxQueryLength) findings.Add(new("query", $"Arama metni en fazla {MaxQueryLength} karakter olabilir.", SeverityLevel.Blocking));
        else if (parameters.Query.Any(char.IsControl)) findings.Add(new("query", "Arama metninde kontrol karakteri olamaz.", SeverityLevel.Blocking));
        return findings;
    }

    public static bool IsValid(IEnumerable<ReportParameterFinding> findings) => !findings.Any(f => f.Level == SeverityLevel.Blocking);

    public static string Serialize(ReportParameterSet parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return JsonSerializer.Serialize(new ReportParameterPayload(parameters.StoreKey, Iso(parameters.FromUtc), Iso(parameters.ToUtc), parameters.DeliveryState, parameters.Query));
    }

    /// <summary>Null when the payload is not a saved filter (garbage, oversized, wrong shape); fields are trimmed, capped and checked, never trusted.</summary>
    public static ReportParameterSet? Deserialize(string reportKey, string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 4000) return null;
        try
        {
            var payload = JsonSerializer.Deserialize<ReportParameterPayload>(json);
            if (payload is null) return null;
            var state = payload.State?.Trim() ?? "";
            return new(reportKey, Clean(payload.Store, 120), ParseDay(payload.From), ParseDay(payload.To), OrdersRules.States.Contains(state, StringComparer.Ordinal) ? state : "", Clean(payload.Query, MaxQueryLength));
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The parameters as one line for a run note or a status: labels and dates, no payload.</summary>
    public static string Summary(ReportParameterSet parameters, ReportParameterSchema schema)
    {
        var parts = new List<string>();
        if (schema.Store) parts.Add(parameters.StoreKey.Length == 0 ? "Mağaza: seçilmedi" : "Mağaza: " + StoreLabel(parameters.StoreKey));
        if (schema.DateRange && parameters.FromUtc is not null && parameters.ToUtc is not null) parts.Add($"{parameters.FromUtc.Value.ToString("d", CultureInfo.CurrentCulture)}–{parameters.ToUtc.Value.ToString("d", CultureInfo.CurrentCulture)}");
        if (schema.DeliveryState) parts.Add("Durum: " + (parameters.DeliveryState.Length == 0 ? "tümü" : OrdersRules.Label(parameters.DeliveryState)));
        if (schema.Query && parameters.Query.Length > 0) parts.Add("Arama: " + parameters.Query);
        return string.Join(" · ", parts);
    }

    public static string StoreLabel(string key) { var (channel, shop) = SplitStore(key); return shop.Length == 0 ? channel : $"{channel} · {shop}"; }
    public static (string Channel, string Shop) SplitStore(string key) { var k = (key ?? "").Trim(); var i = k.IndexOf('|'); return i < 0 ? (k, "") : (k[..i], k[(i + 1)..]); }

    static DateTime Day(DateTime value) => DateTime.SpecifyKind(value.Date, DateTimeKind.Utc);
    static string? Iso(DateTime? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    static DateTime? ParseDay(string? value) => DateTime.TryParseExact((value ?? "").Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var day) ? Day(day) : null;
    static string Clean(string? value, int max) { var text = (value ?? "").Trim(); if (text.Any(char.IsControl)) return ""; return text.Length > max ? text[..max] : text; }
}
