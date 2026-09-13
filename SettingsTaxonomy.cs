namespace TrMarketplaceHubDesktop;

/// <param name="Route">The workspace that owns the setting; the settings shell links there, it never re-implements it.</param>
/// <param name="Section">An optional section inside the route (a tab, a card) the owner can select.</param>
/// <param name="SecretBearing">The page edits credentials: it uses its own masked controls and the shell only links to it.</param>
/// <param name="Inline">The setting is part of the settings shell itself and is hosted inline under its category.</param>
public sealed record SettingsEntry(string Key, string Label, string Description, string Route, string Section = "", bool SecretBearing = false, bool Inline = false);

public sealed record SettingsCategory(string Key, string Label, string Description, IReadOnlyList<SettingsEntry> Entries);

/// <summary>
/// The settings taxonomy (#853): one navigation tree over the settings that already exist -- general, store, connections,
/// import, pricing, notifications, diagnostics -- each entry pointing at the workspace that owns it (or hosted inline
/// when the settings shell itself owns it). No parallel settings framework: a category is a list of links and inline
/// hosts, and an entry whose route the shell does not register is dropped rather than shown as a dead promise.
/// Secret-bearing pages are marked so the shell can say "masked controls, edited on its own page" and never inline them.
/// </summary>
public static class SettingsTaxonomy
{
    public const string RoutePrefix = "settings/";
    public const int MaxLabelLength = 48;

    public static IReadOnlyList<SettingsCategory> Categories { get; } = new SettingsCategory[]
    {
        new("general", "Genel", "Para birimi, vergi, yerel kültür, yedek ve yerel çalışma bilgileri", new SettingsEntry[]
        {
            new("locale", "Döviz / vergi / yerel ayarlar", "Para birimi, KDV, sayı-tarih kültürü ve mağaza kopyalama", "locale-settings"),
            new("backup", "Sürüm, yedek ve taşıma", "Yerel veri yedeği alma ve geri yükleme", "settings", Inline: true),
            new("local-data", "Yerel veri ve otomasyon bilgisi", "XML kaynakları ve ürün kilitleri ilgili ekranlardan düzenlenir; zamanlı yenileme yalnız uygulama açıkken çalışır", "settings", Inline: true),
            new("migration", "Veri geçiş asistanı", "Eski dışa aktarımları güvenli önizleme ve geri alma günlüğüyle taşıma", "migration"),
        }),
        new("store", "Mağaza", "Stok kuralları, politika merkezi, sözlükler ve zamanlayıcılar", new SettingsEntry[]
        {
            new("stock-policies", "Mağaza stok ayarları", "Güvenlik stoğu, üst sınır ve yerel önizleme", "stock-policies"),
            new("policy-center", "Stok / fiyat politika merkezi", "Kanal + mağaza politikaları, kopyalama ve kişisel veri politikası", "policy-center"),
            new("taxonomy", "Kategori / marka / özellik", "Yerel sözlük kayıtları ve harici anahtar eşlemeleri", "taxonomy"),
            new("automation", "Otomasyon", "Kanal ve mağaza bazlı stok/fiyat zamanlayıcıları", "automation"),
        }),
        new("connections", "Bağlantılar", "Mağaza bağlantıları, API sağlığı ve kanal erişim bilgileri", new SettingsEntry[]
        {
            new("connections", "Mağaza bağlantıları", "Tüm kanal ve mağaza kayıtları, yetenekler ve salt okunur bağlantı testleri", "connections"),
            new("api-health", "API bağlantı sağlığı", "Auth, erişilebilirlik, rate-limit, kota ve son hata durumu", "api-health"),
            new("etsy-connection", "Etsy bağlantı ayarları", "Hesap yetkilendirmesi ve mağaza bağlantısı", "etsy", "connection", SecretBearing: true),
            new("ebay-connection", "eBay bağlantı ayarları", "API erişim bilgileri", "ebay", "connection", SecretBearing: true),
            new("ozon-connection", "Ozon bağlantı ayarları", "API erişim bilgileri", "ozon", "connection", SecretBearing: true),
            new("joom-connection", "Joom bağlantı ayarları", "Satıcı kaydı ve erişim bilgileri", "joom", "connection", SecretBearing: true),
            new("amazon-connection", "Amazon bağlantı ayarları", "SP-API ayarları", "amazon", "connection", SecretBearing: true),
            new("trendyol-connection", "Trendyol bağlantı ayarları", "Satıcı API ayarları", "trendyol", "connection", SecretBearing: true),
            new("hepsiburada-connection", "Hepsiburada bağlantı ayarları", "Merchant API ayarları", "hepsiburada", "connection", SecretBearing: true),
            new("fruugo-connection", "Fruugo bağlantı ayarları", "Retailer ayarları", "fruugo", "connection", SecretBearing: true),
            new("allegro-connection", "Allegro bağlantı ayarları", "Public API erişimi", "allegro", "connection", SecretBearing: true),
            new("wish-connection", "Wish bağlantı ayarları", "Merchant ayarları", "wish", "connection", SecretBearing: true),
            new("channels", "Diğer pazaryerleri", "Hesap başvuruları ve entegrasyon gereksinimleri", "channels"),
            new("shipping", "Kargo bağlantısı (Navlungo)", "Kargo bağlantısı ve mevcut hizmet işlemleri", "shipping", SecretBearing: true),
        }),
        new("import", "İçe aktarma", "XML kaynakları, Excel profilleri ve pazaryeri görselleri", new SettingsEntry[]
        {
            new("xml", "XML kaynakları", "Kaynak bağlantısı, alan eşleştirme, fiyat/stok kuralları ve önizleme", "xml"),
            new("excel", "Excel profilleri", "Excel içe/dışa aktarma profilleri ve önizleme", "excel"),
            new("images", "Pazaryeri görselleri", "Kanal görsel gereksinimleri", "settings", Inline: true),
        }),
        new("pricing", "Fiyatlandırma", "Fiyat kuralları ve kur", new SettingsEntry[]
        {
            new("price-policies", "Mağaza fiyat kuralları", "CASE formülü, kur ve güvenli fiyat önizlemesi", "price-policies"),
            new("pricing-locale", "Kur ve para birimi", "Döviz kurları ve varsayılan para birimi (yerel ayarlar)", "locale-settings"),
        }),
        new("notifications", "Bildirimler", "Bildirim merkezi ve mesajlar", new SettingsEntry[]
        {
            new("alerts", "Hata / bildirim merkezi", "Açık, onaylanmış ve çözülmüş uyarılar (genel bakışta)", "dashboard"),
            new("messages", "Mesaj merkezi", "Müşteri mesajları, sistem bildirimleri ve yerel yanıt şablonları", "messages"),
        }),
        new("diagnostics", "Tanılama", "Sağlık, audit, hazırlık ve raporlar", new SettingsEntry[]
        {
            new("diagnostics", "Tanılama / audit", "Güvenli sistem sağlık özeti, audit trail ve destek paketi", "diagnostics"),
            new("readiness", "Üretim hazırlığı", "Yerel veri, secret güvenliği, connector capability ve API sağlık geçidi", "readiness"),
            new("reports", "Rapor kataloğu", "Raporlar, son çalıştırmalar ve çıktılar", "reports"),
        }),
    };

    public static SettingsCategory? Find(string? key) => Categories.FirstOrDefault(c => string.Equals(c.Key, (key ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The category and entry for an entry key, or null.</summary>
    public static (SettingsCategory Category, SettingsEntry Entry)? FindEntry(string? key)
    {
        var k = (key ?? "").Trim();
        foreach (var category in Categories) foreach (var entry in category.Entries) if (string.Equals(entry.Key, k, StringComparison.OrdinalIgnoreCase)) return (category, entry);
        return null;
    }

    /// <summary>The taxonomy as this shell can honour it: entries whose route is registered (inline ones always), categories that still have entries.</summary>
    public static IReadOnlyList<SettingsCategory> Visible(Func<string, bool> routeExists)
    {
        ArgumentNullException.ThrowIfNull(routeExists);
        return Categories.Select(c => c with { Entries = c.Entries.Where(e => e.Inline || routeExists(e.Route)).ToList() }).Where(c => c.Entries.Count > 0).ToList();
    }

    /// <summary>"settings/pricing" → "pricing"; "settings" or anything else → null.</summary>
    public static string? ParseDeepLink(string? route)
    {
        var r = (route ?? "").Trim();
        if (!r.StartsWith(RoutePrefix, StringComparison.OrdinalIgnoreCase)) return null;
        var key = r[RoutePrefix.Length..].Trim().TrimEnd('/');
        return Find(key) is null ? null : Find(key)!.Key;
    }

    public static string DeepLink(string categoryKey) => RoutePrefix + (categoryKey ?? "").Trim().ToLowerInvariant();

    public static IReadOnlyList<(SettingsCategory Category, SettingsEntry Entry)> Search(string? query, Func<string, bool>? routeExists = null)
    {
        var q = (query ?? "").Trim(); if (q.Length == 0) return Array.Empty<(SettingsCategory, SettingsEntry)>();
        return Visible(routeExists ?? (_ => true)).SelectMany(c => c.Entries.Select(e => (c, e))).Where(x => $"{x.c.Label} {x.e.Label} {x.e.Description}".Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
    }

    /// <summary>A label fit for a narrow tree: whole when short, otherwise trimmed with an ellipsis (the full label stays in the tooltip and the accessible name).</summary>
    public static string ShortLabel(string label) => label.Length <= MaxLabelLength ? label : label[..(MaxLabelLength - 1)].TrimEnd() + "…";
}
