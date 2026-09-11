# Devam durumu — 2026-09-11

- [x] Ana proje .NET 8 WPF EXE olarak değiştirildi. CommerceHub korunuyor, login/parola eklenmedi.
- [x] Mevcut 217 test geçti. Aktif/pasif düzenleme ve Etsy pasif gönderim engeli zaten bulundu, tekrar yazılmadı.
- [x] Webdeki filtre davranışı masaüstüne uyarlandı: aktif/pasif/tümü, tam marka/kategori/SKU çoklu listeleri, açıklama/görsel var-yok. Parametreli SQLite, AND/OR birleşimi, sayfalama ve toplam aynı filtrede. 100 değer/6000 karakter sınırı.
- [x] Yeni filtre testi önce eksik sözleşmeyle başarısız; uygulamadan sonra 218 test geçti. Release win-x64 self-contained publish başarılı.
- [ ] Önceki normal ürün operasyon alanlarını masaüstü kartı/JSON kayıtlarıyla karşılaştırıp eksiklerini taşı; ertelenen alanları hariç tut.
- [ ] Merkezi stok rezervasyonu/idempotency ve mağaza stok/fiyat politikaları masaüstüne henüz taşınmadı. Veri modeli/işlem sınırını mevcut SQLite ve sipariş akışıyla uyumlu kur.
- [ ] Kalan normal Excel/XML/sipariş/sync ve gerçek Etsy dikey akışını tamamla. Yerel bağlantı kartı API tamamlanması değildir.

Mevcut masaüstü özellik listesi ve sınırlamalar README-YONETIM-MERKEZI.md içinde. Web özelliklerinin tamamı taşındı denmez. Yeni video transkripti çalıştırma.

## Ürün operasyon alanları — 2026-09-11
- [x] MPN, fatura adı, alt başlık, raf ve nullable son kullanma tarihi masaüstü kartına/JSON kaydına taşındı. Uzunluk doğrulaması, tarih temizleme; mevcut XML yenilemesi alanları korur. Fatura adı harici fatura servisine gönderildi sayılmaz.
- [x] Yeni sözleşme eksikliği RED; uygulama sonrası219test GREEN. Release win-x64 self-contained yayın Windows-Operations klasöründe başarılı; açık Windows-Current süreci kapatılmadı ve üzerine yazılmadı.
- [ ] Yeni operasyon alanlarının görsel WPF kullanıcı testi yapılmadı. Otomatik testler geçici SQLite kullandı; gerçek kayıtlar/test mağazası değiştirilmedi.
- [ ] Normal ürün kapsamındaki kalan alanlar ve merkezi stok/mağaza politikalarının uyarlaması devam ediyor; tüm CORE taşındı sayılmaz.

## Sipariş stok çekirdeği — 2026-09-11
- [x] Kayıtlı siparişten SKU bazlı anlık önizleme ve açık yerel stok düşümü sipariş ekranına eklendi. Ürün listesi işlemden sonra yenilenir.
- [x] catalog.db atomik ürün/stok hareketi/receipt, mağaza+sipariş duplicate koruması; değişmiş tekrar reddi, eşzamanlı oversell ve ikinci satır rollback. XML Stock kilidi satış düşümünü korur.
- [x] 15 yeni test, tüm Release234/234; Windows-Stock self-contained EXE yayınlandı. Önceki çalışan EXE kapatılmadı/üzerine yazılmadı. Canlı API yazması yok.
- [ ] Rezervasyon/depo/güvenlik stoğu, kanal stok gönderim kuyruğu ve mağaza fiyat politikaları halen kalan kapsam. Siparişlerin otomatik satış/iptal/iade stok kararı henüz yok; geçmiş kayıtlar otomatik düşürülmez.
- [ ] Sipariş stok ekranının gerçek görsel kullanıcı testi ayrıca gerekli; servis/SQLite testleri UI testi olarak sayılmaz.

## Mağaza stok politikaları — 2026-09-11
- [x] Kanal+mağaza anahtarı bazlı kalıcı ek güvenlik stoğu/üst sınır ve sürüm kontrolü. Yeni menüden yükle/kaydet/ürün listesi/önizleme. Eksik politika reddedilir; pasif üründe0, negatif gönderilebilir stok yok.
- [x] Katalog ve politika aynı SQLite snapshot içinde hesaplanır; katalog stoğu değişmez. Eksik sözleşme RED, 235 test GREEN; Windows-Policies self-contained Release publish başarılı.
- [ ] Mağaza fiyat kuralları, depo/rezervasyon, otomatik kanal gönderimi halen tamamlanmadı. Politika UI görsel testi yapılmadı; servis testleri gerçek geçici SQLite üzerinde.

Güncel EXE Windows-Policies/TrMarketplaceHubDesktop.exe. Şifre/login yok. Açık eski EXE korunmuştur; canlı mağaza verisi değişmedi.

## Entegra-3 statik analiz uyarlaması — 2026-09-11
- [x] Yetkili decompile/rapor çıktıları incelendi: 12.570 C# dosyası, iki SQLite DB, kategori/özellik cache, ürün/kanal/sipariş/XML/Excel/kargo/ayar tabloları ayrıştırıldı.
- [x] Belirsizlikler ve boş API raporu kaydedildi; özel endpoint uydurulmadı. Özgün modüler mimari ve uygulama sırası `docs/ENTEGRA3-ANALYSIS.md` ile `docs/ENTEGRA3-ARCHITECTURE.md` içinde.
- [ ] Analizden türetilen normal işlevler henüz kodlanmadı; önce doğrulanabilir yerel sözleşme ve test yazılacak. Ertelenen yedi alan kapsam dışıdır.

## Fiyat politikası — 2026-09-11
- [x] Mağaza+pazaryeri bazlı CASE/arithmetic fiyat formülü, hedef döviz, manuel kur, minimum fiyat/marj ve optimistic version kayıtları eklendi.
- [x] Pasif ürün reddi, negatif/zero formül, koruma tabanı ve yuvarlanmış önizleme; fiyat ürüne veya canlı pazaryerine yazılmaz. Windows-Price publish başarılı; mevcut testler235/235.
- [ ] Fiyat politikasının bağımsız yeni test paketi ve görsel WPF kullanıcı testi sonraki kontrolde; dış kur servisi ve canlı fiyat dispatch yok.

## Excel ekranı — 2026-09-11
- [x] WPF Ürünler/Excel işlemleri ekranı eklendi: gerçek XLSX dışa aktarma ve seçilen dosyanın önizlemesi. Hatalı satırlar raporlanır; önizleme otomatik yazma yapmaz.
- [x] Başlık adlarına göre Excel kolon eşleme eklendi; Türkçe/İngilizce yaygın başlıklar ve kolon sırası değişikliği destekleniyor.
- [x] Excel önizlemeden hatalı satır varsa yazmayı engelleyen ve geçerli satırları tek atomik katalog işleminde uygulayan akış eklendi.
- [x] Kullanıcı arayüzünden manuel kolon eşleme, seçili satır uygulama ve hatalı satırları ayrı XLSX raporu olarak dışa aktarma eklendi.
- [x] Excel uygulaması için optimistic kontrollü geri alma snapshot/journal akışı eklendi; katalog sonradan değiştiyse geri alma güvenli biçimde reddediliyor.
- [x] 244 test geçti; Windows-Excel-Complete self-contained publish başarılı.
- [ ] Kategori, marka ve özellik yönetimi/eşleme sonraki ana modüldür.

## Kategori / marka / özellik ve Sync — 2026-09-11
- [x] Yerel kategori, marka ve özellik kayıtları; normalize edilmiş ad/değer doğrulaması ve harici anahtar eşlemesi eklendi.
- [x] WPF kategori/marka/özellik yönetim ekranı eklendi.
- [x] Idempotent sync kuyruğu, hata kaydı, üç deneme sınırı ve tekrar kuyruğa alma eklendi.
- [x] WPF Sync merkezi eklendi; canlı marketplace yazımı yapılmıyor.
- [x] Kalıcı otomasyon zamanlama çekirdeği eklendi: tekil lease, due kontrolü, tamamlanınca sonraki çalışma ve hata sonrası yeniden deneme zamanı.
- [x] 251 test geçti; Windows-Core-249 self-contained publish başarılı.
- [x] Otomasyon çekirdeği stok/fiyat politikası önizlemelerine bağlandı; katalog değiştirmeden idempotent sync işi üretir ve politika hatalarını retry edilebilir kayda çevirir.
- [x] 253 test geçti; Windows-Automation-Policies self-contained publish başarılı.
- [ ] Otomasyon işlerinin WPF zamanlama ayarlarına ve gerçek connector dispatch katmanına bağlanması sonraki adımdır.

## M1 Etsy dispatch zamanlayıcısı — 2026-09-11
- [x] WPF otomasyon yönetimi: stok/fiyat türü, kanal/mağaza, aralık, etkinlik, next/last run, kilit ve son hata görünümü.
- [x] Zamanlayıcı açıkken due otomasyon işleri çalıştırılıp idempotent sync kuyruğuna alınır; Etsy işleri gerçek listing ID eşleşmesi olmadan başarısız kaydedilir.
- [x] Etsy dispatch için somut önizleme, ürün sürümü doğrulaması ve açık kullanıcı onayı eklendi; onaysız HTTP PATCH yok.
- [x] Testler ve self-contained Windows-M1-Etsy yayın doğrulaması tamamlandı.
- [ ] Canlı Etsy credential/scope olmadan production dispatch doğrulanamaz; gerçek mağazada otomatik test çalıştırılmadı.

## PR #4 review düzeltmesi — 2026-09-11
- [x] Etsy dispatch SyncJob ile bağlandı: başarılı HTTP sonucu Succeeded, istisna/HTTP hatası Failed.
- [x] Succeeded sync işi ikinci kez canlı gönderilmiyor; Pending -> Running atomik geçişi kullanılıyor.
- [x] Preview ürün sürümü/stok/fiyat/döviz değişirse dispatch reddediliyor.
- [x] Fake HTTP + geçici SQLite idempotency/status testi eklendi; toplam 259 Release testi geçti.

## M2 Etsy ürün/sipariş stok karar akışı — 2026-09-11
- [x] Resmi Etsy listing detay okuma (`GET listings/{listing_id}`) eklendi; yanıt şeması doğrulanıyor.
- [x] Etsy satış stok kararı preview → açık onay → atomik mevcut CatalogStore receipt/movement akışına bağlandı.
- [x] Aynı receipt ikinci kez stok düşmüyor; iptal/iade için otomatik stok geri koyma yok.
- [x] Eksik SKU, bilinmeyen Etsy siparişi ve pasif/uygunsuz ürün durumlarında işlem reddediliyor.
- [x] 261 Release testi geçti; M2 stacked branch publish doğrulanacak.
- [ ] Etsy gerçek mağaza credential/scope olmadan canlı sipariş yazma doğrulanamaz (`LIVE_API_BLOCKED`).

## M3 eBay güvenli operasyon paritesi — 2026-09-11
- [x] Resmi eBay Inventory ve Fulfillment read/write yolları için mevcut OAuth bağlantısına API istemci metotları eklendi.
- [x] eBay stok preview, açık onay, stale sürüm ve SyncJob idempotency kapısı eklendi.
- [x] Başarılı/başarısız eBay dispatch SyncStore durumuna bağlandı; fake HTTP + SQLite testleri eklendi.
- [x] 263 Release testi geçti; self-contained yayın hazırlanıyor.
- [ ] eBay uygulamasında gerekli production scope/credential doğrulaması ortam bağımlı (`LIVE_API_BLOCKED`).

## M4 ortak marketplace adapter çekirdeği — 2026-09-11
- [x] Ozon/Joom/Allegro/Wish/Navlungo için ortak adapter sözleşmesi ve capability modeli eklendi.
- [x] Kanal+mağaza izole yerel eşleme store'u eklendi; desteklenmeyen operasyon HTTP üretmeden reddediliyor.
- [x] Ortak preview/onay ve `LIVE_API_BLOCKED` davranışı ile fake adapter/SQLite testleri eklendi.
- [x] 265 Release testi geçti; Windows-M4-Adapters self-contained publish başarılı.
- [ ] Bu beş kanal için resmi production endpoint/credential doğrulaması yapılmadı; canlı yazma `LIVE_API_BLOCKED`.

## Etsy operasyonları — 2026-09-11
- [x] Etsy resmi API istemcisine basit ilan stok/fiyat güncellemesi eklendi; `listings_w` yetkisi, PATCH ve resmi API kimlik başlıkları kullanılıyor.
- [x] Geçersiz stok/fiyat/ilan kimliği için istek göndermeyen testler eklendi.
- [x] 256 test geçti; Windows-Etsy-Operations self-contained publish başarılı.
- [ ] Etsy otomasyon sync işlerini gerçek ilan eşlemesi ve kullanıcı onaylı dispatch ekranına bağlamak gerekiyor.
- [ ] Etsy ürün detay okuma ve siparişden yerel stok karar/iptal-iade akışı sonraki adımdır.
- [x] İlan stok/fiyat dispatch preview sözleşmesi eklendi; ilan kimliği, ürün sürümü, stok/fiyat değişmezliği ve açık onay doğrulanıyor.
- [x] 257 test geçti; Windows-Etsy-Preview self-contained publish başarılı.

## M5 XML tedarikçi merkezi — 2026-09-11
- [x] Mevcut XML ekranı, güvenli HTTP/XML okuma, şifreli kimlik bilgisi, alan eşleme, filtre ve önizleme akışları korundu.
- [x] HTTP gzip içerik desteği eklendi; XML boyutu, timeout, HTTPS/file ve DTD güvenlik sınırları korunuyor.
- [x] Manuel ve zamanlanmış içe aktarmalar `XmlRuns` tablosunda Running/Succeeded/Failed geçmişi ve sonuç sayaçlarıyla kaydediliyor.
- [x] 266 Release testi geçti; self-contained yayın `Windows-M5-XML` altında üretildi.
- [ ] XML varyant mapping `DEFERRED_BY_USER`; normal XML dışı tedarikçi API'leri bu fazın kapsamında değil.

## M6 mağaza ve bağlantı merkezi — 2026-09-11
- [x] Etsy, eBay, Amazon, Trendyol, Hepsiburada, Allegro, Ozon, Joom, Wish, Fruugo ve Navlungo için kanal kataloğu ve varsayılan mağaza kayıtları eklendi.
- [x] Kanal+mağaza bazlı etkinlik, görünen ad, durum, son test ve hata geçmişi `MarketplaceConnections` tablosunda tutuluyor; çoklu mağaza kaydı destekleniyor.
- [x] Gizli bilgiler yeni tabloya yazılmıyor; mevcut DPAPI/CredentialStore ayarları okunuyor ve hata metinleri maskeleniyor.
- [x] Salt okunur Etsy/eBay/Ozon bağlantı testleri eklendi; doğrulanmamış kanallar `LIVE_API_BLOCKED` olarak işaretleniyor.
- [x] Yeni WPF Mağaza bağlantıları paneli ve kanal/yardım hızlı geçişleri eklendi.
- [x] 268 Release testi geçti; self-contained yayın `Windows-M6-Connections` altında üretildi.
- [ ] Amazon, Trendyol, Hepsiburada, Allegro, Joom, Wish, Fruugo ve Navlungo için doğrulanmış resmi API credential/contract olmadan canlı operasyon açılmadı.

## M7 ürün yönetimi ana paneli — 2026-09-11
- [x] Ürün listesine XML kaynak kimliği, son güncelleme zamanı ve kanal planı özeti eklendi; mevcut SKU/barkod/marka/kategori/stok/fiyat filtreleri korundu.
- [x] Filtreler SQLite içinde isimli görünüm olarak kaydedilebilir, yüklenebilir ve silinebilir.
- [x] Büyük listede 200 kayıt sayfalama + WPF satır sanallaştırması etkinleştirildi; detay paneli seçili ürünün normal alanlarını ve kanal planlarını gösteriyor.
- [x] Çoklu seçimle aktif/pasif normal toplu işlem eklendi; canlı marketplace yazımı yapılmıyor.
- [x] 269 Release testi geçti; self-contained yayın `Windows-M7-Products` altında üretildi.
- [ ] Varyant, bundle/set, hızlı satır içi düzenleme ve kritik fiyat bu kullanıcı kararıyla kapsam dışıdır.

## M8 sipariş merkezi — 2026-09-11
- [x] Birleşik sipariş listesine kanal, mağaza, sipariş durumu, stok kararı, kargo/tracking ve son alım filtreleri eklendi; sanallaştırmalı liste korundu.
- [x] Ortak `OrderStockDecisionService` ile satış stok önizleme/onay/idempotency ve eksik SKU reddi tüm yerel siparişlere bağlandı.
- [x] İptal/iade otomatik stok geri koymuyor; kargo/tracking yerel gözlem sınırında ve Fulfillment Core kapsamına girmiyor.
- [x] 271 Release testi geçti; self-contained yayın `Windows-M8-Orders` altında üretildi.
- [ ] Etsy dışı sipariş API'leri için doğrulanmış credential/contract olmadan otomatik çekme açılmadı.

## M9 Amazon connector sınırı — 2026-09-11
- [x] Amazon Seller ID, LWA ve SP-API region/marketplace ayarları doğrulamalı model ve mevcut DPAPI ile şifreli store olarak eklendi.
- [x] Amazon bağlantı sekmesi ve yerel ürün planı erişimi eklendi; connection center Amazon kartına bağlandı.
- [x] Resmi region/credential sözleşmesi doğrulanmadığı için read-only test bilinçli olarak `LIVE_API_BLOCKED` döndürüyor; endpoint/scope uydurulmadı.
- [x] 273 Release testi geçti; self-contained yayın `Windows-M9-Amazon` altında üretildi.
- [ ] Amazon SP-API production read/write, credential onayı ve gerçek mağaza testi resmi bilgiler sağlanana kadar kapalı.

## M10 Trendyol connector sınırı — 2026-09-11
- [x] Supplier ID, API key/secret ve User-Agent doğrulamalı model, DPAPI şifreli store ve WPF bağlantı paneli eklendi.
- [x] Trendyol kanal kartı/yerel ürün planı ve connection center hızlı geçişi eklendi.
- [x] Resmi endpoint/scope sözleşmesi doğrulanmadığı için salt okunur test `LIVE_API_BLOCKED` döndürüyor; sahte HTTP çağrısı yok.
- [x] 275 Release testi geçti; self-contained yayın `Windows-M10-Trendyol` altında üretildi.
- [ ] Trendyol production ürün/sipariş/stok/fiyat operasyonları resmi credential ve güncel API sözleşmesi doğrulanana kadar kapalı.

## M11 Hepsiburada connector sınırı — 2026-09-11
- [x] Merchant ID, API kullanıcı adı/şifresi ve User-Agent için doğrulamalı model, DPAPI şifreli store ve WPF bağlantı paneli eklendi.
- [x] Hepsiburada kanal kartı/yerel ürün planı ve connection center hızlı geçişi eklendi.
- [x] Resmi endpoint/scope sözleşmesi doğrulanmadığı için salt okunur test `LIVE_API_BLOCKED` döndürüyor; sahte HTTP çağrısı yok.
- [x] 277 Release testi geçti; self-contained yayın `Windows-M11-Hepsiburada` altında üretildi.
- [ ] Hepsiburada production ürün/sipariş/stok/fiyat operasyonları resmi credential ve güncel API sözleşmesi doğrulanana kadar kapalı.

## M12 Fruugo connector sınırı — 2026-09-11
- [x] Retailer ID, kullanıcı adı/şifre için doğrulamalı model, DPAPI şifreli store ve WPF bağlantı paneli eklendi.
- [x] Fruugo kanal kartı/yerel ürün planı ve connection center hızlı geçişi eklendi.
- [x] Ürün/sipariş API sözleşmesi doğrulanmadığı için salt okunur test `LIVE_API_BLOCKED` döndürüyor; sahte HTTP çağrısı yok.
- [x] 279 Release testi geçti; self-contained yayın `Windows-M12-Fruugo` altında üretildi.
- [ ] Fruugo production ürün/sipariş/stok/fiyat operasyonları retailer kabulü ve resmi API sözleşmesi doğrulanana kadar kapalı.

## M13 sync, otomasyon ve hata merkezi — 2026-09-11
- [x] Sync işi listesi; arama/durum filtreleri, kanal/işlem/varlık/sürüm, deneme sayısı, hata sınıfı ve güncelleme zamanı gösterimi eklendi.
- [x] Network/auth/mapping/validation/rate-limit/stale/idempotency/unsupported sınıflandırması ve retry edilebilir sınıf kapısı eklendi; 3 deneme sınırı korundu.
- [x] Sync merkezi içinde otomasyon listesi ve XML çalıştırma geçmişi sekmeleri birleştirildi; büyük listelerde satır sanallaştırması kullanılıyor.
- [x] Sync hata metinleri maskeleniyor; access/refresh token, API key/secret gibi değerler kalıcı hata kaydına yazılmıyor.
- [x] 281 Release testi geçti; self-contained yayın `Windows-M13-Sync` altında üretildi.
- [ ] Gerçek kanal dispatch'i yalnız doğrulanmış adapter/credential ve açık önizleme/onay sınırlarında; Fulfillment ve ertelenen alanlar kapsam dışı.

## M14 Ozon connector — 2026-09-11
- [x] Mevcut Ozon DPAPI ayarı, ürün sayısı ve depo okuma akışı ortak bağlantı panelinden korunarak kullanılıyor.
- [x] Resmi Seller API ürün listeleme (`/v3/product/list`) için sayfalı read-only ürün özeti ve yanıt doğrulaması eklendi.
- [x] HTTP 401/403/429, timeout ve hatalı JSON davranışları mevcut güvenli hata sınırında; API key yanıt/loglara sızmıyor.
- [x] 282 Release testi geçti; self-contained yayın `Windows-M14-Ozon` altında üretildi.
- [ ] Ozon sipariş, stok/fiyat yazma ve ürün yayınlama için doğrulanmış sözleşme/credential akışı bu fazda açılmadı; desteklenmeyen işlemler `LIVE_API_BLOCKED`.

## M15 Allegro connector — 2026-09-11
- [x] Allegro OAuth bilgileri DPAPI ile saklanan mağaza paneline bağlandı; çoklu mağaza metadata korunuyor.
- [x] Resmi public API `GET /sale/offers` ve `GET /order/checkout-forms` read-only operasyonları, sayfalama ve JSON doğrulaması eklendi.
- [x] 401/403/429/timeout/bozuk yanıt sınırları mevcut hata merkezine uyumlu; yazma ve fulfillment metotları yok.
- [x] 284 Release testi geçti; self-contained yayın `Windows-M15-Allegro` altında üretildi.
- [ ] OAuth refresh akışı ve stok/fiyat/listing write için açık preview/onay/idempotent dispatch sonraki resmi credential doğrulamasına bağlı.

## M16 Joom connector sınırı — 2026-09-11
- [x] Merchant ID/API key için DPAPI şifreli ayar store'u, bağlantı paneli ve yerel ürün planı bağlantısı eklendi.
- [x] Joom API sözleşmesi/kimlik akışı doğrulanmadığı için read-only test `LIVE_API_BLOCKED` döndürüyor; endpoint uydurulmadı.
- [x] 286 Release testi geçti; self-contained yayın `Windows-M16-Joom` altında üretildi.
- [ ] Joom ürün/sipariş/stok/fiyat canlı operasyonları resmi credential ve güncel API sözleşmesi doğrulanana kadar kapalı.

## M17 Wish connector sınırı — 2026-09-11
- [x] Merchant ID/API key için DPAPI şifreli store, WPF bağlantı paneli, Wish kanal kartı ve yerel ürün planı eklendi.
- [x] Güncel Wish API kimlik/endpoint sözleşmesi doğrulanmadığı için salt okunur test `LIVE_API_BLOCKED` döndürüyor; sahte HTTP yok.
- [x] 288 Release testi geçti; self-contained yayın `Windows-M17-Wish` altında üretildi.
- [ ] Wish ürün/sipariş/stok/fiyat canlı operasyonları resmi credential ve API sözleşmesi doğrulanana kadar kapalı.

## M19 ana dashboard ve rapor/bildirim merkezi — 2026-09-11
- [x] Program açılışında ürün, sipariş, stok, XML, sync ve mağaza bağlantı özetlerini gösteren özgün Genel Bakış ekranı eklendi.
- [x] Kanal/mağaza sağlığı, son test ve maskeli hata görünümü eklendi; varsayılan/engelli hesaplar ilgili kanal ekranına açılıyor.
- [x] Son 14 gün yerel sipariş trendi ve mevcut stok özeti eklendi; ağır BI veya uzak rapor API'si eklenmedi.
- [x] Sync, XML, stok ve bağlantı bildirimleri ilgili ekranlara yönlendiren aksiyonlarla birleştirildi; veri okuma arka planda yapılıyor.
- [x] 290 Release testi geçti; self-contained yayın `Windows-M19-Dashboard` altında üretildi.
- [ ] Canlı marketplace mutation ve fulfillment dashboard işlemlerinden açılmadı; dashboard salt-okunur yerel özet olarak kalır.

## M20 Windows kurulum, yedekleme ve taşıma — 2026-09-11
- [x] Self-contained `win-x64` paket için publish/build ve kurulum-kaldırma PowerShell akışı eklendi; kaldırma LocalAppData kullanıcı verisini silmiyor.
- [x] Uygulama sürümü `1.0.0` ve yerel kurulum manifesti gösteriliyor; doğrulanmamış uzak güncelleme endpoint'i çağrılmıyor.
- [x] Veritabanları, ayar dosyaları ve DPAPI ile şifreli credential byte'ları manifest + SHA-256 doğrulamalı zip yedeğine alınabiliyor.
- [x] Geri yükleme geçici staging, mevcut veri için güvenlik yedeği, doğrulama ve atomik klasör değişimiyle uygulanıyor; hata halinde eski klasör korunuyor.
- [x] Ayarlar ekranına kullanıcı onaylı yedek/geri yükleme ve veri klasörü açma paneli eklendi; 292 Release testi geçti.
- [ ] Çalışan uygulama dosya kilitleri nedeniyle geri yükleme başarısız olursa uygulama kapatılıp tekrar açılmalı; installer uzak otomatik güncelleme yapmaz.

## M21 üretim öncesi doğrulama ve sertleştirme — 2026-09-11
- [x] XML/Excel → katalog → kanal planı → sync → sipariş → onaylı stok kararı akışı için mevcut test matrisi ve üretim öncesi kontrol dokümanı eklendi.
- [x] XML ve otomasyon geçmişi credential desenlerini maskeliyor; repo hata saklama yolları tekrar gözden geçirildi.
- [x] Kanal/mağaza izolasyonu, idempotency, stale preview, retry, transaction/oversell ve unsupported HTTP sınırları Release testleriyle korunuyor.
- [x] `installer/Smoke-Test.ps1`, Release testleri, self-contained publish ve installer smoke testi çalıştırıldı; 294 Release testi geçti.
- [x] `docs/PRODUCTION-READINESS.md` ile canlı, bloklu ve ertelenmiş kapsam açıklandı.
- [ ] Gerçek marketplace credential/API sözleşmesi olmayan kanallarda LIVE operasyon hâlâ açılmadı.

## M22 tedarikçi / XML kaynak merkezi — 2026-09-11
- [x] Kaynak ekranına sağlık kontrolü eklendi: HTTPS/yerel erişim, HTTP durumları, timeout, 25 MB sınırı, gzip ve XML alan taraması gösteriliyor.
- [x] Kaynak çoğaltma akışı mapping/fiyat/stok kurallarını taşırken Basic Auth credential'ını kopyalamıyor; yeni kaynağın yetkilendirmesi ayrı tutuluyor.
- [x] Kaynak bazlı ürün sayısı ve son çalışma create/update/skip özeti gösteriliyor; XML geçmişi kalıcı kalıyor.
- [x] Aynı kaynak için eşzamanlı Running XML import `XmlRunStore` tarafından reddediliyor; mevcut snapshot/LockStock davranışı korunuyor.
- [x] 295 Release testi geçti; self-contained yayın `Windows-M22-XML-Center` altında üretildi.
- [ ] XML Variant Mapping ve diğer `DEFERRED_BY_USER` alt özellikleri kapsam dışıdır.

## M23 kategori / marka / özellik eşleme merkezi — 2026-09-11
- [x] Yerel kategori, marka ve özellik sözlükleri tek WPF merkezinde kanal ve mağaza bağlamıyla yönetiliyor.
- [x] Eşleme listesi aranabilir; `MISSING`, `STALE` ve `INVALID` durumları görünür; pasif yerel kayıt eşlenemiyor.
- [x] İsim normalizasyonu yalnız öneri üretir; toplu eşleme önizleme ve kullanıcı onayı olmadan veritabanına yazılmaz.
- [x] Mapping geçmişi tutuluyor ve aynı harici anahtar farklı kanal/mağazalarda birbirinden izole ediliyor.
- [x] 298 Release testi geçti; self-contained yayın `Windows-M23-Taxonomy` altında üretilecek.
- [ ] Resmi marketplace metadata endpointleri doğrulanmadan kategori/özellik/marka uzaktan yazımı açılmayacak; XML Variant Mapping `DEFERRED_BY_USER`.

## M24 ürün görsel ve medya merkezi — 2026-09-11
- [x] Ürün görselleri ayrı `media.db` içinde URL, kaynak, sıra, ana görsel, hash, son doğrulama ve hata durumu ile tutuluyor; mevcut `ImageUrls` verisi ilk açılışta içeri alınıyor.
- [x] HTTPS/file URL normalizasyonu ve ürün bazlı duplicate engeli; ana görsel silinirse sıradaki kayıt otomatik yükseltiliyor.
- [x] Tek WPF panelinde ürün arama, kaynak görünürlüğü, önizleme, ana görsel/sıra işlemleri ve kaydı kaldırma mevcut.
- [x] 20 MB sınırı, timeout, 404, geçersiz adres, desteklenmeyen biçim ve HTTP hata sınıfları; doğrulama/retry yalnız dosya okur, canlı marketplace yazmaz.
- [x] Fake HTTP ve geçici SQLite senaryoları ile 301 Release testi geçti; self-contained yayın `Windows-M24-Media` altında üretilecek.
- [ ] Marketplace görsel yazımı bu modülde açılmadı; yalnız ilgili connector'ın mevcut preview/onay kapısından geçebilir.

## M25 Excel şablon ve gelişmiş aktarım merkezi — 2026-09-11
- [x] SQLite tabanlı Excel profilleri: kolon eşlemeleri, başlık alias'ları, kültür, varsayılan değerler ve dışa aktarım görünür alanları kaydediliyor.
- [x] Profil önizlemesi SKU/barkod üzerinden `CREATE`, `UPDATE`, `SKIP` ve `ERROR` kararlarını satır bazında gösteriyor.
- [x] Filtrelenmiş ürün seti ve profil alanlarıyla dışa aktarım; hatalı satırlar ayrı Excel raporu; geçerli satırlar mevcut atomik/undo akışına gidiyor.
- [x] Gerçek XLSX fixture + geçici SQLite testleri ile 303 Release testi geçti; self-contained yayın `Windows-M25-Excel` altında üretilecek.
- [ ] Varyant/bundle ve diğer `DEFERRED_BY_USER` alanları Excel profiline eklenmedi.

## M26 kanal yayın durumu ve ürün listeleme matrisi — 2026-09-11
- [x] Ürün × kanal × mağaza matrisi; yerel plan/listing ID, mapping durumu, sync sonucu, son hata ve bağlantı sağlığı gösteriliyor.
- [x] `MISSING`, `ERROR`, `STALE`, `AUTH_ERROR`, `PENDING`, `SYNCED` ve `DRAFT` filtreleri ile SKU/kanal/mağaza araması eklendi.
- [x] Ürün ve seçili kanal ekranına hızlı geçiş; capability listesi doğrulanmamış yazımı başarı gibi göstermeden görünür.
- [x] `ChannelProductsStore` kanal/mağaza izolasyonlu listeleme sağlıyor; geçici SQLite testleri ile 305 Release testi geçti; self-contained yayın `Windows-M26-Listing-Matrix` altında üretilecek.
- [ ] Toplu canlı yayın yok; connector preview/onay kapısı ve kullanıcı ertelemeleri korunuyor.
