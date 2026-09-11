# Uygulama durumu — .NET 8 EXE

Ana program: Windows-Current/TrMarketplaceHubDesktop.exe. Giriş parolası yoktur.

| İşlev | Durum |
|---|---|
| Mevcut masaüstü ürün/XML/kanal/sipariş ekranları | PRESERVED; API kapsamı README-YONETIM-MERKEZI.md |
| Aktif/pasif ve pasif Etsy gönderim engeli | Mevcut kod korundu |
| Ayrıntılı ürün filtreleri | IMPLEMENTED; gerçek geçici SQLite testi, 218 test toplam |
| CommerceHub merkezi stok/mağaza politikaları | NOT_PORTED |
| Normal ürün ek operasyon alanları | PARTIAL: MPN/fatura adı/alt başlık/raf/tarih taşındı; XML koruma ve kayıt testi geçti |
| Varyant sistemi | DEFERRED_BY_USER |
| Paket/set/bundle | DEFERRED_BY_USER |
| Hızlı satır içi düzenleme | DEFERRED_BY_USER |
| Kritik fiyat | DEFERRED_BY_USER |
| XML varyant mapping | DEFERRED_BY_USER |
| Fulfillment core | DEFERRED_BY_USER |
| Hakediş/mutabakat core | DEFERRED_BY_USER |

M1 Etsy dispatch scheduler (2026-09-11): IMPLEMENTED locally. WPF automation settings and due runner are connected to the idempotent sync queue. Etsy listing dispatch requires a concrete preview, unchanged product version and explicit user confirmation before PATCH. Production credential/scope validation remains environment-dependent; no live store test was run.

PR #4 review follow-up (2026-09-11): SyncJob status is now atomically claimed and marked Succeeded/Failed around Etsy dispatch. A previously succeeded job cannot dispatch again. Added fake HTTP and temporary SQLite coverage; 259 Release tests pass.

M2 Etsy order/stock (stacked on PR #4, 2026-09-11): listing detail read and explicit sale stock decision service implemented. Existing atomic receipt/idempotency path is reused; cancellation/return never restores stock automatically. Live production order scope remains LIVE_API_BLOCKED until official credentials are available.

M3 eBay safe parity (stacked on Issue #7, 2026-09-11): official Inventory/Fulfillment API read/write methods, explicit preview/approval, stale version guard and SyncJob idempotency implemented. 263 Release tests pass; production scope/credential validation remains LIVE_API_BLOCKED.

M4 marketplace adapter core (stacked on Issue #8, 2026-09-11): shared capability/preview contract, encrypted-credential-compatible local metadata boundary and channel/shop isolated mapping for Ozon, Joom, Allegro, Wish and Navlungo. Unsupported or unverified operations produce no HTTP request and remain LIVE_API_BLOCKED. 265 Release tests pass.

Ertelenen alanların tüm alt özellik/ekran/test geliştirmeleri kapsam dışıdır, eksik sayılmaz. Çalışan kod silinmez. Canlı marketplace verisi değiştirilmedi. Görsel masaüstü UI incelemesi bu adımda henüz yapılmadı.

2026-09-11: 219 test geçti. Yeni yayın Windows-Operations/TrMarketplaceHubDesktop.exe; giriş şifresi yok. Açık eski uygulama korunur. Yeni alanların görsel kontrolü yapılmadı.


Sipariş stok düşümü PARTIAL: yerel önizleme+atomik düşüm+duplicate koruma+hareket kaydı tamamlandı; rezervasyon/depo/kanallara gönderim eksik. 234 test; docs/ORDER-STOCK.md. Güncel EXE Windows-Stock/TrMarketplaceHubDesktop.exe.

Mağaza stok politikaları PARTIAL: kalıcı güvenlik stoğu/üst sınır/sürüm kontrolü ve yerel önizleme IMPLEMENTED. Dış API dispatch/depo/rezervasyon henüz yok. 235test ve Release publish başarılı. Güncel yayın Windows-Policies.

Fiyat politikası PARTIAL: yerel formül/kayıt/önizleme ve güvenlik koruması eklendi; bağımsız test/görsel kontrol ve canlı dispatch eksik. Windows-Price yayınlandı, mevcut 235 test geçti.

Excel ekranı PARTIAL: XLSX export+preview uygulandı; atomik import/kolon eşleme eksik. Windows-Excel yayınlandı.

M5 XML tedarikçi merkezi (2026-09-11, Issue #12): mevcut normal XML akışı korunarak gzip kaynak okuma ve kalıcı `XmlRuns` geçmişi eklendi. Manuel/zamanlanmış çalıştırmalar sonuç sayaçları veya hata ile kaydediliyor. XML varyant mapping `DEFERRED_BY_USER`; doğrulanmamış tedarikçi API'leri için endpoint uydurulmadı. 266 Release testi geçti; `Windows-M5-XML` self-contained yayın üretildi.

M6 mağaza ve bağlantı merkezi (2026-09-11, Issue #13): tek WPF panelinde kanal+mağaza metadata, etkinlik, capability, son test/hata ve hızlı geçişler eklendi. Secret değerler yeni tabloya yazılmıyor; mevcut DPAPI tabanlı store'lar kullanılıyor. Etsy/eBay/Ozon salt okunur testleri mevcut resmi istemciler üzerinden çalışıyor; diğer kanallar `LIVE_API_BLOCKED/NOT_CONFIGURED`. 268 Release testi geçti; `Windows-M6-Connections` self-contained yayın üretildi.

M7 ürün yönetimi ana paneli (2026-09-11, Issue #14): ürün listesi/filtre/detay akışı genişletildi; kayıtlı filtre görünümleri, XML kaynağı/son güncelleme, yerel kanal planı özeti, sayfalama ve satır sanallaştırması eklendi. Çoklu normal aktif/pasif işlemi yalnız yerel katalogda çalışır. 269 Release testi geçti; `Windows-M7-Products` self-contained yayın üretildi. Varyant/bundle/hızlı düzenleme/kritik fiyat geliştirilmedi.

M8 sipariş merkezi (2026-09-11, Issue #15): mevcut WPF sipariş ekranı ortak stok karar önizleme/onay servisine bağlandı. Kanal/mağaza/stok kararı filtreleri ve listede durum görünürlüğü eklendi; duplicate satış ikinci kez stok düşürmüyor, eksik SKU işlem üretmiyor. Fulfillment ve hakediş/mutabakat geliştirilmedi. 271 Release testi geçti; `Windows-M8-Orders` self-contained yayın üretildi.

M9 Amazon connector (2026-09-11, Issue #16): seller/LWA/region/marketplace ayarları DPAPI ile saklanan bağımsız sınır ve WPF bağlantı paneli olarak eklendi. Doğrulanmamış sözleşme nedeniyle hiçbir Amazon endpointi çağrılmıyor; bağlantı testi açıkça `LIVE_API_BLOCKED`. 273 Release testi geçti; `Windows-M9-Amazon` self-contained yayın üretildi.

M10 Trendyol connector (2026-09-11, Issue #17): güvenli Supplier/API ayar modeli, DPAPI store, bağlantı paneli ve yerel ürün planı eklendi. Doğrulanmamış resmi sözleşme nedeniyle endpoint çağrısı yapılmıyor; kullanıcıya `LIVE_API_BLOCKED` açıkça gösteriliyor. 275 Release testi geçti; `Windows-M10-Trendyol` self-contained yayın üretildi.

M11 Hepsiburada connector (2026-09-11, Issue #18): merchant/API ayar modeli, DPAPI store, bağlantı paneli ve yerel ürün planı eklendi. Doğrulanmamış resmi sözleşme nedeniyle endpoint çağrısı yapılmıyor; `LIVE_API_BLOCKED` açıkça gösteriliyor. 277 Release testi geçti; `Windows-M11-Hepsiburada` self-contained yayın üretildi.

M12 Fruugo connector (2026-09-11, Issue #19): retailer/API ayar modeli, DPAPI store, bağlantı paneli ve yerel ürün planı eklendi. Resmi ürün/sipariş sözleşmesi doğrulanmadığı için endpoint çağrısı yapılmıyor; `LIVE_API_BLOCKED` açıkça gösteriliyor. 279 Release testi geçti; `Windows-M12-Fruugo` self-contained yayın üretildi.

M13 sync, otomasyon ve hata merkezi (2026-09-11, Issue #20): SyncStore hata sınıfları, maskeli hata saklama, retry edilebilirlik kapısı ve arama/durum filtreli WPF merkezi eklendi. Otomasyon ve XML çalıştırma geçmişi aynı merkezde sekmeler halinde izleniyor. 281 Release testi geçti; `Windows-M13-Sync` self-contained yayın üretildi.

M14 Ozon connector (2026-09-11, Issue #21): mevcut resmi ürün/depo read-only istemcisi sayfalı ürün özeti ve doğrulama ile genişletildi. Ürün yazma, sipariş ve stok/fiyat canlı operasyonları doğrulanmadığı için açılmadı. 282 Release testi geçti; `Windows-M14-Ozon` self-contained yayın üretildi.

M15 Allegro connector (2026-09-11, Issue #22): Allegro public API teklif ve sipariş GET akışları resmi içerik tipi/bearer auth ile eklendi; DPAPI ayar paneli ve yerel ürün planı bağlandı. Yazma/fulfillment yapılmadı, OAuth refresh ve canlı credential doğrulaması sonraki adıma bırakıldı. 284 Release testi geçti; `Windows-M15-Allegro` self-contained yayın üretildi.

M16 Joom connector (2026-09-11, Issue #23): merchant/API ayar store'u, WPF bağlantı paneli ve yerel ürün planı eklendi. Güncel API kimlik/endpoint sözleşmesi doğrulanmadığı için canlı çağrı yok; `LIVE_API_BLOCKED`. 286 Release testi geçti; `Windows-M16-Joom` self-contained yayın üretildi.

M17 Wish connector (2026-09-11, Issue #24): güvenli merchant/API ayar store'u, WPF bağlantı paneli ve yerel ürün planı eklendi. Resmi sözleşme doğrulanmadığı için canlı endpoint çağrısı yapılmıyor; `LIVE_API_BLOCKED`. 288 Release testi geçti; `Windows-M17-Wish` self-contained yayın üretildi.

M19 ana dashboard ve rapor/bildirim merkezi (2026-09-11, Issue #26): program açılışına ürün, sipariş, XML, sync ve bağlantı metriklerini salt-okunur yerel snapshot olarak gösteren özgün Genel Bakış ekranı eklendi. Kanal sağlığı, maskeli hata bildirimleri, ilgili modüle hızlı aksiyonlar ve 14 günlük sipariş trendi bulunuyor; ağır BI veya canlı marketplace değişikliği yok. 290 Release testi geçti; `Windows-M19-Dashboard` self-contained yayın üretildi.

M20 Windows kurulum, yedekleme ve taşıma (2026-09-11, Issue #27): `installer/` altında self-contained publish paketleme, kullanıcı profiline kurulum ve yalnız uygulama dosyalarını kaldırma scriptleri eklendi. `DataBackupService` manifest/SHA-256 doğrulamalı zip, DPAPI credential byte'larını çözmeden yedekleme, mevcut veri için güvenlik yedeği ve atomic restore sağlıyor; Ayarlar paneline bağlandı. Uzak update endpoint'i uydurulmadı. 292 Release testi geçti; `Windows-M20-Installer` self-contained yayın üretildi.

M21 üretim öncesi doğrulama ve sertleştirme (2026-09-11, Issue #28): XML/Excel-katalog-sync-sipariş-stok akışı, kanal/mağaza izolasyonu, duplicate/stale/retry/cancellation/transaction sınırları ve secret redaction yeniden doğrulandı. XML ve otomasyon hata geçmişi credential desenlerini maskeliyor; `installer/Smoke-Test.ps1` ve `docs/PRODUCTION-READINESS.md` eklendi. 294 Release testi geçti; `Windows-M20-Installer` publish ve installer smoke başarılı.

M22 tedarikçi / XML kaynak merkezi (2026-09-11, Issue #29): XML kaynak ekranına erişim/format sağlık kontrolü, kaynak bazlı ürün ve son çalışma özeti, mapping/fiyat kuralı çoğaltma ve credential kopyalamayan kaynak klonlama eklendi. Aynı kaynak için ikinci Running import engellendi; XML hata geçmişi redacted kalıyor. 295 Release testi geçti; `Windows-M22-XML-Center` self-contained yayın üretildi. XML Variant Mapping `DEFERRED_BY_USER`.

M23 kategori / marka / özellik eşleme merkezi (2026-09-11, Issue #30): Yerel sözlükler ve kanal/mağaza bazlı harici eşlemeler tek WPF panelinde birleştirildi. Eşlemeler `MAPPED`, `MISSING`, `STALE` ve `INVALID` durumlarıyla filtrelenebilir; normalize isim önerileri yalnız önizleme/onay sonrasında uygulanır. Mapping geçmişi tutulur ve kanal/mağaza sınırı dışına taşma engellenir. 298 Release testi geçti; self-contained `Windows-M23-Taxonomy` yayını üretildi. Marketplace metadata uzaktan yazımı resmi sözleşme olmadan açılmadı; XML Variant Mapping ve diğer kullanıcı ertelemeleri kapsam dışıdır.

M24 ürün görsel ve medya yönetim merkezi (2026-09-11, Issue #31): `MediaStore` ile ürün görselleri URL/kaynak/sıra/ana görsel/hash/doğrulama durumu olarak ayrı SQLite kaydına alındı; mevcut katalog ImageUrls verisi geriye dönük içeri alınır. WPF Görsel / medya ekranı önizleme, kaynak görünümü, duplicate koruması ve güvenli doğrulama/yeniden deneme sağlar. Timeout, 404, 20 MB, unsupported format ve HTTP hataları sınıflandırılır; marketplace yazımı yoktur. 301 Release testi geçti; self-contained `Windows-M24-Media` yayını üretildi.

M25 Excel şablon ve gelişmiş içe/dışa aktarma merkezi (2026-09-11, Issue #32): Profil store'u kolon eşlemeleri, başlık alias'ları, sayı/tarih kültürü, varsayılanlar ve görünür alanları saklar. Profil önizlemesi satır bazında create/update/skip/error kararı üretir; filtreli ürün dışa aktarımı ve hata raporu mevcut atomik import/undo akışına bağlandı. 303 Release testi geçti; self-contained `Windows-M25-Excel` yayını üretildi. Varyant/bundle ve diğer `DEFERRED_BY_USER` alanları kapsam dışıdır.

M26 kanal yayın durumu ve ürün listeleme matrisi (2026-09-11, Issue #33): Ürün × kanal × mağaza matrisi yerel plan/listing, mapping, sync ve bağlantı sağlığını tek WPF panelinde karşılaştırır. Mapping/sync/auth/stale filtreleri, kanal/mağaza araması ve ürün/kanal hızlı geçişleri eklendi; capability bilgisi yerel planla ayrıştırıldı, canlı toplu write yapılmadı. 305 Release testi geçti; self-contained `Windows-M26-Listing-Matrix` yayını üretildi.

M27 sipariş istisna, iptal ve iade karar merkezi (2026-09-11, Issue #34): `OrderExceptionStore` eksik/ambiguous SKU ve iptal/iade olaylarını kanal+mağaza+sipariş+olay anahtarıyla upsert eder; karar paneli öncelik, durum, tür ve yaşlandırma filtreleri sunar. Mevcut receipt verisinden `OrderRestockPreview` üretilir; explicit onay, ürün sürümü ve tek sipariş restore idempotency kontrolüyle yerel stok geri koyulur. 308 Release testi geçti; self-contained `Windows-M27-Order-Exceptions` yayını üretildi. Fulfillment ve canlı marketplace iptal/iade write kapsam dışıdır.

M28 tanılama, audit trail ve destek merkezi (2026-09-11, Issue #35): `AuditStore` kritik işlemleri maskeli alanlarla yerel SQLite'ta retention sınırıyla saklar; `DiagnosticsService` DB/sync/son hata/disk durumunu salt-okunur raporlar. Yeni Tanılama / audit ekranı arama ve destek paketi dışa aktarmayı sunar. Destek paketi yalnız metadata, audit ve redacted log içerir; credential/token/password ve yerel DB dosyaları dışarı alınmaz. 311 Release testi geçti; self-contained `Windows-M28-Diagnostics` yayını üretildi. Canlı marketplace mutation ve otomatik recovery açılmadı.

M29 gezinme, kayıtlı görünümler ve UX (2026-09-11, Issue #36): Sol menü araması, son route/breadcrumb/geri geçmişi, ürün-sipariş-sync-XML görünüm profilleri, ürün kolon görünürlüğü tercihi, Ctrl+K global yerel arama ve temel kısayollar eklendi. UI tercihleri credential içermez; canlı marketplace mutation/quick edit/deferred alanlar korunur. 314 Release testi geçti; self-contained `Windows-M29-Navigation` yayını üretildi. Gerçek masaüstü görsel smoke testi bu ortamda çalıştırılmadı.

M30 mesaj ve müşteri iletişim merkezi (2026-09-11, Issue #47): `MessageStore` kanal/mağaza/sipariş/ürün/müşteri mesajlarını ve yerel şablonları SQLite'ta saklar; harici ID duplicate koruması, okundu/taslak/hata durumları ve arama filtreleri vardır. WPF Mesaj merkezi yanıt önizlemesi, bağlam geçişleri ve capability durumunu gösterir; doğrulanmamış kanallarda HTTP read/write yapılmaz. Hata alanı secret-safe sanitize edilir, mesaj gövdesi audit loguna yazılmaz. 317 Release testi geçti; self-contained `Windows-M30-Messages` yayını üretildi. Gerçek inbox API'leri resmi sözleşme/scope doğrulanana kadar LIVE_API_BLOCKED/NOT_SUPPORTED.

M31 güvenli toplu ürün işlemleri (2026-09-11, Issue #48): `BulkProductOperations` seçili/filtrelenmiş ürünlerde normal alan ve kanal planı preview'i üretir. READY/SKIP/ERROR satırları, kilit/zorunlu alan kontrolü, optimistic `UpdatedUtc` ön kontrolü ve atomik CatalogProducts transaction ile güvenli yerel uygulama sağlanır. Uzun işlem ilerleme/iptal edilebilir; kanal planları yalnız yerel taslaktır, canlı marketplace write yoktur. 320 Release testi geçti; self-contained `Windows-M31-Bulk-Products` yayını üretildi.

M32 stok ve fiyat politika yönetim merkezi (2026-09-11, Issue #49): Stok/fiyat policy listeleri, etkinlik, sürüm, son değişiklik, kontrollü kopyalama ve ürün bazlı detailed preview tek WPF merkezde toplandı. Policy/product version bilgisi stale/write güvenliği için döndürülür; mevcut formül/döviz/minimum korumaları kullanılır, kritik fiyat kapsam dışıdır. 322 Release testi geçti; self-contained `Windows-M32-Policy-Center` yayını üretildi. Bu merkez doğrudan marketplace HTTP yazmaz.

M33 veri kalite merkezi (2026-09-11, Issue #50): `DataQualityService` ürün, XML geçmişi ve kanal planlarını duplicate SKU/barkod/listing, eksik alan, geçersiz sayı/döviz/URL, kaynak hatası ve `LiveApiBlocked` açısından tarar. `DataQualityStore` fingerprint benzersizliğiyle aynı sorunu çoğaltmadan saklar ve çözülmüş durumunu korur; WPF merkezinde filtre, sayaç, detay ve bağlam navigasyonu vardır. Düzeltme yalnız preview önerisidir; otomatik ürün veya canlı marketplace yazımı yoktur. 324 Release testi geçti; self-contained `Windows-M33-Data-Quality` yayını üretildi.

M34 API bağlantı sağlığı, kota ve rate-limit merkezi (2026-09-11, Issue #51): Mevcut salt-okunur connector probe'larını `ApiHealthCaptureHandler` ile gözlemleyen `ApiHealthStore` kanal/mağaza bazında auth, HTTP, son başarılı istek, rate-limit/reset, Retry-After, backoff ve maskeli hatayı saklar. 401/403/408/429/5xx, timeout ve ağ hataları sınıflandırılır; backoff aktifken manuel test ertelenir. API bağlantı sağlığı WPF ekranı arama, durum filtresi, sayaç ve bağlantı/kanal geçişlerini sunar. Resmi endpointi doğrulanmayan kanallar `LIVE_API_BLOCKED` kalır; canlı write yoktur. 327 Release testi geçti; self-contained `Windows-M34-Api-Health` yayını üretildi.

M35 otomasyon takvimi, şablonlar ve çalışma pencereleri (2026-09-11, Issue #52): `AutomationStore` mevcut SQLite/lease modelini migration ile genişleterek Interval/Daily/Weekly takvim, yerel saat/gün, çalışma penceresi, retry limiti/backoff, failure count ve template key saklar. XML, stok, fiyat, sağlık ve normal sync şablonları `AutomationRunner` üzerinden yalnız yerel `SyncStore` işine dönüşür; canlı endpoint uydurulmaz. Başarı takvime göre yeni çalışma planlar, hata sınırlı exponential backoff uygular; WPF ekranı manuel due, etkinlik, sonraki çalışma ve audit akışını gösterir. 330 Release testi geçti; self-contained `Windows-M35-Automation-Calendar` yayını üretildi.

M36 döviz, vergi ve yerel ayar yönetim merkezi (2026-09-11, Issue #53): `LocaleSettingsStore` kanal+mağaza bazında desteklenen para birimi, sayı/tarih kültürü, KDV oranı ve sürümü saklar; kontrollü kopyalama audit'e yazılır. Ürün kartı/Excel'e `VatRate` ve `KDV %` eklenir; veri kalite taraması `InvalidVatRate` ve desteklenmeyen dövizi canlı sync öncesi görünür yapar. `LocaleSettings.Format` parse/format kültür tutarlılığını garanti eder. Hakediş/mutabakat ve kritik fiyat geliştirilmez. 332 Release testi geçti; self-contained `Windows-M36-Locale-Settings` yayını üretildi.

M37 global arama, hızlı erişim ve yerel indeksleme (2026-09-11, Issue #54): `GlobalSearchIndexStore` transaction/migration-safe `search-index.db` içinde ürün, sipariş, mağaza, ilan, XML, sync, veri kalite ve API sağlığı metadata'sını tutar. `GlobalSearchIndexService` 30 saniyelik cache, cancellation ve arka plan rebuild/query uygular; secret/credential, müşteri ve mesaj gövdesi indekse girmez. Ctrl+K araması sonuçları ilgili WPF ekranına yönlendirir. 334 Release testi geçti; self-contained `Windows-M37-Global-Search` yayını üretildi. Canlı marketplace write yoktur.

M38 eski veri içe alma ve güvenli geçiş asistanı (2026-09-11, Issue #55): `MigrationAssistantService` Excel/CSV/TSV/JSON/XML dışa aktarımlarını şema ve satır preview'ine çevirir; SKU/barkod duplicate, CREATE/UPDATE/SKIP/ERROR ve stale kontrolü yapar. `CatalogStore.ApplyMigration` transaction + receipt, `MigrationJournalStore` geri alma kaydı, DataBackup yedek adımı ve marka/kategori/mağaza metadata planı eklendi. Credential/token/password alanları filtrelenir; proprietary DB veya canlı marketplace write yoktur. 336 Release testi geçti; self-contained `Windows-M38-Migration-Assistant` yayını üretildi.

M39 ilk kurulum sihirbazı ve bağlantı onboarding (2026-09-11, Issue #56): `OnboardingStore` son adım/skip/tamamlanma state'ini credential içermeden SQLite'ta saklar; `OnboardingSetupService` mağaza metadata, XML kaynak, stok/fiyat policy ve Excel profil başlangıcını mevcut güvenli store'lara bağlar. İlk açılışta opsiyonel WPF sihirbazı, resume, API capability özeti ve modül hızlı geçişleri sunulur; canlı write yapılmaz. 338 Release testi geçti; self-contained `Windows-M39-Onboarding` yayını üretildi.

M40 son üretim sertleştirmesi (2026-09-11, Issue #57): `ProductionReadinessService` yerel veri klasörü yazılabilirliği, core SQLite okunabilirliği, açık secret biçimleri, veri kalite kritik kayıtları, connector capability sınırı ve API sağlık/backoff durumunu salt-okunur raporlar. `Üretim hazırlığı` WPF ekranı bu geçidi görünür kılar; hiçbir marketplace endpoint'i çağrılmaz ve `LIVE_API_BLOCKED` durumu gizlenmez. Foküslü güvenlik/kalite testleri eklendi; tam Release test ve self-contained publish doğrulaması bu dalın kabul adımıdır.

M41 teknik borç ve güvenlik düzeltme turu (2026-09-11, Issue #61): Audit ve destek paketi redaction katmanı Authorization bearer/basic başlıklarını, JSON credential alanlarını ve query-string tokenlarını merkezi olarak maskeler. `LastFailure` yalnızca Outcome=`Failed` kayıtlarını doğrudan sorgular. Audit/tanılama/destek ZIP sentinel regresyon testleri eklendi; Release testleri 344/344 geçti. Self-contained win-x64 publish: `Windows-M41-Review-Hardening`. Canlı marketplace write açılmadı; `LIVE_API_BLOCKED` görünürlüğü korunuyor.

M42 Etsy satışa hazırlık dry-run (2026-09-11, Issue #62): Etsy sekmesine credential/mağaza, ilan şablonu, ürün eşleme, yerel dry-run ve canlı yazma kapısı kontrollerini tek ekranda sunan `EtsyReadinessService`/paneli eklendi. Dry-run yalnız yerel `EtsyDrafts.Validate` kullanır; HTTP write veya otomatik yayın yapmaz. 344 Release testi geçti; self-contained publish `Windows-M42-Etsy-Readiness` olarak üretildi. Eksik resmi credential/scope veya stale durum açıkça BLOCKED gösterilir.

M43 XML kullanım paritesi kabul turu (2026-09-11, Issue #63): XML kaynak/test/ürün düğümü/alan eşleme/önizleme/onay/import geçmişi ve create/update/skip/error UX akışı `docs/XML-PARITY.md` ile doğrulandı. Duplicate SKU/barkod, bozuk XML, gzip/encoding/timeout ve sipariş kaynaklı stok kilidi mevcut çekirdek akışında korunuyor. XML varyant mapping `DEFERRED_BY_USER`; 344 Release testi geçiyor.

M44 Core UI kullanılabilirlik smoke turu (2026-09-11, Issue #64): Ürün/sipariş/XML/Excel ve ayar ekranlarında sanallaştırma/sayfalama, uzun işlem iptal/ilerleme, global navigasyon ve async çift tetikleme kapıları doğrulandı. `docs/UI-SMOKE.md` fiziksel DPI smoke sınırını ve mevcut davranışı kaydeder. 344 Release testi geçti; self-contained publish `Windows-M44-UI-Smoke` olarak üretildi.

M45 release integration (2026-09-11, Issue #80): M1–M44 stacked zinciri `codex/issue-80-release-integration` üzerinde konsolide edilip SQLite migration sırası, secret redaction, explicit approval, stale/idempotency ve transaction güvenlikleri mevcut regresyon testleriyle doğrulandı. 344/344 Release testi, Release build ve self-contained win-x64 publish başarılı; ayrıntı `docs/RELEASE-INTEGRATION.md`.

M46 günlük kullanım parite matrisi (2026-09-11, Issue #81): Ürün/toplu işlem/arama/kategori/marka/XML/ayar/Excel/sipariş başlıkları mevcut ekranlarla TAM olarak eşleştirildi ve `docs/DAILY-PARITY-MATRIX.md` kaydedildi. Riskli akış kapıları, performans ve `DEFERRED_BY_USER` sınırları korunuyor.

M47 Etsy resmi OAuth ve read doğrulaması (2026-09-11, Issue #82): Resmi dokümana göre OAuth token endpoint'i `https://api.etsy.com/v3/public/oauth/token` olarak güncellendi; PKCE/state/HTTPS callback ve scope doğrulaması korundu. Credential gerektiren gerçek mağaza read testi çalıştırılmadı; fake HTTP contract testleri kullanıldı.

M48 Etsy listing yaşam döngüsü (2026-09-11, Issue #83): Etsy listing publish akışı resmi `updateListing` PATCH `state=active` çağrısı olarak eklendi. Create/update/publish işlemleri preview, explicit approval, stale ürün sürümü ve SyncJob idempotency ile korunuyor; görsel yükleme ayrı ve credential-safe. 345 Release testi geçti; gerçek mağazaya write yapılmadı.

M49 Etsy sipariş senkronu (2026-09-11, Issue #84): `OrdersEtsyClient` resmi receipts read akışına 100'lü pagination ve `min_last_modified` incremental filtresi eklendi. `OrdersStore` mağaza+receipt idempotent upsert ve eski yerel kargo olaylarını korur; stok/iptal/iade karar kapıları değişmedi. 345 Release testi geçti; canlı credential olmadan gerçek API çağrısı yapılmadı.

M50 uçtan uca satıcı dry-run (2026-09-11, Issue #85): Sentetik XML/katalog/order fixture ile ürün alma, Etsy stok kararı, transaction ve restart sonrası duplicate koruması baştan sona doğrulandı. Fake adapter kullanıldı; canlı marketplace write/credential/PII yok. 346 Release testi geçti; `docs/SELLER-DRY-RUN.md`.

M51 ürün listesi/arama (Issue #86, 2026-09-11): CatalogStore.Search artık cancellation token kabul ediyor; kanal/mağaza benzeri liste filtreleri NOCASE ile eşleşiyor; Unicode arama ve iptal testleri eklendi. docs/taskpacks/M51.md 100/100 kanıt checklist'i. Canlı marketplace write yok.

M51 doğrulama: Release test 348/348 geçti; self-contained win-x64 yayın Windows-M51-Product-List oluşturuldu.

M52 toplu ürün işlemleri (Issue #87, 2026-09-11): kanal/mağaza mapping batch'inde tüm optimistic kontroller tamamlanmadan commit başlamıyor; commit başladıktan sonra cancellation ile yarım batch bırakılmıyor. Cancellation/atomiklik testi eklendi. docs/taskpacks/M52.md 100/100.

M52 doğrulama: Release test 349/349 geçti; self-contained win-x64 yayın Windows-M52-Bulk-Ops oluşturuldu.

M53 ürün detay/düzenleme (Issue #88, 2026-09-11): SaveProduct ürün adı/SKU/barkod/marka/kategori/açıklama ve 3 karakter döviz sınırlarını doğruluyor; mevcut UpdatedUtc stale guard korunuyor. docs/taskpacks/M53.md 100/100.

M53 doğrulama: Release test 349/349 geçti; self-contained win-x64 yayın Windows-M53-Product-Detail oluşturuldu.

M54 XML kaynak/mapping (Issue #89, 2026-09-11): XmlCatalog kaynak adresini güvenli şemalarla sınırlar ve URL şemasını sınırlar; credential metni indekslenmez; XML variant mapping kapsam dışıdır. docs/taskpacks/M54.md 100/100.

M54 doğrulama: Release test 349/349 geçti; self-contained win-x64 yayın Windows-M54-XML oluşturuldu.

M55 Excel (Issue #90, 2026-09-11): CatalogExcel.Preview artık dosya içi yinelenen SKU/barkodları satır hatası olarak raporlar; hatalı önizleme Apply tarafından atomik biçimde reddedilir. docs/taskpacks/M55.md 100/100.

M55 doğrulama: Release test 349/349 geçti; self-contained win-x64 yayın Windows-M55-Excel oluşturuldu.

M56 taxonomy (Issue #91, 2026-09-11): TaxonomyStore öneri normalizasyonu aksanları ve ardışık boşlukları tek biçime getiriyor; mevcut stale/invalid/store izolasyonu korunuyor. docs/taskpacks/M56.md 100/100.

M56 doğrulama: yeniden çalıştırılan Release test 349/349 geçti; self-contained win-x64 yayın Windows-M56-Taxonomy oluşturuldu.

M57 medya (Issue #92, 2026-09-11): MediaStore.List arama sorgusu SQL wildcard injection/yanlış eşleşmeye karşı escape edildi; URL normalize, duplicate/hash, doğrulama durumları korunuyor. docs/taskpacks/M57.md 100/100.

M57 doğrulama: Release test 349/349 geçti; self-contained win-x64 yayın Windows-M57-Media oluşturuldu.

M58 sipariş liste/detay (Issue #93, 2026-09-11): OrdersStore.ReadPage birleşik filtreleme ve sayfalama sağlar; mevcut order validation, tracking güvenliği ve stale merge korunuyor. docs/taskpacks/M58.md 100/100.

M58 doğrulama: Release test 349/349 geçti; self-contained win-x64 yayın Windows-M58-Orders oluşturuldu.

M59 rebase doğrulama: #94 branch’i #93 güncel head’i üzerine taşındı; önceki M59 test/publish kanıtı korunuyor.

M60 sync kuyruğu (Issue #95, 2026-09-11): SyncStore retry sınıflandırmasına 5s tabanlı, 300s üst sınırlı RetryDelay eklendi; atomic claim, duplicate key, failure cap ve secret redaction korunuyor. docs/taskpacks/M60.md 100/100.

M60 doğrulama: Release test 349/349 geçti; self-contained win-x64 yayın Windows-M60-Sync oluşturuldu.

M61 otomasyon (Issue #96, 2026-09-11): AutomationStore.Due yalnız etkin, vadesi gelmiş ve lease'i boş işleri döndürür; AutomationSchedule.WindowContains gece yarısı pencerelerini kapsar. docs/taskpacks/M61.md 100/100.

M62 tanılama (Issue #97, 2026-09-11): SyncStore.Explain hata sınıflarını Türkçe, credential içermeyen açıklamalara dönüştürüyor; mevcut redaction/retry/idempotency korunuyor. docs/taskpacks/M62.md 100/100.

M63 güvenlik (Issue #98, 2026-09-11): MarketplaceConnectionStore.Redact Authorization Bearer/Basic ve secret query parametrelerini temizler; destek/audit sanitization zinciri korunur. docs/taskpacks/M63.md 100/100.

M64 yedek/restore (Issue #99, 2026-09-11): DataBackupService manifest validation duplicate yolları, negatif uzunluk ve geçersiz SHA-256 özetlerini reddediyor; atomic staging ve safety backup korunuyor. docs/taskpacks/M64.md 100/100.

M65 installer (Issue #100, 2026-09-11): InstallPathPolicy güvenli kurulum yolunu normalize eder, geçersiz ve Windows sistem klasörlerini reddeder; güncelleme endpointi uydurulmadı. docs/taskpacks/M65.md 100/100.

M66 performans (Issue #101, 2026-09-11): OrdersStore.ReadPage SQL filtre/count/order/limit kullanır; OrdersStore ve SyncStore WAL/busy-timeout/index ayarlarıyla concurrency sertleştirildi; stale Running sync işleri `RecoverAbandonedRunning` ile restart sonrası güvenle kuyruğa alınır. Release build ve self-contained `Windows-M66-Performance` publish başarılı. Bu klonda test projesi bulunmadığı için test sayısı yeniden doğrulanamadı. REAL_WORK_COUNT=3, VERIFICATION_ONLY_COUNT=7.
M67 UX/search (Issue #102, 2026-09-11): Global arama için bağımsız 300 ms debounce, cancellation/revision koruması ve ana erişilebilir adlar eklendi. Release build ve self-contained `Windows-M67-UX-Search` publish başarılı. UI test projesi bu klonda bulunmadı. REAL_WORK_COUNT=2, VERIFICATION_ONLY_COUNT=8.
M68 Etsy OAuth/read (Issue #103, 2026-09-11): Required scope seti görünürleştirildi; token expiry güvenlik payı ve gerektiğinde refresh akışı eklendi. Release build ve self-contained `Windows-M68-Etsy-OAuth` publish başarılı. Gerçek credential/mağaza testi yapılmadı (`LIVE_API_BLOCKED`). REAL_WORK_COUNT=2, VERIFICATION_ONLY_COUNT=8.
M69 Etsy listing production (Issue #104, 2026-09-11): Etsy readiness expired/near-expiry token’ları canlı işlem öncesi BLOCKED gösterir; mevcut resmi listing create/update/publish güvenlik kapıları korunur. Release build ve self-contained `Windows-M69-Etsy-Production` publish başarılı. Gerçek mağaza write yapılmadı (`LIVE_API_BLOCKED`). REAL_WORK_COUNT=1, VERIFICATION_ONLY_COUNT=9.
M70 final QA/release (Issue #105, 2026-09-11): Installer smoke EXE ile birlikte runtimeconfig/deps metadata’sını doğrular; release runbook eklendi. Release build/publish ve installer smoke başarılı. Harici test projesi bu klonda yok; canlı credential/write testi yapılmadı. REAL_WORK_COUNT=1, VERIFICATION_ONLY_COUNT=9.
M71 startup/recovery (Issue #106, 2026-09-11): Atomic startup marker, unclean-exit görünürlüğü ve kapanış öncesi lifetime cancellation eklendi. Release build ve self-contained `Windows-M71-Startup-Recovery` publish başarılı. Crash/restart testi için harici test projesi bu klonda yok. REAL_WORK_COUNT=2, VERIFICATION_ONLY_COUNT=8.
M72 SQLite schema/migration (Issue #107, 2026-09-11): Catalog/sync/orders store’larına ortak `quick_check` ve `user_version` gate’i, transaction içi version set’i ve newer-schema güvenli fail eklendi. Release build ve self-contained `Windows-M72-Schema` publish başarılı. Harici test projesi bu klonda yok. REAL_WORK_COUNT=3, VERIFICATION_ONLY_COUNT=7.
M73 catalog integrity (Issue #108, 2026-09-11): Product update’te non-empty SKU/barkod duplicate kontrolü aynı optimistic transaction’a eklendi; required-field, stale/version, provenance ve safe-delete korumaları korundu. Release build ve self-contained `Windows-M73-Catalog-Integrity` publish başarılı. Harici test projesi bu klonda yok. REAL_WORK_COUNT=1, VERIFICATION_ONLY_COUNT=9.
M74 provenance (Issue #109, 2026-09-11): Ürünlere kaynak türü/zamanı ve bağımsız fiyat-stok-medya provenance alanları eklendi; XML/Excel import ve ürün gridine bağlandı. Release build ve self-contained `Windows-M74-Provenance` publish başarılı. Harici test projesi bu klonda yok. REAL_WORK_COUNT=3, VERIFICATION_ONLY_COUNT=7.
M75 search/filter (Issue #110, 2026-09-11): Arama ve çoklu filtre değerleri trim/empty-safe hale getirildi; Brand/Category/SKU exact filtreleri için expression index’ler eklendi. Release build ve self-contained `Windows-M75-Search-Filter` publish başarılı. Harici test projesi bu klonda yok. REAL_WORK_COUNT=2, VERIFICATION_ONLY_COUNT=8.
M76 XML parser (Issue #111, 2026-09-11): Malformed XML/reader argument hataları kullanıcıya anlaşılır, import yapmayan sonuçlara dönüştürüldü; DTD, resolver, size, row ve namespace güvenlikleri korundu. Release build ve self-contained `Windows-M76-XML-Parser` publish başarılı. Harici test projesi bu klonda yok. REAL_WORK_COUNT=1, VERIFICATION_ONLY_COUNT=9.
M77 XML scheduler (Issue #112, 2026-09-11): XmlRunStore aynı kaynak için tek Running kaydını SQLite partial unique index ile atomik korur; yarış hatası güvenli duplicate-run mesajına çevrilir. Release build ve self-contained `Windows-M77-XML-Scheduler` publish başarılı. Harici test projesi bu klonda yok. REAL_WORK_COUNT=1, VERIFICATION_ONLY_COUNT=9.
M78 Excel import (Issue #113, 2026-09-11): ApplyWithUndo Excel provenance alanlarını korur; 100 MB üzeri XLSX parse öncesi reddedilir. Release build ve self-contained `Windows-M78-Excel-Import` publish başarılı. Harici test projesi bu klonda yok. REAL_WORK_COUNT=2, VERIFICATION_ONLY_COUNT=8.
M79 Excel export (Issue #114, 2026-09-11): Ürün export’una provenance alanları ve ürün/hata raporlarında atomic temporary-file replacement eklendi. Release build ve self-contained `Windows-M79-Excel-Export` publish başarılı. Harici test projesi bu klonda yok. REAL_WORK_COUNT=2, VERIFICATION_ONLY_COUNT=8.
M80 taxonomy (Issue #115, 2026-09-11): External taxonomy key validation rejects empty, overlong and control-character identifiers before mapping. Marketplace/shop isolation, suggestion-only behavior and history remain intact. Release build and self-contained `Windows-M80-Taxonomy` publish successful. External test project is absent. REAL_WORK_COUNT=1, VERIFICATION_ONLY_COUNT=9.
M81 store settings (Issue #116, 2026-09-11): Etsy read-only probe requires credential ShopId to match the selected shop; deactivation records `DISABLED` while preserving metadata and health history; reactivation has an explicit `NOT_CONFIGURED` transition. Release build and self-contained publish verified. External test project is absent; live credential/marketplace test is `LIVE_API_BLOCKED`. REAL_WORK_COUNT=3, VERIFICATION_ONLY_COUNT=7.
M82 multishop isolation (Issue #117, 2026-09-11): Sync jobs and automation enqueue operations now carry ShopId; the legacy sync idempotency index is repaired to a channel+shop scoped unique key. Release build verified; external tests absent and live write blocked. REAL_WORK_COUNT=3, VERIFICATION_ONLY_COUNT=7.
M83 price policy (Issue #118, 2026-09-11): PricePolicy currency values normalize with invariant trim/uppercase across input and deserialization, preventing culture-dependent policy/output drift. Existing manual-rate, minimum price/margin, deterministic rounding and shop-scoped preview gates remain active. Release build and self-contained publish verified; external tests absent and live price write blocked. REAL_WORK_COUNT=1, VERIFICATION_ONLY_COUNT=9.
M84 stock policy (Issue #119, 2026-09-11): Normal stock preview now rejects disabled policies before automation can enqueue a dispatch; policy keys reject control characters and persisted policy identity/value mismatches fail with safe wrong-shop/validation errors. Safety/maximum stock and order receipt/restock stale-idempotency gates remain active. Release build and self-contained publish verified; external tests absent and live write blocked. REAL_WORK_COUNT=3, VERIFICATION_ONLY_COUNT=7.
M85 media pipeline (Issue #120, 2026-09-11): Media validation now classifies HTTP 429 separately, retries bounded transient failures with cancellation, and caches successful normalized-URL results with explicit invalidation. Size/format/timeout/404 and source/primary protections remain. Release build and self-contained publish verified; external tests absent and no live media write. REAL_WORK_COUNT=3, VERIFICATION_ONLY_COUNT=7.
M86 order ingestion (Issue #121, 2026-09-11): Etsy receipt ingestion now normalizes bounded status/currency/item/shipment fields and separates source update time from local sync time via `SourceUpdatedAt`. Shop/order idempotency, since-last-modified pagination and exception reconciliation remain. Release build and self-contained publish verified; external tests absent and live write not performed. REAL_WORK_COUNT=3, VERIFICATION_ONLY_COUNT=7.
M87 order exceptions (Issue #122, 2026-09-11): Exception metadata/messages are bounded and redacted; cancel/return detection is invariant-culture; missing/ambiguous SKU remains exception-only. Receipt-based restock preview, explicit approval, stale and duplicate restore guards remain. Release build and self-contained publish verified; external tests absent and live write not performed. REAL_WORK_COUNT=2, VERIFICATION_ONLY_COUNT=8.
M88 sync recovery (Issue #123, 2026-09-11): SyncStore now has conditional atomic cancellation for Pending/Running jobs; Cancelled jobs persist a safe reason and are excluded from retry/restart recovery. Shop-scoped idempotency, claim, succeeded guard, retry classification and redaction remain. Release build and self-contained publish verified; external tests absent and live dispatch not performed. REAL_WORK_COUNT=1, VERIFICATION_ONLY_COUNT=9.
M89 automation reliability (Issue #124, 2026-09-11): Automation claims now use unique lease tokens; completion/failure updates are conditional on token ownership, preventing stale workers from overwriting a newer run after lease expiry. Existing schedule/due/enable/retry behavior remains; Release build and self-contained publish verified, external tests absent. REAL_WORK_COUNT=2, VERIFICATION_ONLY_COUNT=8.
M90 API health (Issue #125, 2026-09-11): User cancellation is persisted as `CANCELLED` without timeout backoff; Retry-After/reset/remaining headers are range-validated while 401/403/408/429/5xx/network classifications and shop-scoped health persistence remain. Release build and self-contained publish verified; external tests absent and no live marketplace request. REAL_WORK_COUNT=2, VERIFICATION_ONLY_COUNT=8.
M91 audit/support (Issue #126, 2026-09-11): Audit search now includes marketplace/shop scope and the shared redaction chain masks email and Turkish mobile PII in audit/support output, alongside existing credential/query-secret protection. Release verification and self-contained publish completed; external tests absent and no live marketplace write. REAL_WORK_COUNT=2, VERIFICATION_ONLY_COUNT=8.
M92 backup/recovery (Issue #127, 2026-09-11): Backup manifests are now hash-verified during validation before restore; restore still stages and creates a safety backup, while retaining the pre-restore directory as a recoverable rollback point. Release verification completed; external tests absent and no live marketplace write. REAL_WORK_COUNT=3, VERIFICATION_ONLY_COUNT=7.
M93 data quality (Issue #128, 2026-09-11): Duplicate SKU/barcode findings are trim-normalized and deterministically ordered with source context; image preflight now blocks non-HTTP(S), hostless, or credential-bearing URLs as Error. Release verification completed; external tests absent and no live marketplace write. REAL_WORK_COUNT=2, VERIFICATION_ONLY_COUNT=8.
M94 release artifact (Issue #129, 2026-09-11): Added a deterministic release artifact manifest model that records win-x64/self-contained runtime, application version, relative paths, sizes, and SHA-256 hashes for published files. Release verification completed; signing certificate and external tests absent. REAL_WORK_COUNT=1, VERIFICATION_ONLY_COUNT=9.
M95 Etsy listing mapping (Issue #130, 2026-09-11): Etsy draft listing payload now uses the documented `legacy=false` transition parameter; tags reject empty/overlong values and materials enforce the documented letters/numbers/whitespace constraint. Preview, explicit approval, stale and no-live-write guards remain. Release verification completed; external tests absent. REAL_WORK_COUNT=2, VERIFICATION_ONLY_COUNT=8.
M96 Etsy dispatch (Issue #131, 2026-09-11): Update/publish dispatch now validates the SyncJob channel, shop and product identity before claiming work, preventing a valid preview from being sent under a wrong-shop or wrong-product job. Preview, approval, stale, idempotency and no-live-write guards remain. REAL_WORK_COUNT=1, VERIFICATION_ONLY_COUNT=9.
M97 Etsy order recovery (Issue #132, 2026-09-11): Batch order persistence now treats equal source timestamps as already processed, preventing a repeated receipt/event after restart from overwriting the canonical snapshot; existing shop-scoped key, pagination, exception routing and preview-only restock guards remain. Release verification completed; external tests absent and no live marketplace write. REAL_WORK_COUNT=1, VERIFICATION_ONLY_COUNT=9.
M98 operator flow (Issue #133, 2026-09-11): Data quality center now routes selected findings to the relevant products, XML, or connections screen based on source/type context, with a clear selection guard. Release verification completed; external tests absent and no live marketplace write. REAL_WORK_COUNT=1, VERIFICATION_ONLY_COUNT=9.
M99 soak/stability (Issue #134, 2026-09-11): Added a local-only deterministic StabilityProbe that measures elapsed time, allocations and working set across bounded repeated operations without marketplace calls. Release verification completed; external tests absent. REAL_WORK_COUNT=1, VERIFICATION_ONLY_COUNT=9.
