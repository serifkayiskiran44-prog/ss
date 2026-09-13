using System.Text.RegularExpressions;

namespace TrMarketplaceHubDesktop;

/// <summary>A customer's contact data for one order: kept apart from the order snapshot, so no export, transfer or log that serialises orders ever carries it.</summary>
public sealed record OrderCustomer(string Marketplace, string ShopId, string OrderId, string Name, string Email, string Phone, string Address)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Name) && string.IsNullOrWhiteSpace(Email) && string.IsNullOrWhiteSpace(Phone) && string.IsNullOrWhiteSpace(Address);
}

/// <summary>Whether and how customer data may be revealed: off by default, a reason required, and a reveal that re-masks itself.</summary>
public sealed record PiiRevealPolicy(bool Allowed = false, int RevealSeconds = 30, bool RequireReason = true)
{
    public const int MinSeconds = 5, MaxSeconds = 300;
    public PiiRevealPolicy Normalized() => this with { RevealSeconds = Math.Clamp(RevealSeconds, MinSeconds, MaxSeconds) };
}

public sealed record PiiRevealDecision(bool Allowed, string Reason, string DeniedBecause, DateTime? RemaskAtUtc);

/// <summary>
/// Masking and reveal rules for customer contact data (#841). Every field is shown masked by default -- an e-mail
/// keeps its first letter and domain suffix, a phone its last two digits, an address its first word, a name its
/// initial -- so a field is recognisable without being readable. A reveal is an explicit request: refused when the
/// policy does not allow it or when the required reason is missing, and when granted it expires at a fixed time so
/// nothing stays open. Every request, granted or refused, is an audit event that names the fields and the reason
/// and never a value.
/// </summary>
public static class PiiReveal
{
    public const int MinReasonLength = 5;
    public static readonly IReadOnlyList<string> Fields = new[] { "Ad", "E-posta", "Telefon", "Adres" };

    public static string MaskEmail(string? value)
    {
        var v = (value ?? "").Trim(); if (v.Length == 0) return "—";
        var at = v.IndexOf('@');
        if (at <= 0 || at == v.Length - 1) return v[..1] + "•••";
        var local = v[..at]; var domain = v[(at + 1)..];
        var dot = domain.LastIndexOf('.');
        var suffix = dot > 0 ? domain[dot..] : "";
        return local[..1] + "••@" + domain[..1] + "•••" + suffix;
    }

    public static string MaskPhone(string? value)
    {
        var digits = new string((value ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return "—";
        if (digits.Length <= 2) return "••";
        return ((value ?? "").Trim().StartsWith('+') ? "+" : "") + new string('•', Math.Min(9, digits.Length - 2)) + digits[^2..];
    }

    public static string MaskAddress(string? value)
    {
        var v = Regex.Replace((value ?? "").Trim(), @"\s+", " "); if (v.Length == 0) return "—";
        var space = v.IndexOf(' ');
        return (space > 0 ? v[..space] : v[..Math.Min(3, v.Length)]) + " •••";
    }

    public static string MaskName(string? value)
    {
        var v = (value ?? "").Trim(); if (v.Length == 0) return "—";
        return string.Join(" ", v.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(p => p[..1] + "•••"));
    }

    /// <summary>The four fields as the default view shows them.</summary>
    public static IReadOnlyList<(string Field, string Masked)> Masked(OrderCustomer customer)
    {
        ArgumentNullException.ThrowIfNull(customer);
        return new[] { ("Ad", MaskName(customer.Name)), ("E-posta", MaskEmail(customer.Email)), ("Telefon", MaskPhone(customer.Phone)), ("Adres", MaskAddress(customer.Address)) };
    }

    /// <summary>Grant or refuse a reveal; a grant carries the moment it re-masks itself.</summary>
    public static PiiRevealDecision Request(PiiRevealPolicy policy, string? reason, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var p = policy.Normalized();
        var text = Regex.Replace((reason ?? "").Trim(), @"\s+", " ");
        if (!p.Allowed) return new(false, text, "Politika kişisel veri gösterimine izin vermiyor (Politika merkezi → Kişisel veri).", null);
        if (p.RequireReason && text.Length < MinReasonLength) return new(false, text, $"Gerekçe gerekli (en az {MinReasonLength} karakter).", null);
        return new(true, text, "", nowUtc.AddSeconds(p.RevealSeconds));
    }

    /// <summary>The audit line for a request: module, action, order, the fields and the reason -- no value, whatever was revealed.</summary>
    public static AuditEvent AuditFor(OrderCustomer customer, PiiRevealDecision decision, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(customer); ArgumentNullException.ThrowIfNull(decision);
        var reason = AuditStore.Sanitize(decision.Reason);
        return new AuditEvent
        {
            AtUtc = nowUtc, Module = "orders", Action = decision.Allowed ? "pii-reveal" : "pii-reveal-denied",
            Marketplace = customer.Marketplace, ShopId = customer.ShopId, OrderId = customer.OrderId, Outcome = decision.Allowed ? "Info" : "Warning",
            Detail = (decision.Allowed ? $"Alanlar: {string.Join(", ", Fields)} · gerekçe: {reason} · otomatik maskeleme: {decision.RemaskAtUtc:HH:mm:ss}Z" : $"Reddedildi: {decision.DeniedBecause}" + (reason.Length > 0 ? $" · gerekçe: {reason}" : "")),
        };
    }
}
