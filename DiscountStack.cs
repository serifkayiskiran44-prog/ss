using System.Globalization;

namespace TrMarketplaceHubDesktop;

/// <summary>One step of the stack: what was applied, to what, and the value before and after.</summary>
public sealed record DiscountStep(string Kind, string Words, decimal Before, decimal After);

/// <summary>The stack's outcome: the state, the cost actually paid after the supplier's discount, the sale price after every discount and the shop's rounding, the steps in the order they ran, and the words.</summary>
public sealed record DiscountStackResult(string State, decimal EffectiveCost, decimal SalePrice, IReadOnlyList<DiscountStep> Steps, string Words)
{
    public const string Ok = "OK", NoDiscount = "NO_DISCOUNT", Unsupported = "UNSUPPORTED", Invalid = "INVALID", ZeroPrice = "ZERO_PRICE";
    public bool Blocks => State is Unsupported or Invalid or ZeroPrice;
}

/// <summary>
/// The discount stack (#926). Three discounts can reach a price and their order is fixed, never a matter of which
/// field was typed first: (1) the supplier's discount, a percentage off the cost — the cost the formula and the
/// money gate then see; (2) the sale price as the rule computes it from that cost, rounded by the shop's profile;
/// (3) the shop's own discount, a percentage off the sale price; (4) the channel's discount, off the sale price,
/// in the two forms a channel supports here — "percent:10" or "amount:5" in the sale currency; (5) the shop's
/// rounding again on the result. Between steps four decimals are kept. A negative input or a percentage over a
/// hundred is INVALID, a channel discount in any other form is UNSUPPORTED (it is neither guessed nor ignored), a
/// price discounted to nothing is ZERO_PRICE; each blocks the price by name. Nothing typed is NO_DISCOUNT.
/// </summary>
public static class DiscountStack
{
    public const string SupplierKind = "supplier", LocalKind = "local", ChannelKind = "channel";
    public const string PercentForm = "percent", AmountForm = "amount";

    /// <summary>"percent:10" or "amount:5" → the form and the value; empty → null; anything else → null with supported=false.</summary>
    public static (string Form, decimal Value)? ParseChannel(string? text, out bool supported)
    {
        supported = true;
        var value = (text ?? "").Trim(); if (value.Length == 0) return null;
        var cut = value.IndexOf(':');
        if (cut <= 0) { supported = false; return null; }
        var form = value[..cut].Trim().ToLowerInvariant();
        if (form is not (PercentForm or AmountForm) || !decimal.TryParse(value[(cut + 1)..].Trim(), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)) { supported = false; return null; }
        return (form, number);
    }

    /// <summary>Refuses the numbers that are not a discount; an unknown channel form is not refused here — it is the calculation's UNSUPPORTED state.</summary>
    public static void Validate(decimal? supplierPercent, decimal? localPercent)
    {
        if (supplierPercent is { } s && (s < 0 || s > 100)) throw new ArgumentException("Tedarikçi indirimi 0 ile 100 arasında bir yüzde olmalı.");
        if (localPercent is { } l && (l < 0 || l > 100)) throw new ArgumentException("Yerel indirim 0 ile 100 arasında bir yüzde olmalı.");
    }

    public static DiscountStackResult Apply(decimal cost, Func<decimal, decimal> salePriceOfCost, decimal? supplierPercent, decimal? localPercent, string? channelDiscount, Func<decimal, decimal> round)
    {
        ArgumentNullException.ThrowIfNull(salePriceOfCost); ArgumentNullException.ThrowIfNull(round);
        var steps = new List<DiscountStep>();
        if (supplierPercent is { } sp && (sp < 0 || sp > 100)) return Refused(DiscountStackResult.Invalid, cost, $"tedarikçi indirimi geçersiz: %{N(sp)}; 0 ile 100 arasında olmalı");
        if (localPercent is { } lp && (lp < 0 || lp > 100)) return Refused(DiscountStackResult.Invalid, cost, $"yerel indirim geçersiz: %{N(lp)}; 0 ile 100 arasında olmalı");
        var channel = ParseChannel(channelDiscount, out var supported);
        if (!supported) return Refused(DiscountStackResult.Unsupported, cost, $"desteklenmeyen kanal indirimi: '{AuditStore.Redact((channelDiscount ?? "").Trim())}'; 'percent:10' ya da 'amount:5' biçimi kullanın");
        if (channel is { } c && (c.Value < 0 || (c.Form == PercentForm && c.Value > 100))) return Refused(DiscountStackResult.Invalid, cost, $"kanal indirimi geçersiz: {c.Form} {N(c.Value)}");

        var effectiveCost = cost;
        if (supplierPercent is { } supplier && supplier > 0) { effectiveCost = Four(cost * (1m - supplier / 100m)); steps.Add(new(SupplierKind, $"tedarikçi indirimi %{N(supplier)}: maliyet {N(cost)} → {N(effectiveCost)}", cost, effectiveCost)); }
        var sale = round(salePriceOfCost(effectiveCost));
        if (localPercent is { } local && local > 0) { var after = Four(sale * (1m - local / 100m)); steps.Add(new(LocalKind, $"yerel indirim %{N(local)}: {N(sale)} → {N(after)}", sale, after)); sale = after; }
        if (channel is { } ch && ch.Value > 0)
        {
            var after = ch.Form == PercentForm ? Four(sale * (1m - ch.Value / 100m)) : Four(sale - ch.Value);
            steps.Add(new(ChannelKind, $"kanal indirimi {ch.Form} {N(ch.Value)}: {N(sale)} → {N(after)}", sale, after)); sale = after;
        }
        var final = round(sale);
        if (steps.Count > 0 && final != sale) steps.Add(new("rounding", $"yuvarlama: {N(sale)} → {N(final)}", sale, final));
        if (final <= 0) return new(DiscountStackResult.ZeroPrice, effectiveCost, final, steps, "indirimler fiyatı sıfıra indirdi: " + string.Join("; ", steps.Select(s => s.Words)) + "; sıfır fiyat gönderilmez");
        if (steps.Count == 0) return new(DiscountStackResult.NoDiscount, cost, final, steps, "indirim yok");
        return new(DiscountStackResult.Ok, effectiveCost, final, steps, string.Join("; ", steps.Select(s => s.Words)));
    }

    static DiscountStackResult Refused(string state, decimal cost, string words) => new(state, cost, 0m, Array.Empty<DiscountStep>(), words);
    static decimal Four(decimal value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);
    static string N(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
