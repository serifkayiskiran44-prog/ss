using System.Globalization;

namespace TrMarketplaceHubDesktop;

public sealed record ProductAuditEntry(
    string Action,
    string Module,
    string Outcome,
    string When,
    string Repeat,
    int Count,
    DateTime FirstAtUtc,
    DateTime LastAtUtc,
    string Detail)
{
    public bool HasDetail => Detail.Length > 0;
}

public sealed record ProductAuditTimelineView(IReadOnlyList<ProductAuditEntry> Entries, string Headline, bool Truncated)
{
    public bool HasEntries => Entries.Count > 0;
}

/// <summary>
/// A product's change history as something an operator can actually read (#806). Five hundred raw audit rows
/// are not history: the dominant noise is retries, so consecutive events sharing the same action, module and
/// outcome collapse into one entry carrying the attempt count and the span between the first and last try --
/// which is the part that says whether something is still failing. A different outcome breaks the group, so a
/// success after a run of failures is never swallowed. The detail is the expandable part and is re-sanitized
/// and capped here regardless of what the writer did: an audit row is exactly where a failed call's message,
/// with its token, tends to land.
/// </summary>
public static class ProductAuditTimeline
{
    public const int MaxEntries = 200;
    public const int MaxDetailLength = 600;

    public static ProductAuditTimelineView Build(IReadOnlyList<AuditEvent> events, string productId, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(events);
        var mine = events
            .Where(e => e is not null && e.ProductId.Equals((productId ?? "").Trim(), StringComparison.Ordinal))
            .OrderByDescending(e => e.AtUtc)
            .ToList();

        var entries = new List<ProductAuditEntry>();
        var index = 0;
        while (index < mine.Count && entries.Count < MaxEntries)
        {
            var head = mine[index];
            var run = 1;
            while (index + run < mine.Count && SameAttempt(head, mine[index + run])) run++;
            var last = head.AtUtc;
            var first = mine[index + run - 1].AtUtc;
            entries.Add(new(
                head.Action,
                head.Module,
                head.Outcome,
                Ago(nowUtc - last),
                run == 1 ? "" : $"{run.ToString("N0", CultureInfo.CurrentCulture)} kez · ilk denemeden bu yana {Span(last - first)}",
                run,
                first,
                last,
                Detail(head.Detail)));
            index += run;
        }

        var truncated = index < mine.Count;
        var headline = mine.Count == 0
            ? "Bu ürün için denetim kaydı yok."
            : truncated
                ? $"{mine.Count.ToString("N0", CultureInfo.CurrentCulture)} kayıttan en yeni {entries.Count.ToString("N0", CultureInfo.CurrentCulture)} girdi"
                : $"{entries.Count.ToString("N0", CultureInfo.CurrentCulture)} girdi";
        return new(entries, headline, truncated);
    }

    static bool SameAttempt(AuditEvent left, AuditEvent right) =>
        string.Equals(left.Action, right.Action, StringComparison.Ordinal)
        && string.Equals(left.Module, right.Module, StringComparison.Ordinal)
        && string.Equals(left.Outcome, right.Outcome, StringComparison.Ordinal);

    static string Detail(string? value)
    {
        var safe = AuditStore.Sanitize(value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return safe.Length > MaxDetailLength ? safe[..MaxDetailLength] + "…" : safe;
    }

    static string Ago(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        if (elapsed.TotalMinutes < 1) return "az önce";
        if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes} dakika önce";
        if (elapsed.TotalDays < 1) return $"{(int)elapsed.TotalHours} saat önce";
        return $"{(int)elapsed.TotalDays} gün önce";
    }

    static string Span(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        if (elapsed.TotalMinutes < 1) return "bir dakikadan az";
        if (elapsed.TotalHours < 1) return $"{(int)elapsed.TotalMinutes} dakika";
        if (elapsed.TotalDays < 1) return $"{(int)elapsed.TotalHours} saat";
        return $"{(int)elapsed.TotalDays} gün";
    }
}
