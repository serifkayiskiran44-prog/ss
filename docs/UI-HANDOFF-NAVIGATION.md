# UI / navigasyon devir notu — 2026-11-17

Kapsam: Tasarım işi ayrı yürütülüyor. Bu not yalnız kodda **var olan** ama ana menüye bağlı olmayan parçaları, rota sözleşmesini ve testlerin bağlı olduğu adları kaydeder. Mantık/veri tarafı bu notla değişmedi; arayüz dosyalarında yeniden düzenleme yapılmadı.

## 1. Şimdiki ana menü (`MainWindow.Navigation.cs`, birleşik dal)

| Grup | Rotalar |
|---|---|
| ÇALIŞMA ALANI | `products`, `categories`, `brands`, `xml`, `excel`, `orders` |
| ENTEGRASYON | `marketplaces` (Trendyol/Etsy bağlantıları `marketplaces` içinde açılır), `bizimhesap`, `nes` |
| YÖNETİM | `connections`, `settings` |

- Açılış rotası: kayıtlı `last-route`, yoksa `products`.
- Tüm sayfalar `BuildNavigation` içinde açılışta **hemen** kurulur (`Page(key,title,description,UIElement)`). Yeni sayfalar eklenirse tembel kurulum (ilk seçimde fabrika çağrısı) açılış süresini korur.
- `SelectRoute` bilinmeyen rotayı sessizce yok sayar (hata vermez): panellerin `navigate("...")` çağrıları karşılıksız rotada hiçbir şey yapmaz.

## 2. Kodda hazır ama menüye bağlı olmayan ekranlar

Tarihçe: `1233d31` (2026-09-17, "simplify MonoBridge workspace navigation") menüyü yaklaşık 35 sayfadan 5'e indirdi; `9d6c01f` (2026-09-18) kategori/marka ve Trendyol çalışma alanlarını getirirken Genel bakış de menüden çıktı. Eski tam menü: `git show 1233d31^:MainWindow.Navigation.cs`. Aşağıdakilerin hiçbirine uygulamanın başka yerinden referans yok (panel olarak testlerde de yok; arkadaki servislerin test kapsamı değişken, bağlamadan önce ilgili servis testlerine bakın).

| Önerilen rota | Fabrika | Ne gösterir | Not |
|---|---|---|---|
| `dashboard` | `DashboardPanel.Create(dir, navigate)` | ürün/stok/sipariş/XML/sync kartları, mağaza sağlığı, bildirimler, 14 günlük özet | yerel okuma, arka planda yüklenir |
| `bulk-products` | `BulkProductsPanel.Create(dir, navigate)` | toplu ürün güncelleme (önizleme + sürüm kontrolü + onay) | |
| `media` | `MediaPanel.Create(dir, navigate)` | ürün görselleri | görsel önizleme için tek HTTP GET |
| `listing-matrix` | `ChannelListingMatrixPanel.Create(dir, navigate)` | ürün × hesap bağlantı matrisi | `ProductChannelBindingStore` okur |
| `data-quality` | `DataQualityPanel.Create(dir, navigate)` | veri kalite merkezi | Genel bakış bildirimi bu rotaya yönlenir |
| `order-exceptions` | `OrderExceptionsPanel.Create(dir, navigate)` | sipariş istisnaları | |
| `messages` | `MessagePanel.Create(dir, navigate)` | yerel mesaj merkezi | canlı gelen kutusu `LIVE_API_BLOCKED` |
| `api-health` | `ApiHealthPanel.Create(dir, navigate)` | bağlantı sağlığı, kota, son hata | |
| `diagnostics` | `DiagnosticsPanel.Create(dir, navigate)` | tanılama, audit, destek paketi | |
| `readiness` | `ProductionReadinessPanel.Create(dir)` | üretim hazırlığı raporu | |
| `migration` | `MigrationAssistantPanel.Create(dir)` | Excel/CSV/JSON/XML veri taşıma | |
| `policy-center` | `PolicyCenterPanel.Create(dir)` | stok/fiyat politika merkezi | hesap ayarlarındaki "Ürün kuralları" ile örtüşebilir |
| `locale-settings` | `LocaleSettingsPanel.Create(dir)` | para birimi, KDV, sayı/tarih kültürü | |
| `sync` | `MainWindow.BuildSync()` | sync işleri, XML geçmişi, otomasyon işleri | `MainWindow` örnek metodu |
| `automation` | `MainWindow.BuildAutomation()` | otomasyon takvimi ve şablonları | aynı |
| `price-policies`, `stock-policies` | `MainWindow.BuildPricePolicies()`, `BuildStockPolicies()` | mağaza fiyat/stok kuralları | politika merkeziyle örtüşebilir |
| `shipping` | `NavlungoPanel.Create()` | Navlungo kargo bağlantısı | **şu an ayarlanabileceği hiçbir ekran yok** |
| — | `EtsyReadinessPanel.Create`, `MarketplaceSetupPanel`, `JoomPanel`, `WishPanel`, `FruugoPanel` | eski kanal sayfaları | çok mağazalı hesap çalışma alanıyla yer değiştirmiş olabilir |

## 3. Rota sözleşmesi (`ScreenParityAudit`)

`RequiredRoutes`: `dashboard, products, orders, xml, excel, taxonomy, sync, automation, connections, api-health, messages, settings, reports`. Açılışta eksik olanlar `operations.log` içine "Ekran paritesi BLOCKED: …" olarak yazılır; şu an eksik: `api-health, automation, dashboard, messages, reports, sync, taxonomy`.

- `taxonomy`: artık `categories` + `brands` olarak iki rota; sözleşme güncellenmeli ya da takma ad eklenmeli.
- `reports`: hiçbir zaman ayrı panel olmadı (Genel bakış içindeki "Son 14 gün yerel raporları" bölümü); sözleşmeden çıkarılmalı ya da `dashboard` takma adı olmalı.
- Panellerin çıkardığı rotalar: `products, orders, connections, xml, sync, listing-matrix, data-quality, api-health`.
- Sözleşmeyi değiştirirsen `SafetyAndStabilityTests.ScreenParityAuditReportsMissingRouteAndPassesCompleteContract` kendi kendini doğrular (liste değişince geçmeye devam eder).

## 4. Ayarlar sayfası ve kısayollar

Şu an: "BizimHesap bağlantı ayarları", "NES e‑Fatura ayarları", "Mağaza bağlantılarını yönet", (tepsi açıkken) "Pencereyi kapatınca bildirim alanında çalıştır", yedek/taşıma paneli. Eski menüde her kanal için ayrı "… bağlantı ayarları" kısayolu ve Genel bakışta Kategoriler/Excel/Raporlar/Mesajlar/Ayarlar hızlı erişim kartları vardı (`1233d31` ile kalktı).

## 5. Bu turda arayüz dosyalarında yapılan mantık kaynaklı değişiklikler

- `MarketplaceShopProductsPanel.cs`: hesap ürün listesi varsayılan süzgeci `Bound` (yalnız bağlı ürünler + o hesabın "yeni ürün adayı" kayıtları); süzgeç kutusu `Bağlı ürünler / Sorunsuz / Hatalı`; yeni düğme `MarketplaceDropCandidates` ("Adaylıktan çıkar", yalnız yerel kayıt). Görünümü değiştirebilirsin; adlar ve davranış testlerde.
- `OrderCardWindow.Build(...)`: pencereyi göstermeden kurar (testler için); `OrderApplyStockButton` fiziksel satış ve incelemeli siparişte kapalı.
- `OrdersPanel.cs`: iki yenileme düğmesi var ("Tüm mağazalardan yenile" ve "Trendyol'dan yenile"); sadeleştirme tasarım kararı.

## 6. Testlerin bağlı olduğu kontrol adları (değiştirirsen testi birlikte güncelle)

`EtsyAccountProductSections, EtsyCreationHandoffButton, EtsyPreview_Stock, EtsyProducts, EtsySend, HepsiburadaCreateDispatchPreview, HepsiburadaPreviewOperation, HepsiburadaReadProducts, InventoryLocationsButton, InventoryTransferApplyButton, InventoryTransferFrom, InventoryTransferPreviewButton, InventoryTransferTo, InvoiceSelectedOrder, ManualStoreSaleButton, MarketplaceAccountCard_*, MarketplaceAccountChooser, MarketplaceAccountChooserToggle, MarketplaceAccountHeading, MarketplaceAccountSwitcher, MarketplaceAccountTabs, MarketplaceAccountWorkspace, MarketplaceAddStore, MarketplaceBulk_Category, MarketplaceConnectionList, MarketplaceDefaultCategory, MarketplaceDropCandidates, MarketplaceNewChannel, MarketplaceOpenProductCard, MarketplaceOperation_*, MarketplaceOrderConnectionFilter, MarketplaceOrderReviewFilter, MarketplaceOrderSourceFilter, MarketplaceOrderStatusFilter, MarketplaceSaveShopSettings, MarketplaceSaveStore, MarketplaceSelectAllFiltered, MarketplaceShopProducts, MarketplaceShopSettingsSections, MarketplaceShowAccounts, MarketplaceStoreEditor, NavigationList, OrderApplyStockButton, OrderColumns, PageHeader, ProductBindingFlagsApplyButton, ProductConnectSelectedButton, ProductConnectionsButton, ProductManageContentEdit, ProductManagePriceEdit, ProductManageStockEdit, ProductSourceButton, ProductSourceFilter, ProductSourceGroup, ProductSummaryBar, RefreshNesInvoiceNumbers, RefreshTrendyolOrders, SharedBrand, TrendyolAccountProductSections, TrendyolBuildPreview, TrendyolOperationMode, TrendyolProducts, TrendyolSafetyCoverage, TrendyolSend, Map_*`

Ayrıca bazı testler görünür metinle arar: "Bağlantıyı ve yönetimi güncelle", "Seçili ürünleri bağla / eşleştir", "Bu hesap için mağaza kuralları etkin", "Fiyatı varsayılan olarak yönet", "Mağazayı aç", "Sipariş okumayı zamanla", "Ürün okumayı zamanla", "Hesap kuralları", "Sipariş kuralları".

## 7. Ekranı bekleyen hazır servis: siteye XML yayınlama

`SiteXmlPublisher` (kök dizin) hazır ve testli, ekranı yok. Akış: `Preview(siteKey, fileName, xmlPath)` → `SiteXmlPublishPreview` (hedef adres, SHA-256, Model sayısı, boyut gösterilir) → kullanıcı açıkça onaylar → `PublishAsync(preview, approved: true)` → `SiteXmlPublishReceipt`. `IsConfigured` anahtarın kayıtlı olup olmadığını söyler (değeri asla vermez). Hatalar `SiteXmlPublishException` (doğrulama farkı için `SiteXmlPublishVerificationException`) olarak gelir ve iletilebilir; belirteç içermez. Zamanlayıcıya veya tepsiye bağlamayın.
