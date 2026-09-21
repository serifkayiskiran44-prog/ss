namespace TrMarketplaceHubDesktop;

public sealed record HepsiburadaPriceSuggestion(decimal? Price,string Message);
public static class HepsiburadaCompetition
{
    public static HepsiburadaPriceSuggestion Suggest(decimal competitorPrice,decimal minimum,decimal maximum,decimal difference)
    {
        if(competitorPrice<=0||minimum<=0||maximum<minimum||difference<0)return new(null,"Fiyat ve sınırlar geçersiz.");
        var suggestion=decimal.Round(Math.Clamp(competitorPrice-difference,minimum,maximum),2,MidpointRounding.AwayFromZero);
        return new(suggestion,"Rakip fiyatından fark düşülerek alt/üst sınıra göre hesaplandı. Otomatik gönderilmez.");
    }
}
