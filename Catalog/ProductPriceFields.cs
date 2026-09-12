namespace TrMarketplaceHubDesktop.Catalog;

public partial class CatalogStore
{
    public CatalogProduct AddPriceField(string productId, CatalogPriceField field)
    {
        ValidatePriceField(field);
        var product = FindProduct(productId) ?? throw new InvalidOperationException("Ürün bulunamadı.");
        if (product.PriceFields.Any(f => f.Name.Equals(field.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"'{field.Name}' adlı fiyat alanı zaten tanımlı; aynı isim iki kez eklenemez.");
        product.PriceFields.Add(field);
        SaveProduct(product);
        return product;
    }

    public CatalogProduct UpdatePriceField(string productId, CatalogPriceField field)
    {
        ValidatePriceField(field);
        var product = FindProduct(productId) ?? throw new InvalidOperationException("Ürün bulunamadı.");
        var index = product.PriceFields.FindIndex(f => f.Name.Equals(field.Name, StringComparison.OrdinalIgnoreCase));
        if (index < 0) throw new InvalidOperationException($"'{field.Name}' adlı fiyat alanı bulunamadı; önce ekleyin.");
        product.PriceFields[index] = field;
        SaveProduct(product);
        return product;
    }

    public CatalogProduct RemovePriceField(string productId, string name)
    {
        var product = FindProduct(productId) ?? throw new InvalidOperationException("Ürün bulunamadı.");
        var removed = product.PriceFields.RemoveAll(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) throw new InvalidOperationException($"'{name}' adlı fiyat alanı bulunamadı.");
        SaveProduct(product);
        return product;
    }

    static void ValidatePriceField(CatalogPriceField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (string.IsNullOrWhiteSpace(field.Name) || field.Name.Length > 64) throw new ArgumentException("Fiyat alanı adı 1-64 karakter olmalı.");
        if (field.Value < 0) throw new ArgumentException("Fiyat alanı değeri negatif olamaz.");
        if (string.IsNullOrWhiteSpace(field.Currency) || field.Currency.Length != 3) throw new ArgumentException("Fiyat alanı 3 haneli para birimi kodu gerektirir.");
    }
}
