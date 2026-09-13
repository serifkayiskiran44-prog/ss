using System.Globalization;
using TrMarketplaceHubDesktop.Catalog;

namespace TrMarketplaceHubDesktop;

/// <summary>One decided contest for one field: who won, who lost, with which priorities, why, and when — words and numbers only.</summary>
public sealed record SourcePriorityDecision(string ProductId, string Field, string WinnerSourceId, string LoserSourceId, int WinnerPriority, int LoserPriority, string Reason, DateTime DecidedUtc, bool IncomingWins)
{
    /// <summary>The words a row or an audit line shows: "öncelik 120 > 100 · Tedarikçi B kazandı" never a value.</summary>
    public string Words(Func<string, string> nameOf)
    {
        ArgumentNullException.ThrowIfNull(nameOf);
        var winner = nameOf(WinnerSourceId); var loser = nameOf(LoserSourceId);
        return Reason switch
        {
            SourcePriority.ManualLock => $"elle kilitli · {loser} yazamadı",
            SourcePriority.HigherPriority => $"öncelik {WinnerPriority.ToString(CultureInfo.CurrentCulture)} > {LoserPriority.ToString(CultureInfo.CurrentCulture)} · {winner} kazandı, {loser} yazamadı",
            SourcePriority.EqualPriorityFresher => $"eşit öncelik {WinnerPriority.ToString(CultureInfo.CurrentCulture)} · daha yeni gözlem kazandı: {winner}",
            SourcePriority.StaleHolder => $"{loser} bayat (son gözlem eskimiş) · {winner} kazandı",
            _ => $"{winner} kazandı",
        };
    }
}

/// <summary>
/// Source priority decisions (#896). When two sources carry the same product and a feed would write a field
/// another source or the operator currently owns, the outcome is decided by rules a person can read back, not by
/// whichever feed ran last: an operator lock beats every feed; otherwise the higher source priority wins; when
/// the priorities are equal the fresher observation wins (the incoming feed is the fresher one); and a holder
/// whose observation has gone stale — older than its own interval's grace — loses even to a lower priority,
/// because a stale number is worse than a fresh one. Every decision names the winner, the loser, both priorities,
/// the reason and the moment, is written to the audit trail and kept on the field's origin, so a restart shows
/// why the field reads as it does. Ids, priorities, reasons and times only — never a value.
/// </summary>
public static class SourcePriority
{
    public const int DefaultPriority = 100;
    public const string ManualLock = "manual-lock";
    public const string HigherPriority = "higher-priority";
    public const string EqualPriorityFresher = "equal-priority-fresher";
    public const string StaleHolder = "stale-holder";
    public const string AuditAction = "field-priority";
    public static readonly TimeSpan MinimumStaleGrace = TimeSpan.FromHours(6);

    /// <summary>A holder's observation is stale when it is older than three of its source's intervals (at least six hours).</summary>
    public static bool IsStale(FieldOrigin holder, XmlSource? holderSource, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(holder);
        var interval = TimeSpan.FromMinutes(Math.Max(1, holderSource?.IntervalMinutes ?? 30));
        var grace = interval * 3 < MinimumStaleGrace ? MinimumStaleGrace : interval * 3;
        return nowUtc - holder.ObservedUtc > grace;
    }

    /// <summary>Decides whether the incoming feed may write a field the given origin holds; null when there is no contest (no holder, the same source, or an operator's field without a lock which the feed overwrites as before).</summary>
    public static SourcePriorityDecision? Decide(string productId, string field, XmlSource incoming, FieldOrigin? holder, XmlSource? holderSource, bool locked, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (locked) return new(productId, field, "", incoming.Id, int.MaxValue, incoming.Priority, ManualLock, nowUtc, IncomingWins: false);
        if (holder is null || holder.Kind == FieldProvenance.ManualKind || string.IsNullOrEmpty(holder.SourceId) || string.Equals(holder.SourceId, incoming.Id, StringComparison.Ordinal)) return null;
        var holderPriority = holderSource?.Priority ?? DefaultPriority;
        if (IsStale(holder, holderSource, nowUtc)) return new(productId, field, incoming.Id, holder.SourceId, incoming.Priority, holderPriority, StaleHolder, nowUtc, IncomingWins: true);
        if (incoming.Priority > holderPriority) return new(productId, field, incoming.Id, holder.SourceId, incoming.Priority, holderPriority, HigherPriority, nowUtc, IncomingWins: true);
        if (incoming.Priority < holderPriority) return new(productId, field, holder.SourceId, incoming.Id, holderPriority, incoming.Priority, HigherPriority, nowUtc, IncomingWins: false);
        return new(productId, field, incoming.Id, holder.SourceId, incoming.Priority, holderPriority, EqualPriorityFresher, nowUtc, IncomingWins: true);
    }

    /// <summary>The audit row for a decision: module "import", the product and the field, the words — a source is named by id here and by name on screen.</summary>
    public static AuditEvent ToAudit(SourcePriorityDecision decision, Func<string, string> nameOf)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return new AuditEvent { Module = "import", Action = AuditAction, ProductId = decision.ProductId, Outcome = decision.IncomingWins ? "Info" : "Warning", Detail = AuditStore.Redact($"{decision.Field}: {decision.Words(nameOf)}") };
    }
}
