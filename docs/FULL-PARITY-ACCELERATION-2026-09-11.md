# Full parity acceleration — Entegra + Etsy + dropshipping

Tarih: 2026-09-11

## Amaç
MonoBridge Desktop'ı yalnız connector bulunan bir WPF uygulaması olmaktan çıkarıp dropshipping ağırlıklı, Entegra sınıfı kapsamlı entegrasyon ve günlük operasyon merkezine dönüştürmek.

## Kapsam değişikliği
Kullanıcı önceki yedi DEFERRED alanı tekrar kapsam içine aldı. Aşağıdakiler artık `IN_SCOPE`:
- varyant/seçenek sistemi
- bundle/paket/set
- hızlı satır içi düzenleme
- kritik fiyat
- XML varyant mapping
- fulfillment core
- hakediş/mutabakat core

Uygulama login/password sistemi ayrıca istenmediği için eklenmez. Güvenlik sınırları kalkmaz: secret repo/log/issue içine yazılmaz, doğrulanmamış API uydurulmaz, gerçek marketplace write otomatik test edilmez, canlı write preview + açık onay + stale/idempotency + wrong-shop guard ister.

## Mevcut repo kod envanteri ve gözlenen açıklıklar
Ana dalda ürün, XML/Excel, taxonomy, stok/fiyat policy, sipariş, sync/automation, credential store ve bazı connector iskeletleri var. Etsy tarafında `EtsyConnector.cs`, `EtsyOAuth.cs`, `EtsyShopClient.cs`, `EtsyDrafts.cs`, `OrdersEtsyClient.cs` mevcut. Kod şu anda mağaza testi, PKCE OAuth, listing listesi, basit listing stok/fiyat patch'i, draft oluşturma + ilk görsel, receipt/order okuma gibi temel akışları içeriyor.

Önemli Etsy gap'leri:
1. Listing'in tüm alanlarını tam okuma/update etme ve lifecycle state yönetimi ortak bir service olarak tamamlanmalı.
2. Inventory ayrı model olmalı; SKU/property_values/offerings/price_on_property/quantity_on_property/sku_on_property desteklenmeli.
3. Etsy 2026 üçüncü varyant desteği nedeniyle inventory reader/writer 3 property'yi güvenle işlemeli.
4. Inventory write tam replacement semantiğine sahip olduğu için önce read-current → merge → preview → stale guard → full write yapılmalı.
5. Batch inventory ve batch shipping okuma endpointleri kullanılmalı; eski includes=Inventory/Shipping varsayımlarına dayanılmamalı.
6. Shipping profiles, return policies, shop sections, seller taxonomy/properties ve processing/readiness profiles UI'da seçilebilir/cache'lenebilir olmalı.
7. Digital listing/file ve personalization gibi fiziksel ürün dışı türler capability-driven ayrılmalı.
8. Çoklu image sıralama/silme/yenileme ve video/file varlık yönetimi eksik.
9. Listing ↔ local product eşleme, drift/reconciliation ve remote-vs-local diff ekranı eksik.
10. OAuth scope/readiness/refresh durumunu tek production readiness ekranında göstermek gerekiyor.

## GitHub açık kaynak Etsy karşılaştırması
Davranış ve API kapsamı referansı olarak public projeler incelenir; kod kör kopyalanmaz.

### DColl/etsy-mcp-server
README kapsamı: shop, listings CRUD/publish/delete, images, digital files, inventory, sections, taxonomy ve orders. `inventory.ts` full inventory replacement yapısını açıkça ele alıyor ve mevcut inventory'nin önce okunmasını öneriyor. Bizim projede bu davranış stale/idempotency/wrong-shop guard ile daha sıkı uygulanmalı.

### avlihachev/etsy-mcp-server
2026 API değişikliklerine göre shared secret header formatı, physical listing için `readiness_state_id`, shipping profile, return policy ve processing profile akışlarını güncel tutuyor. Bu alanlar bizim Etsy UI/readiness katmanına alınmalı.

### profplum700/etsy-mcp-server ve mevcut ETSY-HAZIR-KOD-ARASTIRMA
PKCE, refresh token, listings, receipts, taxonomy ve shop sections referansı var; ancak bazı eski implementasyonlar eksik/placeholder. Davranış karşılaştırması yapılmalı, lisans/versiyon kontrol edilmeden kaynak kodu alınmamalı.

## Etsy resmi API 2026 kritik noktaları
- V3 requestlerde `x-api-key` keystring + shared secret biçimi ve yetkili isteklerde OAuth Bearer kullanılır.
- Physical listing create/update akışında shipping profile ve processing/readiness profile gerekir.
- Listing inventory products/offerings/property_values yapısıyla yönetilir.
- Ağustos 2026 itibarıyla üç varyant özelliğini okuma/yazma desteklenmelidir; write için API'nin `max_variations_supported=3` sözleşmesi izlenir.
- Inventory ve shipping batch read için ayrı endpointler vardır; 100 listing ID'ye kadar batch okuma yapılabilir.
- Listing publish için gerekli varlıkların/readiness'in tam olduğu doğrulanmalıdır.

## Entegra resmi eğitim/sürüm notlarından doğrulanan büyük özellik aileleri
Aşağıdaki başlıklar Entegra'nın güncel eğitim merkezi ve sürüm notlarında davranış seviyesinde doğrulanmıştır; birebir UI/kod kopyalanmaz:
- ürün listesi, detaylı arama, toplu işlemler, sağ-tık operasyonları
- kolay/detaylı ürün ekleme ve linkten hızlı ürün ekleme
- varyant/seçenek yönetimi ve platform seçenek eşleme
- paket/set/bundle
- kritik stok, kritik fiyat ve akıllı fiyat
- kategori/marka/platform şablonları
- Excel ürün/sipariş/varyant import-export ve toplu fiyat/adet güncelleme
- XML ürün/sipariş import-export, varyantlı XML şablonları ve ürün-XML bağlantısı
- döviz/kur ayarları ve satış fiyatı/kârlılık hesapları
- kampanyaya stok ayırma
- manuel sipariş, toplu sipariş işlemleri, detaylı filtre ve arşiv
- tanımsız ürün ayarları
- iade yönetimi
- mesaj/soru-cevap merkezi
- bildirim ayarları
- e-fatura/e-arşiv sağlayıcıları ve fatura URL/numarası
- kargo API'leri, barkod/etiket yazdırma, otomatik akıllı etiket
- fatura + kargo etiketi tek çıktı/QR yaklaşımı
- fulfillment entegrasyonları
- hakediş/mutabakat kontrolü
- rekabet/buybox analizi
- ürün denetim/menşei/güvenlik alanları
- tarih bazlı fiyat
- min/max satış adedi
- kanal bazlı sadece fiyat/sadece adet güncelleme
- ürün bazlı bağlı XML'ler
- duplicate integration ID/kargo kodu filtreleri
- stok tükenme süresi raporları

## Mevcut koddaki mimari riskler
- `MainWindow.xaml.cs` çok büyümüş; yeni modüller feature/service/panel olarak ayrılmalı.
- `ChannelProductsPanel` bazı channel adlarını switch/ternary ile isimlendiriyor; connector manifest-driven UI hedeflenmeli.
- `MarketplaceRegistry` statik birkaç kanal kaydı içeriyor; capability registry ve connector certification ile birleşmeli.
- Eski TODO/IMPLEMENTATION_STATUS'ta artık geçersiz DEFERRED satırları var; worker her ilgili paket sonunda güncellemeli.
- Etsy'nin simple PATCH akışı inventory/variation semantiğini karşılamıyor; simple listing ve inventory write ayrılmalı.
- Test command dokümanda dış test projesine bakıyor; clean-clone test altyapısı kesin olarak repo içinde olmalı.

## Hızlandırılmış uygulama sırası
1. Kapsam/queue düzeltme + test altyapısı.
2. Etsy full parity ve official contract hardening.
3. Varyant/seçenek çekirdeği + XML/Excel variant ingest.
4. Bundle/set + hızlı düzenleme + kritik stok/fiyat + tarih bazlı fiyat.
5. Dropshipping source-of-truth/anomaly/stock/price/supplier sistemleri.
6. Marketplace channel policy ve listing drift/reconciliation.
7. Sipariş/iade/fatura/kargo/fulfillment.
8. Hakediş/mutabakat + kârlılık ve operasyon raporları.
9. Mesaj/bildirim/rekabet/ürün güvenliği.
10. Full Entegra parity audit, clean clone, soak, installer ve release freeze.

## Kanıt modeli
Her issue sonunda `REAL_WORK_COUNT`, `VERIFICATION_ONLY_COUNT`, `FILES_CHANGED`, `TESTS_ADDED_OR_CHANGED`, `TEST_RESULT`, `PUBLISH`, `BLOCKER` zorunlu. Hızlı kapanış tek başına başarı değildir; gerçek diff + hedefli test + publish kanıtı gerekir.
