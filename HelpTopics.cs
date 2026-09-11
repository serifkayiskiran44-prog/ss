namespace TrMarketplaceHubDesktop;

public sealed record HelpTopic(string Route, string Title, string Preconditions, string Recovery);

public static class HelpTopics
{
    public static IReadOnlyList<HelpTopic> All { get; } = Array.AsReadOnly(new[]
    {
        new HelpTopic("dashboard", "Ana sayfa", "Yerel veri klasörü erişilebilir olmalı.", "Durum satırındaki ilgili merkeze gidin."),
        new HelpTopic("products", "Ürün havuzu", "Ürün kaydı veya import önizlemesi.", "Stale uyarısında ürünü yenileyip tekrar önizleyin."),
        new HelpTopic("xml", "XML yönetimi", "Kaynak ve güvenli önizleme.", "Hatalı satırları düzeltip yeniden önizleyin."),
        new HelpTopic("excel", "Excel işlemleri", "Başlık eşleme ve geçerli satırlar.", "Hatalı satır raporunu dışa aktarın; apply yapmayın."),
        new HelpTopic("orders", "Sipariş ve stok", "Kayıtlı sipariş ve SKU eşleşmesi.", "Eksik SKU’yu düzeltmeden stok kararı uygulamayın."),
        new HelpTopic("sync", "Sync merkezi", "Önizleme ve açık retry onayı.", "Failed işi sınıfına göre düzeltin veya yeniden önizleyin."),
        new HelpTopic("connections", "Mağaza bağlantıları", "Şifreli credential store ve shop kimliği.", "NOT_CONFIGURED/LIVE_API_BLOCKED durumunda canlı çağrı yapılmaz."),
        new HelpTopic("settings", "Ayarlar", "Yerel ayar dosyaları.", "Bozuk workspace state güvenli varsayılana döner."),
        new HelpTopic("reports", "Raporlar ve destek", "Yerel audit/diagnostics.", "Secret içermeyen support package üretin.")
    });
    public static HelpTopic? ForRoute(string route) => All.FirstOrDefault(x => x.Route.Equals(route, StringComparison.OrdinalIgnoreCase));
}
