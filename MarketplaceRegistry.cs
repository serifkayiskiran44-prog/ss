namespace TrMarketplaceHubDesktop;

public sealed record MarketplaceSetup(string Id, string Name, string RegistrationUrl, string DocumentationUrl, string Prerequisites)
{
    public string Status => "Bağlı değil — API entegrasyonu tamamlanmadı";
}

public static class MarketplaceRegistry
{
    public static IReadOnlyList<MarketplaceSetup> All { get; } = Array.AsReadOnly(new[]
    {
        new MarketplaceSetup("ebay", "eBay", "https://www.ebay.com/sellercenter", "https://developer.ebay.com/develop/guides/sell/authorization", "Satıcı hesabı, geliştirici uygulaması, production anahtarları ve satıcı OAuth onayı gerekiyor."),
        new MarketplaceSetup("wish", "Wish", "https://merchant.wish.com/", "https://merchant.wish.com/documentation/api/v3/oauth", "Satıcı hesabı, private app kaydı, dönüş adresi ve OAuth onayı gerekiyor."),
        new MarketplaceSetup("ozon", "Ozon", "https://seller.ozon.com/tr/", "https://docs.ozon.ru/api/seller/", "Satıcı hesabı ve Seller API erişimi gerekiyor. Güncel kimlik doğrulama sözleşmesi resmî panelden doğrulanmalı."),
        new MarketplaceSetup("joom", "Joom", "https://merchant.joom.com/", "https://merchant.joom.com/docs/api", "Satıcı kabulü ve API uygulama erişimi gerekiyor. Güncel yetkilendirme sözleşmesi resmî panelden doğrulanmalı."),
        new MarketplaceSetup("allegro", "Allegro", "https://allegro.pl/rejestracja/konto-firmowe/nowe-konto", "https://developer.allegro.pl/tutorials/uwierzytelnianie-i-autoryzacja-zlq9e75GdIR", "Aktif hesap, iki aşamalı giriş, geliştirici uygulaması ve satıcı OAuth onayı gerekiyor."),
        new MarketplaceSetup("fruugo", "Fruugo", "https://sell.fruugo.com/verification/register", "https://developer.fruugo.com/", "Retailer kabulü ve API erişimi gerekiyor. Order API Basic auth kullanır; ürün API sözleşmesi ayrıca doğrulanmalı.")
    });
}

