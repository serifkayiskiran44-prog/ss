namespace TrMarketplaceHubDesktop;

public sealed record ShippingCostBracket(string Carrier, decimal DesiFrom, decimal DesiTo, decimal Cost, string Currency);

public sealed class ShippingCostTable
{
    readonly List<ShippingCostBracket> brackets = new();
    public IReadOnlyList<ShippingCostBracket> Brackets => brackets.ToArray();

    public void Add(ShippingCostBracket bracket)
    {
        ArgumentNullException.ThrowIfNull(bracket);
        if (string.IsNullOrWhiteSpace(bracket.Carrier)) throw new ArgumentException("Kargo firması gerekli.");
        if (bracket.DesiFrom < 0 || bracket.DesiTo <= bracket.DesiFrom) throw new ArgumentException("Desi aralığı geçersiz (üst sınır alt sınırdan büyük olmalı).");
        if (bracket.Cost < 0) throw new ArgumentException("Kargo maliyeti negatif olamaz.");
        if (string.IsNullOrWhiteSpace(bracket.Currency) || bracket.Currency.Length != 3) throw new ArgumentException("Kargo maliyeti 3 haneli para birimi kodu gerektirir.");
        if (brackets.Any(b => b.Carrier.Equals(bracket.Carrier, StringComparison.OrdinalIgnoreCase) && bracket.DesiFrom < b.DesiTo && bracket.DesiTo > b.DesiFrom))
            throw new ArgumentException($"'{bracket.Carrier}' için {bracket.DesiFrom}-{bracket.DesiTo} desi aralığı mevcut bir aralıkla çakışıyor.");
        brackets.Add(bracket);
    }

    public bool Remove(string carrier, decimal desiFrom, decimal desiTo)
        => brackets.RemoveAll(b => b.Carrier.Equals(carrier, StringComparison.OrdinalIgnoreCase) && b.DesiFrom == desiFrom && b.DesiTo == desiTo) > 0;

    // Half-open [DesiFrom, DesiTo) bracket match; null when the carrier has no bracket covering this desi.
    public ShippingCostBracket? Resolve(string carrier, decimal desi)
        => brackets.FirstOrDefault(b => b.Carrier.Equals(carrier, StringComparison.OrdinalIgnoreCase) && desi >= b.DesiFrom && desi < b.DesiTo);
}
