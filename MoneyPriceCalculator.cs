using System;

public enum MoneyPriceStatus { Ready, BlockedNegativeMargin, BlockedMissingInput, BlockedStaleFx, BlockedInvalid }

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
}

public sealed record MoneyPriceResult(
    MoneyPriceInput Input,
    decimal SalePriceTry,
    decimal GrossContribution,
    decimal NetContribution,
    decimal MarginPercent,
    MoneyPriceStatus Status,
    string ChannelShop,
    DateTimeOffset CalculatedAtUtc)
{
    public bool IsApproximate => Input.CommissionRatePercent is null || Input.EstimatedShipping is null || Input.TransactionCost is null || Input.VatRatePercent is null;
}

public static class MoneyPriceCalculator
{
    public static MoneyPriceResult Calculate(MoneyPriceInput input)
    {
        if (input is null || string.IsNullOrWhiteSpace(input.Sku) || string.IsNullOrWhiteSpace(input.Channel) || string.IsNullOrWhiteSpace(input.Shop) || input.SalePrice <= 0 || input.CostTry < 0)
            return Result(input, 0, 0, 0, MoneyPriceStatus.BlockedInvalid);
        if (!string.Equals(input.Currency, "TRY", StringComparison.OrdinalIgnoreCase) && (input.FxRateTryPerUnit is null || input.FxRateTryPerUnit <= 0))
            return Result(input, 0, 0, 0, MoneyPriceStatus.BlockedMissingInput);
        if (input.FxSnapshotUtc is null)
            return Result(input, 0, 0, 0, MoneyPriceStatus.BlockedMissingInput);
        var now = input.AsOfUtc ?? DateTimeOffset.UtcNow;
        if (now - input.FxSnapshotUtc.Value > TimeSpan.FromHours(24))
            return Result(input, 0, 0, 0, MoneyPriceStatus.BlockedStaleFx);
        if (input.CommissionRatePercent is null || input.EstimatedShipping is null || input.TransactionCost is null || input.VatRatePercent is null)
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
        return new(input, decimal.Round(saleTry, 2, MidpointRounding.AwayFromZero), decimal.Round(gross, 2, MidpointRounding.AwayFromZero), decimal.Round(net, 2, MidpointRounding.AwayFromZero), decimal.Round(margin, 2, MidpointRounding.AwayFromZero), status, $"{input.Channel.Trim().ToLowerInvariant()}/{input.Shop.Trim()}", now);
    }

    static MoneyPriceResult Result(MoneyPriceInput? input, decimal saleTry, decimal gross, decimal net, MoneyPriceStatus status) => new(input ?? new("", "", "", 0, 0, ""), saleTry, gross, net, 0, status, input is null ? "" : $"{input.Channel.Trim().ToLowerInvariant()}/{input.Shop.Trim()}", DateTimeOffset.UtcNow);
}

public static class PriceDispatchPreflight
{
    public static void EnsureReady(MoneyPriceResult result)
    {
        if (result.Status != MoneyPriceStatus.Ready)
            throw new InvalidOperationException($"Fiyat gönderimi engellendi: {result.Status}; {result.ChannelShop}.");
    }
}
