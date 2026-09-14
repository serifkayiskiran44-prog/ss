using System;

public enum MoneyPriceStatus { Ready, BlockedNegativeMargin, BlockedMissingInput, BlockedStaleFx, BlockedInvalid, IncompleteCost } // #927: a required fee (commission, shipping, payment) is missing -- no net figure is shown as an estimate, the live write is blocked

public sealed record MoneyPriceInput(string Sku, string Channel, string Shop, decimal SalePrice, decimal CostTry, string Currency)
{
    public decimal? CommissionRatePercent { get; init; }
    public decimal? EstimatedShipping { get; init; }
    public decimal? TransactionCost { get; init; }
    public decimal? VatRatePercent { get; init; }
    public bool VatIncludedInSale { get; init; }
    public decimal? FxRateTryPerUnit { get; init; }
    public DateTimeOffset? FxSnapshotUtc { get; init; }
    public DateTimeOffset? AsOfUtc { get; init; }
    /// <summary>#924: how old the FX snapshot may be; null keeps the 24 hour default.</summary>
    public TimeSpan? FxStaleAfter { get; init; }
}

/// <summary>#928: one line of the net margin's explanation -- what it is, the amount in TRY (deductions negative), the words.</summary>
public sealed record MoneyBreakdownLine(string Key, string Label, decimal AmountTry, string Words);

public sealed record MoneyPriceResult(
    MoneyPriceInput Input,
    decimal SalePriceTry,
    decimal GrossContribution,
    decimal NetContribution,
    decimal MarginPercent,
    MoneyPriceStatus Status,
    string ChannelShop,
    DateTimeOffset CalculatedAtUtc,
    IReadOnlyList<string>? MissingFees = null, // #927: the required fees the input lacks, named
    IReadOnlyList<MoneyBreakdownLine>? Breakdown = null) // #928: the immutable explanation of how the net was reached -- selling price, VAT if included, commission, shipping, payment, cost, a rounding difference when the cents need one, net
{
    public bool IsApproximate => Input.CommissionRatePercent is null || Input.EstimatedShipping is null || Input.TransactionCost is null || Input.VatRatePercent is null;
    /// <summary>#927: COMPLETE when every required fee is known, INCOMPLETE_COST when one is missing -- the state the card and the gate share.</summary>
    public string CostCompleteness => MissingFees is { Count: > 0 } ? "INCOMPLETE_COST" : "COMPLETE";
    /// <summary>#928: the explanation in words, one per line; empty when the gate could not compute.</summary>
    public IReadOnlyList<string> Explanation => Breakdown is null ? Array.Empty<string>() : Breakdown.Select(l => l.Words).ToList();
}

public static class MoneyPriceCalculator
{
    /// <summary>#927: the required fees a rule lacks, named in one place for the gate and the card -- commission, shipping, payment/transaction; zero is a legitimate fee, only null is missing.</summary>
    public static IReadOnlyList<string> MissingFees(decimal? commission, decimal? shipping, decimal? transaction)
    { var missing = new List<string>(); if (commission is null) missing.Add("komisyon"); if (shipping is null) missing.Add("kargo"); if (transaction is null) missing.Add("işlem/ödeme"); return missing; }

    public static MoneyPriceResult Calculate(MoneyPriceInput input)
    {
        if (input is null || string.IsNullOrWhiteSpace(input.Sku) || string.IsNullOrWhiteSpace(input.Channel) || string.IsNullOrWhiteSpace(input.Shop) || input.SalePrice <= 0 || input.CostTry < 0)
            return Result(input, 0, 0, 0, MoneyPriceStatus.BlockedInvalid);
        if (!string.Equals(input.Currency, "TRY", StringComparison.OrdinalIgnoreCase) && (input.FxRateTryPerUnit is null || input.FxRateTryPerUnit <= 0))
            return Result(input, 0, 0, 0, MoneyPriceStatus.BlockedMissingInput);
        if (input.FxSnapshotUtc is null)
            return Result(input, 0, 0, 0, MoneyPriceStatus.BlockedMissingInput);
        var now = input.AsOfUtc ?? DateTimeOffset.UtcNow;
        if (now - input.FxSnapshotUtc.Value > (input.FxStaleAfter ?? TimeSpan.FromHours(24))) // #924: the operator's window
            return Result(input, 0, 0, 0, MoneyPriceStatus.BlockedStaleFx);
        // #927: a missing required fee is its own state -- named, never a net figure shown as an estimate; the tax rate missing stays the input gap it was.
        var missingFees = MissingFees(input.CommissionRatePercent, input.EstimatedShipping, input.TransactionCost);
        if (missingFees.Count > 0) return Result(input, 0, 0, 0, MoneyPriceStatus.IncompleteCost, missingFees);
        if (input.VatRatePercent is null)
            return Result(input, 0, 0, 0, MoneyPriceStatus.BlockedMissingInput);
        if (input.CommissionRatePercent < 0 || input.VatRatePercent < 0 || input.VatRatePercent >= 100 || input.EstimatedShipping < 0 || input.TransactionCost < 0)
            return Result(input, 0, 0, 0, MoneyPriceStatus.BlockedInvalid);
        var rate = string.Equals(input.Currency, "TRY", StringComparison.OrdinalIgnoreCase) ? 1m : input.FxRateTryPerUnit!.Value;
        var saleTry = checked(input.SalePrice * rate);
        var netSale = input.VatIncludedInSale ? saleTry / (1m + input.VatRatePercent.Value / 100m) : saleTry;
        var commission = checked(saleTry * input.CommissionRatePercent.Value / 100m);
        var gross = saleTry - input.CostTry;
        var net = netSale - commission - input.EstimatedShipping.Value - input.TransactionCost.Value - input.CostTry;
        var margin = netSale == 0 ? 0 : net / netSale * 100m;
        var status = net <= 0 ? MoneyPriceStatus.BlockedNegativeMargin : MoneyPriceStatus.Ready;
        var netRounded = decimal.Round(net, 2, MidpointRounding.AwayFromZero);
        return new(input, decimal.Round(saleTry, 2, MidpointRounding.AwayFromZero), decimal.Round(gross, 2, MidpointRounding.AwayFromZero), netRounded, decimal.Round(margin, 2, MidpointRounding.AwayFromZero), status, $"{input.Channel.Trim().ToLowerInvariant()}/{input.Shop.Trim()}", now, null, Breakdown(input, rate, saleTry, netSale, commission, netRounded)); // #928
    }

    /// <summary>#928: the explanation -- every line rounded to the cent, the lines adding up to the rounded net exactly (a rounding line absorbs the cents when they need it), the net last.</summary>
    static IReadOnlyList<MoneyBreakdownLine> Breakdown(MoneyPriceInput input, decimal rate, decimal saleTry, decimal netSale, decimal commission, decimal netRounded)
    {
        var lines = new List<MoneyBreakdownLine>(); var sale = Cents(saleTry); var inv = System.Globalization.CultureInfo.InvariantCulture;
        var isTry = string.Equals(input.Currency, "TRY", StringComparison.OrdinalIgnoreCase);
        lines.Add(new("sale", "Satış fiyatı", sale, isTry ? $"Satış fiyatı: {Money(sale)} TRY" : $"Satış fiyatı: {Money(input.SalePrice)} {input.Currency.Trim().ToUpperInvariant()} × {rate.ToString("0.####", inv)} = {Money(sale)} TRY"));
        var vatWords = input.VatRatePercent!.Value.ToString("0.##", inv);
        if (input.VatIncludedInSale) { var vat = Cents(saleTry - netSale); lines.Add(new("vat", "KDV (satışa dahil)", -vat, $"KDV (%{vatWords}, satışa dahil): -{Money(vat)} TRY")); }
        else lines.Add(new("vat", "KDV", 0m, $"KDV (%{vatWords}): satış fiyatına dahil değil, düşülmedi"));
        lines.Add(new("commission", "Komisyon", -Cents(commission), $"Komisyon (%{input.CommissionRatePercent!.Value.ToString("0.##", inv)}): -{Money(Cents(commission))} TRY"));
        lines.Add(new("shipping", "Kargo", -Cents(input.EstimatedShipping!.Value), $"Kargo: -{Money(Cents(input.EstimatedShipping.Value))} TRY"));
        lines.Add(new("transaction", "İşlem/ödeme", -Cents(input.TransactionCost!.Value), $"İşlem/ödeme: -{Money(Cents(input.TransactionCost.Value))} TRY"));
        lines.Add(new("cost", "Maliyet", -Cents(input.CostTry), $"Maliyet: -{Money(Cents(input.CostTry))} TRY"));
        var difference = netRounded - lines.Sum(l => l.AmountTry);
        if (difference != 0m) lines.Add(new("rounding", "Yuvarlama farkı", difference, $"Yuvarlama farkı: {Money(difference)} TRY"));
        lines.Add(new("net", "Net kâr", netRounded, $"Net kâr: {Money(netRounded)} TRY"));
        return lines;
    }
    static decimal Cents(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
    static string Money(decimal value) => value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    static MoneyPriceResult Result(MoneyPriceInput? input, decimal saleTry, decimal gross, decimal net, MoneyPriceStatus status, IReadOnlyList<string>? missingFees = null) => new(input ?? new("", "", "", 0, 0, ""), saleTry, gross, net, 0, status, input is null ? "" : $"{input.Channel.Trim().ToLowerInvariant()}/{input.Shop.Trim()}", DateTimeOffset.UtcNow, missingFees); // #927
}

public static class PriceDispatchPreflight
{
    public static void EnsureReady(MoneyPriceResult result)
    {
        if (result.Status != MoneyPriceStatus.Ready)
            throw new InvalidOperationException($"Fiyat gönderimi engellendi: {result.Status}{(result.MissingFees is { Count: > 0 } missing ? " (eksik maliyet kalemleri: " + string.Join(", ", missing) + ")" : "")}; {result.ChannelShop}."); // #927: the missing fees are named
    }
}
