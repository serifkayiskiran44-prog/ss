namespace TrMarketplaceHubDesktop;

public sealed record OrderTotals(decimal Net,decimal Vat,decimal Gross,decimal Discount,decimal Commission,decimal CalculatedGross,decimal SourceDifference)
{
    public bool HasMismatch=>Math.Abs(SourceDifference)>=0.01m;
}

public static class OrderTotalsCalculator
{
    public static OrderTotals Calculate(OrderSnapshot order)
    {
        ArgumentNullException.ThrowIfNull(order);
        decimal calculatedNet=0,calculatedVat=0,calculatedGross=0;
        foreach(var item in order.Items)
        {
            var quantity=Math.Max(0,item.Quantity);
            var rate=Math.Max(0,item.VatRate);
            var grossUnit=item.UnitPrice??(item.NetUnitPrice.HasValue?Round(item.NetUnitPrice.Value*(1+rate/100)):0);
            var netUnit=item.NetUnitPrice??(rate==0?grossUnit:Round(grossUnit/(1+rate/100)));
            var lineNet=Round(netUnit*quantity);
            var lineGross=Round(grossUnit*quantity);
            calculatedNet+=lineNet;calculatedGross+=lineGross;calculatedVat+=lineGross-lineNet;
        }
        calculatedNet=Round(calculatedNet);calculatedVat=Round(calculatedVat);calculatedGross=Round(calculatedGross);
        var net=Round(order.NetTotal??calculatedNet);
        var vat=Round(order.VatTotal??(order.NetTotal.HasValue&&order.Total.HasValue?order.Total.Value-order.NetTotal.Value:calculatedVat));
        var gross=Round(order.Total??calculatedGross);
        return new(net,vat,gross,Round(order.DiscountTotal??0),Round(order.CommissionTotal??0),calculatedGross,Round(gross-(net+vat)));
    }
    static decimal Round(decimal value)=>Math.Round(value,2,MidpointRounding.AwayFromZero);
}
