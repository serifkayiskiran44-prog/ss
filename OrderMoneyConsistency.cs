using System.Globalization;

namespace TrMarketplaceHubDesktop;

/// <summary>The consistency check for one order's total against its reported components: line totals (quantity × unit price), a discount, a shipping charge, a tax — each present only if the marketplace reported it.</summary>
public sealed record OrderMoneyCheck(string Marketplace, string ShopId, string OrderId, string Outcome, decimal? Expected, decimal? Actual, decimal? Difference, decimal Tolerance, IReadOnlyList<string> MissingComponents, string Words)
{
    public const string Ok = "OK", Mismatch = "MISMATCH", MissingComponent = "MISSING_COMPONENT", UnknownCurrency = "UNKNOWN_CURRENCY", NoTotal = "NO_TOTAL";
}

/// <summary>
/// Order total consistency (#946). A marketplace's order carries a total; some also report the components it is
/// made of — each line's unit price, a discount, a shipping charge, a tax — and when they do, the total should equal
/// the line totals minus the discount plus the shipping and the tax, within a cent-rounding tolerance (one cent per
/// line, at least one cent). An order missing its total has nothing to check; one whose currency is not a
/// recognizable three-letter code cannot be checked either — an amount without a known currency is not a number
/// to compare. An order missing one or more line prices cannot be summed, and says which lines; that is not itself
/// a mismatch. Only when every component needed is present and the sums disagree is it a mismatch, with the
/// expected total, the actual, the signed difference and the tolerance in the words. A mismatch is kept as the same
/// order exception every other order problem lives in (#945's store), never a silent acceptance.
/// </summary>
public static class OrderMoneyConsistency
{
    public const string Kind = "money-consistency";

    public static bool IsRecognizedCurrency(string? currency) => (currency ?? "").Trim().Length == 3 && (currency ?? "").Trim().All(char.IsAsciiLetterUpper);

    public static OrderMoneyCheck Evaluate(OrderSnapshot order)
    {
        ArgumentNullException.ThrowIfNull(order);
        var m = (order.Marketplace ?? "").Trim().ToLowerInvariant(); var s = (order.ShopId ?? "").Trim(); var o = (order.OrderId ?? "").Trim();
        if (order.Total is not { } total) return new(m, s, o, OrderMoneyCheck.NoTotal, null, null, null, 0m, Array.Empty<string>(), "sipariş toplamı bildirilmemiş; tutarlılık kontrol edilemedi");
        if (!IsRecognizedCurrency(order.Currency)) return new(m, s, o, OrderMoneyCheck.UnknownCurrency, null, total, null, 0m, Array.Empty<string>(), $"para birimi tanınmıyor ({Cur(order.Currency)}); tutar bir para birimi olmadan karşılaştırılamaz");
        var missing = order.Items.Select((i, ix) => (i, ix)).Where(x => x.i.UnitPrice is null).Select(x => "satır " + (x.ix + 1).ToString(CultureInfo.InvariantCulture) + " (" + ((x.i.Sku ?? "").Trim().Length > 0 ? "SKU " + x.i.Sku!.Trim() : x.i.Title) + ")").ToList();
        if (missing.Count > 0) return new(m, s, o, OrderMoneyCheck.MissingComponent, null, total, null, 0m, missing, $"satır fiyatı eksik ({missing.Count} satır); toplam kontrol edilemedi: {string.Join(", ", missing)}");
        var lineTotal = order.Items.Sum(i => i.Quantity * i.UnitPrice!.Value);
        var expected = lineTotal - (order.DiscountTotal ?? 0m) + (order.ShippingTotal ?? 0m) + (order.TaxTotal ?? 0m);
        var tolerance = Math.Max(0.01m, 0.01m * Math.Max(1, order.Items.Count));
        var difference = total - expected;
        if (Math.Abs(difference) <= tolerance)
            return new(m, s, o, OrderMoneyCheck.Ok, expected, total, difference, tolerance, Array.Empty<string>(), $"tutarlı: {Money(total, order.Currency)} (satırlar {Money(lineTotal, order.Currency)}{Signed(order.DiscountTotal, "iskonto", order.Currency)}{Signed(order.ShippingTotal, "kargo", order.Currency, add: true)}{Signed(order.TaxTotal, "vergi", order.Currency, add: true)})");
        return new(m, s, o, OrderMoneyCheck.Mismatch, expected, total, difference, tolerance, Array.Empty<string>(),
            $"uyumsuz: bildirilen {Money(total, order.Currency)}, hesaplanan {Money(expected, order.Currency)} (fark {SignedMoney(difference, order.Currency)}, tolerans {Money(tolerance, order.Currency)}) — satırlar {Money(lineTotal, order.Currency)}{Signed(order.DiscountTotal, "iskonto", order.Currency)}{Signed(order.ShippingTotal, "kargo", order.Currency, add: true)}{Signed(order.TaxTotal, "vergi", order.Currency, add: true)}");
    }

    /// <summary>Evaluates every order and raises or resolves the shared MONEY_MISMATCH exception (#945's store) accordingly; returns (raised, resolved).</summary>
    public static (int Raised, int Resolved) Sync(LineMappingExceptionStore exceptions, IReadOnlyList<OrderSnapshot> orders, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(exceptions); ArgumentNullException.ThrowIfNull(orders);
        var raised = 0; var resolved = 0;
        var mismatched = new HashSet<(string, string, string)>();
        foreach (var order in orders)
        {
            var check = Evaluate(order);
            if (check.Outcome == OrderMoneyCheck.Mismatch)
            {
                exceptions.Raise(check.Marketplace, check.ShopId, check.OrderId, LineMappingOrderException.MoneyMismatch, check.Words, nowUtc);
                mismatched.Add((check.Marketplace, check.ShopId, check.OrderId)); raised++;
            }
        }
        foreach (var open in exceptions.Open().Where(x => x.Kind == LineMappingOrderException.MoneyMismatch && !mismatched.Contains((x.Marketplace, x.ShopId, x.OrderId))))
        { exceptions.Resolve(open.Marketplace, open.ShopId, open.OrderId, LineMappingOrderException.MoneyMismatch, nowUtc); resolved++; }
        return (raised, resolved);
    }

    static string Cur(string? currency) => string.IsNullOrWhiteSpace(currency) ? "belirtilmemiş" : currency.Trim();
    static string Money(decimal value, string currency) => value.ToString("0.00", CultureInfo.InvariantCulture) + (string.IsNullOrWhiteSpace(currency) ? "" : " " + currency.Trim());
    static string SignedMoney(decimal value, string currency) => (value >= 0 ? "+" : "") + Money(value, currency);
    static string Signed(decimal? value, string label, string currency, bool add = false) => value is { } v && v != 0m ? $", {label} {(add ? "+" : "-")}{Money(Math.Abs(v), currency)}" : "";
}
