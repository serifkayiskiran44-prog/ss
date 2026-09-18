namespace TrMarketplaceHubDesktop.Trendyol;

public sealed record TrendyolPriceSuggestion(decimal? Price,string Reason);
public static class TrendyolCompetition
{
    public static TrendyolPriceSuggestion Suggest(TrendyolBuybox buybox,decimal minimum,decimal maximum,decimal difference)
    {
        if(minimum<=0||maximum<minimum||difference<0||difference>maximum||decimal.Round(minimum,2)!=minimum||decimal.Round(maximum,2)!=maximum||decimal.Round(difference,2)!=difference)return new(null,"Alt/üst sınır ve fark geçersiz; en fazla iki ondalık kullanın.");
        if(!buybox.HasMultipleSeller)return new(null,"Başka satıcı bilgisi yok; fiyat önerilmedi.");
        var competitor=buybox.Rank==1?buybox.SecondPrice:buybox.FirstPrice;
        if(competitor is null or <=0 || buybox.Rank is null or <=0)return new(null,"Sıra/rakip fiyatı eksik; fiyat önerilmedi.");
        var price=decimal.Round(Math.Clamp(competitor.Value-difference,minimum,maximum),2,MidpointRounding.AwayFromZero);
        return new(price,"API buybox fiyatına göre sınırlandırılmış öneri; sepet indirimi ve satıcı adı bilinmiyor. Otomatik gönderilmez.");
    }
}
