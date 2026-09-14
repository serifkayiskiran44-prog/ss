using System.Globalization;

namespace TrMarketplaceHubDesktop;

/// <summary>The currency check for one order: the header, every line, the shipping and the refund, against the store's own expected currency when one is known.</summary>
public sealed record OrderCurrencyCheck(string Marketplace, string ShopId, string OrderId, string Outcome, string HeaderCurrency, IReadOnlyList<string> Mismatches, string Words)
{
    public const string Ok = "OK", Mismatch = "MISMATCH", UnknownCurrency = "UNKNOWN_CURRENCY";
}

/// <summary>
/// Currency consistency guard (#947). An order's header carries a currency; a line, the shipping charge or a refund
/// may report their own — usually the same, occasionally not, when a marketplace mixes currencies inside one order
/// or a refund was issued in a different one. Every reported currency must be a recognizable three-letter code: an
/// unrecognized or missing one is never silently treated as the store's default or as TRY — it is its own outcome,
/// UNKNOWN_CURRENCY, and nothing is compared until it is fixed. When the store itself has a known expected currency
/// (its price policy), the header is checked against that too. Every disagreement is named — which line, or the
/// shipping, or the refund, and both currencies — and kept as the shared CURRENCY_MISMATCH exception (#945's
/// store); a clean order clears it. Currency codes and line numbers only — never an amount's meaning beyond that.
/// </summary>
public static class OrderCurrencyConsistency
{
    public static bool IsRecognized(string? currency) => (currency ?? "").Trim().Length == 3 && (currency ?? "").Trim().All(char.IsAsciiLetterUpper);

    public static OrderCurrencyCheck Evaluate(OrderSnapshot order, string? storeExpectedCurrency = null)
    {
        ArgumentNullException.ThrowIfNull(order);
        var m = (order.Marketplace ?? "").Trim().ToLowerInvariant(); var s = (order.ShopId ?? "").Trim(); var o = (order.OrderId ?? "").Trim();
        var header = (order.Currency ?? "").Trim().ToUpperInvariant();
        if (!IsRecognized(header)) return new(m, s, o, OrderCurrencyCheck.UnknownCurrency, header, Array.Empty<string>(), $"sipariş başlığının para birimi tanınmıyor ({Cur(order.Currency)}); varsayılan bir para birimi kullanılmadı, karşılaştırma yapılmadı");
        var mismatches = new List<string>();
        var store = (storeExpectedCurrency ?? "").Trim().ToUpperInvariant();
        if (store.Length > 0 && store != header) mismatches.Add($"mağaza beklenen para birimi {store}, sipariş başlığı {header}");
        for (var i = 0; i < order.Items.Count; i++)
        {
            var line = (order.Items[i].Currency ?? "").Trim().ToUpperInvariant();
            if (line.Length == 0) continue; // not separately reported: assumed the header's
            if (!IsRecognized(line)) mismatches.Add($"satır {N(i + 1)} para birimi tanınmıyor ({Cur(order.Items[i].Currency)})");
            else if (line != header) { var itemLabel = (order.Items[i].Sku ?? "").Trim().Length > 0 ? "SKU " + order.Items[i].Sku!.Trim() : order.Items[i].Title; mismatches.Add($"satır {N(i + 1)} ({itemLabel}) para birimi {line}, başlık {header}"); }
        }
        if (!string.IsNullOrWhiteSpace(order.ShippingCurrency))
        {
            var shipping = order.ShippingCurrency!.Trim().ToUpperInvariant();
            if (!IsRecognized(shipping)) mismatches.Add($"kargo para birimi tanınmıyor ({order.ShippingCurrency.Trim()})");
            else if (shipping != header) mismatches.Add($"kargo para birimi {shipping}, başlık {header}");
        }
        if (order.RefundTotal is not null && !string.IsNullOrWhiteSpace(order.RefundCurrency))
        {
            var refund = order.RefundCurrency!.Trim().ToUpperInvariant();
            if (!IsRecognized(refund)) mismatches.Add($"iade para birimi tanınmıyor ({order.RefundCurrency.Trim()})");
            else if (refund != header) mismatches.Add($"iade para birimi {refund}, başlık {header}");
        }
        if (mismatches.Count == 0) return new(m, s, o, OrderCurrencyCheck.Ok, header, mismatches, $"tutarlı: her bileşen {header}");
        return new(m, s, o, OrderCurrencyCheck.Mismatch, header, mismatches, "para birimi uyumsuz: " + string.Join("; ", mismatches));
    }

    /// <summary>Evaluates every order (against its store's expected currency, when the lookup finds one) and raises or resolves the shared CURRENCY_MISMATCH exception; returns (raised, resolved).</summary>
    public static (int Raised, int Resolved) Sync(LineMappingExceptionStore exceptions, IReadOnlyList<OrderSnapshot> orders, Func<string, string, string?> storeExpectedCurrency, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(exceptions); ArgumentNullException.ThrowIfNull(orders); ArgumentNullException.ThrowIfNull(storeExpectedCurrency);
        var raised = 0; var resolved = 0; var bad = new HashSet<(string, string, string)>();
        foreach (var order in orders)
        {
            var check = Evaluate(order, storeExpectedCurrency((order.Marketplace ?? "").Trim(), (order.ShopId ?? "").Trim()));
            if (check.Outcome == OrderCurrencyCheck.Mismatch)
            { exceptions.Raise(check.Marketplace, check.ShopId, check.OrderId, LineMappingOrderException.CurrencyMismatch, check.Words, nowUtc); bad.Add((check.Marketplace, check.ShopId, check.OrderId)); raised++; }
        }
        foreach (var open in exceptions.Open().Where(x => x.Kind == LineMappingOrderException.CurrencyMismatch && !bad.Contains((x.Marketplace, x.ShopId, x.OrderId))))
        { exceptions.Resolve(open.Marketplace, open.ShopId, open.OrderId, LineMappingOrderException.CurrencyMismatch, nowUtc); resolved++; }
        return (raised, resolved);
    }

    static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
    static string Cur(string? currency) => string.IsNullOrWhiteSpace(currency) ? "belirtilmemiş" : currency.Trim();
}
