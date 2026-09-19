namespace TrMarketplaceHubDesktop;

public sealed record MarketplaceProductPanelRow(string Channel, string ShopId, string Status, string MappingId, string Capabilities, string Readiness, string LastError)
{
    public string Durum => MarketplaceStatusText.ToTurkish(Status);
    public string Kontrol => MarketplaceStatusText.ToTurkish(Readiness);
    public string Aciklama => MarketplaceStatusText.Explain(Status, Readiness, MappingId, LastError);
}

/// <summary>Teknik kodlar kayıtta değişmeden kalır; masaüstü ekranı Türkçe gösterir.</summary>
public static class MarketplaceStatusText
{
    static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["WORKING"] = "Hazırlık tamam", ["MAPPING_REQUIRED"] = "Ürün eşleştirmesi gerekli",
        ["LIVE_API_BLOCKED"] = "Canlı API henüz açık değil", ["NOT_SUPPORTED"] = "Desteklenmiyor",
        ["READY_READ_ONLY"] = "Salt okunur hazır", ["CREDENTIAL_TEST_REQUIRED"] = "Bağlantı testi gerekli",
        ["AUTH_ERROR"] = "Kimlik doğrulama sorunu", ["NETWORK_ERROR"] = "Ağ bağlantısı sorunu",
        ["TIMEOUT"] = "Bağlantı zaman aşımı", ["PARTIAL"] = "Kısmi hazır",
        ["MISSING"] = "İlan eşlemesi yok", ["STALE"] = "Eşleme güncel değil",
        ["ERROR"] = "İşlem hatası", ["PENDING"] = "İşlem bekliyor",
        ["SYNCED"] = "Eşitlendi", ["DRAFT"] = "Taslak", ["None"] = "İşlem yok",
        ["CONNECTED"] = "Bağlı", ["CONNECTED_READ_ONLY"] = "Salt okunur bağlı",
        ["NOT_CONFIGURED"] = "Ayarlar eksik", ["FAILED"] = "Bağlantı başarısız",
        ["NewListingCandidate"] = "Yeni ilan adayı", ["Active"] = "Etkin",
        ["Approved"] = "Onaylandı", ["Unmatched"] = "Eşleşmedi",
        ["Rejected"] = "Reddedildi", ["ReviewRequired"] = "İnceleme gerekli"
    };

    public static string ToTurkish(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : Labels.TryGetValue(value, out var label) ? label : value;

    public static string Explain(string status, string readiness, string mappingId, string lastError)
    {
        if (!string.IsNullOrWhiteSpace(lastError)) return "Son işlem notu: " + lastError;
        if (status.Equals("LIVE_API_BLOCKED", StringComparison.OrdinalIgnoreCase) || readiness.Equals("LIVE_API_BLOCKED", StringComparison.OrdinalIgnoreCase)) return "Bu kanal için canlı ürün, stok ve fiyat gönderimi açılmadan önce resmi API erişimi doğrulanmalı.";
        if (readiness.Equals("MAPPING_REQUIRED", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(mappingId)) return "Bu ürünü pazaryeri ilanına bağlamak için ürün / SKU / barkod eşleştirmesi oluşturun.";
        if (readiness.Equals("CREDENTIAL_TEST_REQUIRED", StringComparison.OrdinalIgnoreCase)) return "Bağlantı ayarlarında erişim anahtarını kaydedip bağlantı testini çalıştırın.";
        return "Yerel ürün planı hazır. Yayın ve senkronizasyon adımlarını bağlantı ekranından takip edebilirsiniz.";
    }
}

/// <summary>Builds marketplace product panels from shared connection, mapping, capability and health stores.</summary>
public static class MarketplaceProductPanelModel
{
    public static IReadOnlyList<MarketplaceProductPanelRow> Build(string productId, string? directory = null)
    {
        if (string.IsNullOrWhiteSpace(productId)) throw new ArgumentException("Ürün kimliği zorunlu.", nameof(productId));
        var mappings = new MarketplaceMappingStore(directory);
        var health = new ApiHealthStore(directory);
        return new MarketplaceConnectionStore(directory).List().Select(connection =>
        {
            var definition = MarketplaceConnectionCatalog.Get(connection.Channel);
            var mapping = mappings.Find(connection.Channel, connection.ShopId, productId);
            var api = health.Get(connection.Channel, connection.ShopId);
            var blocked = definition.LiveApiBlocked;
            var status = blocked ? "LIVE_API_BLOCKED" : !connection.Enabled ? "NOT_SUPPORTED" : api?.State is "AUTH_ERROR" or "NETWORK_ERROR" or "TIMEOUT" ? "PARTIAL" : "WORKING";
            var capabilities = definition.Capabilities.Enabled.Count == 0 ? "NOT_SUPPORTED" : string.Join(", ", definition.Capabilities.Enabled.OrderBy(x => x.ToString()));
            var readiness = blocked ? "LIVE_API_BLOCKED" : mapping is null ? "MAPPING_REQUIRED" : connection.Status == "CONNECTED_READ_ONLY" ? "READY_READ_ONLY" : "CREDENTIAL_TEST_REQUIRED";
            return new MarketplaceProductPanelRow(connection.Channel, connection.ShopId, status, mapping?.ExternalId ?? "", capabilities, readiness, api?.LastError ?? connection.LastError);
        }).ToArray();
    }
}
