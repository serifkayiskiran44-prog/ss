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
