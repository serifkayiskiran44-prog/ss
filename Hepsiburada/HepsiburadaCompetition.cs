namespace TrMarketplaceHubDesktop.Hepsiburada;

public sealed record HepsiburadaPriceSuggestion(decimal? Price, bool AutomaticallyApplied, string Detail);

public static class HepsiburadaCompetition
{
    public static HepsiburadaPriceSuggestion Suggest(HepsiburadaBuybox row, decimal minimum, decimal maximumDecrease, decimal undercut)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (minimum <= 0 || maximumDecrease < 0 || undercut < 0 || Scale(minimum) > 2 || Scale(maximumDecrease) > 2 || Scale(undercut) > 2)
            return new(null, false, "Fiyat sınırları geçersiz.");
        if (row.WinningPrice is null or <= 0 || row.OwnPrice is null or <= 0)
            return new(null, false, "Buybox fiyatı eksik.");
        var floor = Math.Max(minimum, row.OwnPrice.Value - maximumDecrease);
        var target = row.Rank == 1 ? row.OwnPrice.Value : row.WinningPrice.Value - undercut;
        var proposed = decimal.Round(Math.Max(floor, target), 2, MidpointRounding.AwayFromZero);
        return new(proposed, false, "Korunan Buybox fiyat önerisi; otomatik gönderilmez.");
    }

    static int Scale(decimal value) => (decimal.GetBits(value)[3] >> 16) & 0x7F;
}
