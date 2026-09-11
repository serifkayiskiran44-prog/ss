# Devam durumu — 2026-09-11

- [x] Ana proje .NET 8 WPF EXE olarak değiştirildi. CommerceHub korunuyor, login/parola eklenmedi.
- [x] Mevcut 217 test geçti. Aktif/pasif düzenleme ve Etsy pasif gönderim engeli zaten bulundu, tekrar yazılmadı.
- [x] Webdeki filtre davranışı masaüstüne uyarlandı: aktif/pasif/tümü, tam marka/kategori/SKU çoklu listeleri, açıklama/görsel var-yok. Parametreli SQLite, AND/OR birleşimi, sayfalama ve toplam aynı filtrede. 100 değer/6000 karakter sınırı.
- [x] Yeni filtre testi önce eksik sözleşmeyle başarısız; uygulamadan sonra 218 test geçti. Release win-x64 self-contained publish başarılı.
- [ ] Önceki normal ürün operasyon alanlarını masaüstü kartı/JSON kayıtlarıyla karşılaştırıp eksiklerini taşı; ertelenen alanları hariç tut.
- [ ] Merkezi stok rezervasyonu/idempotency ve mağaza stok/fiyat politikaları masaüstüne henüz taşınmadı. Veri modeli/işlem sınırını mevcut SQLite ve sipariş akışıyla uyumlu kur.
- [ ] Kalan normal Excel/XML/sipariş/sync ve gerçek Etsy dikey akışını tamamla. Yerel bağlantı kartı API tamamlanması değildir.

## M66 performans/concurrency/restart — 2026-09-11
- [x] Sipariş SQLite sayfalaması önceki branch’te SQL filtre/count/LIMIT/OFFSET kullanıyor; WAL, busy-timeout ve sorgu indeksleri eklendi.
- [x] SyncStore atomik Pending claim’i korunarak stale Running işleri restart sonrası güvenli biçimde Pending’e alan `RecoverAbandonedRunning` eklendi.
- [x] Release build ve self-contained Windows-M66-Performance publish başarılı.
- [ ] Harici test projesinde hedefli restart/concurrency test paketi bu klonda bulunmadığı için eklenemedi; gerçek test sayısı bu nedenle doğrulanamadı.

## M67 navigasyon/erişilebilirlik/global arama — 2026-09-11
- [x] Global arama için bağımsız debounce timer, cancellation ve stale-result revision koruması eklendi.
- [x] Ana pencere, menü araması, global arama ve geri düğmesine erişilebilir adlar eklendi.
- [x] Release build ve self-contained `Windows-M67-UX-Search` publish başarılı.
- [ ] Harici UI smoke/test projesi bu klonda bulunmuyor; gerçek kullanıcı arayüzü testi yapılamadı.

## M68 Etsy OAuth/read güvenliği — 2026-09-11
- [x] OAuth scope sözleşmesi tekil required-scope setiyle görünür hale getirildi.
- [x] Token expiry için bir dakikalık güvenlik payı ve gerektiğinde refresh API’si eklendi.
- [x] Release build ve self-contained `Windows-M68-Etsy-OAuth` publish başarılı.
- [ ] Gerçek credential/mağaza read testi yapılmadı; canlı ortam `LIVE_API_BLOCKED`.

## M69 Etsy listing üretim akışı — 2026-09-11
- [x] Etsy readiness geçidi artık süresi dolmuş/dolmak üzere olan token’ı canlı işlem öncesi BLOCKED gösteriyor.
- [x] Mevcut resmi listing create/update/publish preview, stale, wrong-shop ve idempotency kapıları korundu.
- [x] Release build ve self-contained `Windows-M69-Etsy-Production` publish başarılı.
- [ ] Gerçek mağazada write/order testi yapılmadı (`LIVE_API_BLOCKED`).

## M70 final QA/release — 2026-09-11
- [x] Installer smoke EXE yanında runtimeconfig/deps metadata varlığını da doğruluyor.
- [x] Release runbook eklendi; canlı marketplace write ve gerçek credential testleri yapılmadı.
- [x] Release build, publish ve installer smoke başarılı.
- [ ] Harici test projesi bu klonda bulunmadığı için test sayısı doğrulanamadı.

## M71 startup/shutdown/recovery — 2026-09-11
- [x] Atomic startup marker ve unclean-exit özeti eklendi.
- [x] Kapanışta lifetime cancellation önce çalışıyor; temiz kapanış marker’ı kaldırıyor.
- [x] Release build ve self-contained `Windows-M71-Startup-Recovery` publish başarılı.
- [ ] Harici test projesi bu klonda yok; crash/restart otomatik testi yapılamadı.

## M72 SQLite schema/migration — 2026-09-11
- [x] Catalog, sync ve orders store’larında ortak `quick_check`/`user_version` schema gate’i eklendi.
- [x] Unsupported newer schema için güvenli fail ve transaction içi version set’i eklendi.
- [x] Release build ve self-contained `Windows-M72-Schema` publish başarılı.
- [ ] Harici test projesi bu klonda yok; eski fixture/disk dolu/izin senaryoları otomatik çalıştırılamadı.

## M73 katalog CRUD/integrity — 2026-09-11
- [x] Product update’te non-empty SKU/barkod duplicate kontrolü optimistic transaction içine alındı.
- [x] Mevcut required-field, stale/version, provenance ve safe-delete korumaları korundu.
- [x] Release build ve self-contained `Windows-M73-Catalog-Integrity` publish başarılı.
- [ ] Harici test projesi bu klonda yok; CRUD regression test sayısı doğrulanamadı.
 
## M74 provenance — 2026-09-11
- [x] Ürünlerde kaynak türü/zamanı ve bağımsız fiyat-stok-medya kaynak alanları eklendi.
- [x] XML/Excel import kaynak damgası ve ürün gridinde kaynak görünürlüğü eklendi.
- [x] Release build ve self-contained `Windows-M74-Provenance` publish başarılı.
- [ ] Harici test projesi bu klonda yok; provenance regression testleri çalıştırılamadı.

## M75 search/filter — 2026-09-11
- [x] Query ve multi-value filter input trim/empty-safe oldu.
- [x] Exact Brand/Category/SKU filtreleri için SQLite expression index’leri eklendi.
- [x] Release build ve self-contained `Windows-M75-Search-Filter` publish başarılı.
- [ ] Harici test projesi bu klonda yok; fixture/performance testleri çalıştırılamadı.

## M76 XML parser — 2026-09-11
- [x] Malformed XML ve reader argument hataları anlaşılır/güvenli import hatasına dönüştürüldü.
- [x] DTD/resolver/size/row/namespace güvenlik sınırları korundu.
- [x] Release build ve self-contained `Windows-M76-XML-Parser` publish başarılı.
- [ ] Harici test projesi bu klonda yok; XML fixture regression testleri çalıştırılamadı.

## M77 XML scheduler — 2026-09-11
- [x] Aynı XML kaynağı için tek Running çalıştırma SQLite partial unique index ile atomik hale getirildi.
- [x] Paralel yarış sonucu güvenli duplicate-run hatasına dönüştürüldü.
- [x] Release build ve self-contained `Windows-M77-XML-Scheduler` publish başarılı.
- [ ] Harici test projesi bu klonda yok; restart/concurrency fixture testleri çalıştırılamadı.

## M78 Excel import — 2026-09-11
- [x] Excel undo/apply akışı provenance alanlarını koruyor.
- [x] 100 MB üzeri XLSX dosyaları parser öncesi reddediliyor.
- [x] Release build ve self-contained `Windows-M78-Excel-Import` publish başarılı.
- [ ] Harici test projesi bu klonda yok; XLSX fixture/cancellation testleri çalıştırılamadı.

## M79 Excel export — 2026-09-11
- [x] Export alanlarına ürün provenance sütunları eklendi.
- [x] Ürün ve hata XLSX raporları temporary-file atomic replace kullanıyor.
- [x] Release build ve self-contained `Windows-M79-Excel-Export` publish başarılı.
- [ ] Harici test projesi bu klonda yok; büyük veri/cancellation smoke testi çalıştırılamadı.

## M80 taxonomy — 2026-09-11
- [x] Harici taxonomy key doğrulaması boş/uzun/kontrol karakterli değerleri reddediyor.
- [x] Mağaza izolasyonu, suggestion-only ve history davranışları korundu.
- [x] Release build ve self-contained `Windows-M80-Taxonomy` publish başarılı.
- [ ] Harici test projesi bu klonda yok; taxonomy regression testleri çalıştırılamadı.

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

## M27 sipariş istisna, iptal ve iade karar merkezi — 2026-09-11
- [x] Eksik/ambiguous SKU ve iptal/iade olayları için kalıcı SQLite istisna kuyruğu; kanal/mağaza/sipariş/tür/öncelik/durum/yaşlandırma filtreleri.
- [x] Sipariş taraması aynı olay anahtarını upsert eder; sorunlar kaybolmaz ve tekrar taramada duplicate kayıt büyümez.
- [x] İptal/iade için mevcut stok receipt'inden somut geri koyma önizlemesi; kullanıcı onayı olmadan hareket yok.
- [x] Geri koyma transaction + ürün sürümü kontrolü + tek sipariş idempotency kaydı ile stale/duplicate stok hareketi engellendi.
- [x] 308 Release testi geçti; self-contained yayın `Windows-M27-Order-Exceptions` altında üretilecek.
- [ ] Fulfillment, hakediş/mutabakat ve otomatik canlı iptal/iade API yazımı kapsam dışı.

## M28 tanılama, audit trail ve destek merkezi — 2026-09-11
- [x] Kritik UI/işlem kayıtları için credential/PII redaction ve 5000 kayıt retention sınırı olan yerel `AuditStore` eklendi.
- [x] DB, migration/okuma, sync kuyruğu, son hata ve disk durumu `DiagnosticsService` ile salt-okunur özetleniyor.
- [x] Tanılama/audit ekranı; arama, yenileme, destek paketi dışa aktarma ve ilgili merkezlere geçiş içeriyor.
- [x] Destek zip'i yalnız maskeli metadata, audit ve son operasyon logunu içeriyor; yerel DB/şifreli credential byte'ları dahil edilmiyor.
- [x] 311 Release testi geçti; self-contained yayın `Windows-M28-Diagnostics` altında üretildi.
- [ ] Canlı marketplace değişikliği, credential çözme veya otomatik kurtarma bu merkezden açılmadı.

## M29 gezinme, kayıtlı görünümler ve UX — 2026-09-11
- [x] Sol menü araması, son route tercihi, breadcrumb ve geri bağlamı eklendi.
- [x] Ürün/sipariş/sync/XML için yerel kayıtlı görünüm profilleri; ürün kolon görünürlüğü tercihi eklendi.
- [x] Ctrl+K global arama (SKU, barkod, sipariş, listing, mağaza) ve temel klavye kısayolları eklendi.
- [x] Boş arama, route geri dönüşü ve mevcut preview/onay/iptal kapıları korunuyor; canlı marketplace mutation yok.
- [x] UiPreferenceStore için 314 Release testi geçti; self-contained yayın `Windows-M29-Navigation` altında üretildi.
- [ ] Görsel WPF smoke testleri gerçek masaüstü oturumu gerektirdiği için otomatikleştirilmedi; veri/tercih akışları test edildi.

## M30 mesaj ve müşteri iletişim merkezi — 2026-09-11
- [x] Kanal/mağaza/sipariş/ürün/müşteri/konu/durum/zaman filtreli yerel mesaj merkezi eklendi.
- [x] Harici ID duplicate koruması, okundu/taslak/hata durumları ve PII/secret-safe audit sınırı eklendi.
- [x] Yerel mesaj şablonları ve sipariş/ürün ekranına bağlam geçişleri eklendi.
- [x] Doğrulanmış mesaj capability'si olmayan kanallarda okuma/yazma `NOT_SUPPORTED/LIVE_API_BLOCKED`; HTTP isteği yok.
- [x] Fake SQLite/capability/redaction testleri ile 317 Release testi geçti; self-contained yayın `Windows-M30-Messages` altında üretildi.
- [ ] Gerçek marketplace inbox/mesaj API'leri resmi capability/scope doğrulanana kadar açılmayacak.

## M31 güvenli toplu ürün işlemleri — 2026-09-11
- [x] Seçili/arama sonucundaki ürünlerde aktif/pasif, kategori, marka, normal ad/açıklama ve yerel kanal planı işlemleri eklendi.
- [x] Satır bazlı READY/SKIP/ERROR preview, eski/yeni değer, kilit ve zorunlu alan doğrulaması eklendi.
- [x] Preview sonrası `UpdatedUtc` stale kontrolü ve CatalogProducts transaction ile kısmi yazma engellendi.
- [x] Büyük seçimlerde ilerleme/iptal, audit özeti ve canlı connector'a çıkmayan kanal planı akışı eklendi.
- [x] Geçici SQLite/stale/kanal planı testleri ile 320 Release testi geçti; self-contained yayın `Windows-M31-Bulk-Products` altında üretildi.
- [ ] Toplu canlı marketplace write yok; her connector'ın mevcut preview/onay akışı korunuyor.

## M32 stok ve fiyat politika yönetim merkezi — 2026-09-11
- [x] Kanal/mağaza stok ve fiyat policy listeleri, etkinlik, sürüm ve son değişiklik görünürlüğü eklendi.
- [x] Policy kopyalama/çoğaltma, optimistic version kontrolü ve hedef mağaza izolasyonu eklendi.
- [x] Ürün bazlı stok/fiyat preview; policy/product sürüm bilgisi ve pasif policy/write engeli eklendi.
- [x] Mevcut fiyat formülü, döviz, minimum fiyat ve minimum fark çekirdeği kullanıldı; kritik fiyat kapsam dışı.
- [x] Geçici SQLite/stale/kopyalama testleri ile 322 Release testi geçti; self-contained yayın `Windows-M32-Policy-Center` altında üretildi.
- [ ] Canlı dispatch yalnız connector'ın mevcut preview/onay kapısından geçer; bu merkez doğrudan HTTP yazmaz.

## M33 veri kalite merkezi — 2026-09-11
- [x] Duplicate SKU/barkod/listing, eksik zorunlu alan, negatif sayı, döviz, görsel URL'si, XML run ve canlı API capability uyarıları tek taramada toplanıyor.
- [x] Fingerprint tabanlı upsert aynı sorunu tekrar taramada çoğaltmıyor; `Resolved` durumu korunuyor.
- [x] WPF merkezinde arama, önem/durum/tür filtreleri, sayaçlar, detay, ürün/XML navigasyonu ve manuel çözüldü işareti eklendi.
- [x] Kalite mesajları redacted saklanıyor; sahte endpoint veya otomatik canlı düzeltme yok.
- [x] Geçici SQLite testleri ile 324 Release testi geçti; self-contained yayın `Windows-M33-Data-Quality` altında üretilecek.
- [ ] Düzeltme önerilerinin uygulanması kullanıcı onayı ve ilgili modül preview'i olmadan yapılmayacak.

## M34 API bağlantı sağlığı, kota ve rate-limit merkezi — 2026-09-11
- [x] Kanal/mağaza bazında auth, son deneme, son başarılı istek, HTTP kodu, hata sınıfı ve son hata kaydı eklendi.
- [x] Salt okunur connector testlerinin response başlıklarından rate-limit, reset ve Retry-After güvenli biçimde yakalanıyor.
- [x] 401/403/408/429/5xx, timeout ve DNS/ağ hataları sınıflandırılıyor; credential/response body saklanmıyor.
- [x] Rate-limit/backoff etkinken bağlantı testinin gereksiz tekrar isteği yapması engelleniyor; `ApiHealthStore.ShouldDefer` kuyruk üst katmanına kapı sağlıyor.
- [x] API bağlantı sağlığı WPF ekranı; arama, durum filtresi, sayaç, backoff ve mağaza/kanal hızlı geçişlerini içeriyor.
- [x] Fake HTTP header/exception ve geçici SQLite testleri ile 327 Release testi geçti; self-contained yayın `Windows-M34-Api-Health` altında üretilecek.
- [ ] Doğrulanmamış marketplace endpointleri ve canlı write işlemleri açılmadı.

## M35 otomasyon takvimi, şablonlar ve çalışma pencereleri — 2026-09-11
- [x] XML yenileme, stok, fiyat, bağlantı sağlığı ve normal sync için tekrar kullanılabilir yerel şablonlar eklendi.
- [x] Interval/Daily/Weekly zamanlama, yerel saat, haftalık gün seçimi ve isteğe bağlı çalışma penceresi eklendi.
- [x] AutomationStore migration'ı ile kanal+mağaza+tür, sonraki çalışma, etkinlik, lease ve retry alanları kalıcı saklanıyor.
- [x] Atomik lease duplicate çalışmayı engelliyor; hatalarda sınırlı exponential backoff, retry limiti ve redacted hata kaydı uygulanıyor.
- [x] Otomasyon WPF ekranında şablon, takvim, pencere, sonraki çalışma, retry ve audit görünürlüğü sağlandı; uygulama kapalı çalışma modeli açıkça belirtiliyor.
- [x] Geçici SQLite/takvim/retry testleri ile 330 Release testi geçti; self-contained yayın `Windows-M35-Automation-Calendar` altında üretilecek.
- [ ] Arka plan Windows servisi veya doğrulanmamış marketplace write işlemi açılmadı.

## M36 döviz, vergi ve yerel ayar yönetim merkezi — 2026-09-11
- [x] Kanal/mağaza bazında TRY/USD/EUR/GBP, CultureInfo ve tarih biçimi ayarları kalıcı olarak saklanıyor.
- [x] Ürün kartı ve Excel akışına KDV % alanı eklendi; 0–100 doğrulaması yapılıyor.
- [x] Aynı kültür katmanı ile sayı parse/format örnekleri güvence altına alındı; yanlış kültür kaynaklı fiyat bozulması reddediliyor.
- [x] Mağaza ayarı kopyalama, sürüm ve audit trail eklendi; politika/formül çekirdeği korunuyor.
- [x] Veri kalite merkezine geçersiz KDV kontrolü eklendi; geçersiz döviz kontrolü yerel ayarlarla tutarlı hale geldi.
- [x] Geçici SQLite/kültür/kopyalama testleri ile 332 Release testi geçti; self-contained yayın `Windows-M36-Locale-Settings` altında üretilecek.
- [ ] Hakediş/mutabakat, kritik fiyat ve muhasebe motoru kapsam dışıdır.

## M37 global arama, hızlı erişim ve yerel indeksleme — 2026-09-11
- [x] Ürün, sipariş, mağaza, kanal ilanı, XML kaynak/çalışma, sync, veri kalite ve API sağlığı kayıtlarını birleştiren migration-safe `search-index.db` eklendi.
- [x] Credential, token, parola, XML URL kimlik bilgisi, müşteri ve mesaj gövdesi indekslenmiyor; hata metinleri redacted tutuluyor.
- [x] 30 saniyelik yerel cache, iptal edilebilir async rebuild/query ve transaction ile atomik indeks yenileme eklendi.
- [x] Ctrl+K/üst arama sonuçları ilgili ekrana yönlendiriyor; ürün güncellemesi indeksi geçersiz kılıyor.
- [x] Geçici SQLite, cancellation ve secret-safety testleri ile 334 Release testi geçti; self-contained yayın `Windows-M37-Global-Search` altında üretilecek.
- [ ] Arama yalnız yerel metadata üzerinde çalışır; marketplace'e canlı write veya doğrulanmamış endpoint yoktur.

## M38 eski veri içe alma ve güvenli geçiş asistanı — 2026-09-11
- [x] Excel, CSV/TSV, JSON ve normal XML için dosya/şema okuma, alan tanıma ve satır bazlı CREATE/UPDATE/SKIP/ERROR önizlemesi eklendi.
- [x] SKU/barkod duplicate ve stale `UpdatedUtc` kontrolleri; marka/kategori normal sözlük planı ve güvenli mağaza metadata planı eklendi.
- [x] Credential/token/password alanları filtreleniyor; geçiş öncesi kullanıcı seçmeli yedek, transaction katalog uygulaması ve migration journal geri alma eklendi.
- [x] Veri geçiş WPF ekranı, hata raporu ve geri alma ID'si eklendi; geçici fixture testleri ile 336 Release testi geçti; self-contained yayın `Windows-M38-Migration-Assistant` altında üretilecek.
- [ ] Proprietary Entegra veritabanı/decompile dosyası doğrudan okunmaz; canlı marketplace write yapılmaz.

## M39 ilk kurulum sihirbazı ve bağlantı onboarding — 2026-09-11
- [x] İlk açılışta opsiyonel WPF sihirbazı, atlama ve yarıda kalınca son adıma dönme state'i eklendi.
- [x] Mağaza metadata, XML kaynağı, varsayılan stok/fiyat policy ve Excel başlangıç profili adımları mevcut doğrulama/store'lara bağlandı.
- [x] Credential/token/parola state'e alınmıyor; XML URL'sinde inline secret reddediliyor; bilinmeyen API capability'si NOT_CONFIGURED/LIVE_API_BLOCKED olarak gösteriliyor.
- [x] Son sağlık özeti ve ilgili panellere hızlı geçiş eklendi; UI smoke/state testleri ile 338 Release testi geçti; self-contained yayın `Windows-M39-Onboarding` altında üretilecek.
- [ ] Sihirbaz canlı marketplace write yapmaz; credential girişi ilgili kanalın mevcut güvenli ekranındadır.

## M40 son üretim sertleştirmesi — 2026-09-11
- [x] Yerel üretim hazırlığı servisi; veri klasörü, core SQLite depoları, secret taraması, veri kalite, connector capability ve API sağlık kontrollerini tek raporda topluyor.
- [x] WPF `Üretim hazırlığı` ekranı kontrol durumlarını, bloklu doğrulanmamış connector'ları ve güvenlik sınırını görünür kılıyor.
- [x] Açık secret ve kritik veri kalite kaydı için bloklayıcı testler eklendi; rapor değerleri dışarı sızdırmıyor.
- [ ] `LIVE_API_BLOCKED` kanallar için resmi credential/scope/endpoint sözleşmesi doğrulanmadıkça canlı write açılmayacak.

## M41 teknik borç ve güvenlik düzeltme turu — 2026-09-11
- [x] Merkezi audit/support redaction; Authorization bearer/basic başlıkları, JSON credential alanları ve query tokenları dışa aktarımlarda maskeleniyor.
- [x] Son başarısız audit kaydı Outcome=`Failed` ile doğrudan sorgulanıyor; arama metnindeki tesadüfi eşleşmeler sonucu bozmaz.
- [x] Audit, tanılama ve destek ZIP sentinel testleri eklendi; Release testleri 344/344 geçti.
- [ ] Resmi credential/scope/endpoint sözleşmesi doğrulanmadıkça `LIVE_API_BLOCKED` kanallarda canlı write açılmayacak.

## M42 Etsy satışa hazırlık dry-run — 2026-09-11
- [x] Etsy sekmesine credential, ilan şablonu, ürün eşleme ve yerel dry-run geçidini tek ekranda gösteren satışa hazırlık paneli eklendi.
- [x] Onaysız/stale canlı yazma engeli ve `LIVE_API_BLOCKED` görünürlüğü korunuyor; dry-run HTTP write çağrısı yapmıyor.
- [x] Yeni connector veya ertelenmiş alanlara dokunulmadı; 344 Release testi geçiyor.

## M43 XML kullanım paritesi kabul turu — 2026-09-11
- [x] XML kaynak → test → düğüm/alan eşleme → önizleme → create/update/skip/error → onay → geçmiş akışı ve kullanıcı farkları dokümante edildi.
- [x] Sipariş kaynaklı stok düşümünün XML yenilemesiyle ezilmemesi, duplicate/bozuk XML/timeout/gzip sınırları mevcut test ve transaction akışıyla doğrulandı.
- [x] XML varyant mapping ve doğrulanmamış marketplace write kapsam dışı bırakıldı; Release testleri 344/344.

## M44 Core UI kullanılabilirlik smoke turu — 2026-09-11
- [x] Büyük listelerde sanallaştırma/sayfalama, uzun işlerde iptal/ilerleme ve async işlem kapıları doğrulandı.
- [x] Navigasyon, global arama, boş/loading/hata/başarı durumları ve riskli butonların çift tetikleme koruması gözden geçirildi; `docs/UI-SMOKE.md` eklendi.
- [x] UI kodu derlendi, 344 Release testi geçti ve self-contained publish üretildi; fiziksel DPI/ekran smoke bu ortamda çalıştırılmadı.

## M45 release integration — 2026-09-11
- [x] M1–M44 stacked zinciri `codex/issue-80-release-integration` üzerinde doğrulandı; eksik/çakışan çalışan özellik bulunmadı.
- [x] SQLite migration, secret redaction, explicit approval, stale/idempotency ve transaction regresyon matrisi kontrol edildi.
- [x] 344/344 Release testi, Release build ve self-contained win-x64 publish tamamlandı; ayrıntı `docs/RELEASE-INTEGRATION.md` içinde.

## M46 günlük kullanım parite matrisi — 2026-09-11
- [x] Ürün, toplu işlem, arama, kategori/marka, XML, ayar, Excel ve sipariş günlük akışları TAM/KAPSAM matrisiyle belgelendi.
- [x] Preview/onay/stale/idempotency, sayfalama ve ertelenmiş alan sınırları korunuyor; `docs/DAILY-PARITY-MATRIX.md` eklendi.

## M47 Etsy resmi OAuth ve read doğrulaması — 2026-09-11
- [x] Güncel resmi OAuth token endpoint'i ve scope sözleşmesi kontrol edildi; token URL'si `api.etsy.com` olarak düzeltildi.
- [x] PKCE/state/HTTPS callback, DPAPI credential saklama ve fake HTTP contract testleri korunuyor.
- [ ] Gerçek mağaza read testi için kullanıcı credential/scope gerekir; eksik durumda canlı çağrı yapılmaz.

## M48 Etsy listing yaşam döngüsü — 2026-09-11
- [x] Resmi create/update/publish gereksinimleri doğrulandı; publish `state=active` ile açık approval/stale/idempotency kapısından geçiyor.
- [x] Fake HTTP + SQLite testleri eklendi; 345/345 Release testi geçti.

## M49 Etsy sipariş senkronu — 2026-09-11
- [x] Resmi receipt read akışına pagination ve `min_last_modified` incremental filtresi eklendi.
- [x] Mağaza+receipt idempotent upsert, transaction stok kararı ve explicit restock preview korunuyor; 345 Release testi geçiyor.
- [ ] Gerçek mağaza credential olmadan canlı receipt çağrısı yapılmadı.

## M50 uçtan uca satıcı dry-run — 2026-09-11
- [x] Sentetik XML → katalog → Etsy sipariş stok kararı → restart/idempotency fixture testi eklendi.
- [x] Fake adapter kullanıldı; gerçek marketplace HTTP write ve PII yok; 346/346 Release testi geçiyor.

- [x] M51 ürün listesi/arama paketi: filtre eşleşmelerinde NOCASE, cancellation kapısı ve 100 maddelik checklist (docs/taskpacks/M51.md).

- [x] M52 toplu ürün işlemleri: channel mapping batch commit cancellation kapısı ve atomiklik testi (docs/taskpacks/M52.md).

- [x] M53 ürün detay düzenleme: metin alanı uzunlukları ve ISO 4217 üç karakter döviz doğrulaması (docs/taskpacks/M53.md).

- [x] M54 XML kaynak/mapping: kaynak adresi yalnız http/https/file; URL şeması doğrulanıyor; credential metni arama indeksine alınmıyor (docs/taskpacks/M54.md).

- [x] M55 Excel içe/dışa aktarma: önizlemede yinelenen SKU/barkod satırları raporlanıyor; atomik import ve undo korunuyor (docs/taskpacks/M55.md).

- [x] M56 kategori/marka/özellik eşleme: öneri normalizasyonunda Unicode ve ardışık boşluklar normalize ediliyor (docs/taskpacks/M56.md).

- [x] M57 ürün medya/görsel: medya liste araması % ve _ karakterlerini literal işler (docs/taskpacks/M57.md).

- [x] M58 sipariş liste/detay: OrdersStore.ReadPage ile kanal/mağaza/durum/metin filtreleri ve sayfalama eklendi (docs/taskpacks/M58.md).

- [x] M60 sync kuyruğu: RetryDelay ile 5s tabanlı üstel backoff ve 300s üst sınırı eklendi; idempotency/claim/redaction korunuyor (docs/taskpacks/M60.md).

- [x] M61 otomasyon scheduler: due-job sorgusu, enable/lease filtreleri ve gece yarısını aşan çalışma penceresi doğrulaması (docs/taskpacks/M61.md).

- [x] M62 hata merkezi/tanılama: SyncErrorClass için güvenli kullanıcı açıklamaları eklendi; redaction, retry ve idempotency korunuyor (docs/taskpacks/M62.md).

- [x] M63 credential/secret security: Authorization and secret query values are redacted before metadata, audit, and support export persistence (docs/taskpacks/M63.md).

- [x] M64 backup/restore: manifest duplicate path, SHA-256 format and length validation added before extraction (docs/taskpacks/M64.md).

- [x] M65 installer/onboarding: safe install path policy validates normalized paths and rejects Windows system targets (docs/taskpacks/M65.md).

- [x] M66 performans: OrdersStore.ReadPage filtreleme/sayfalama SQL tarafına taşındı; büyük sipariş listelerinde full materialization azaltıldı (docs/taskpacks/M66.md).

- [x] M81 mağaza bağlantı/ayar güvenliği: credential ShopId binding, disabled state ve geçmiş koruyan deactivate akışı (docs/taskpacks/M81.md).
- [x] M82 çoklu mağaza izolasyonu: SyncJob idempotency ve otomasyon enqueue işlemleri ShopId kapsamına alındı (docs/taskpacks/M82.md).
- [x] M83 fiyat politikası: policy currency normalize/culture-safe hale getirildi; manuel kur, koruma tabanı ve deterministic rounding korunuyor (docs/taskpacks/M83.md).
- [x] M84 stok politikası: pasif policy dispatch engeli, wrong-shop/persisted değer doğrulaması ve safety/maximum korumaları (docs/taskpacks/M84.md).
- [x] M85 medya pipeline: rate-limit sınıflandırması, bounded cancellation-aware retry ve normalize URL TTL cache/invalidation eklendi (docs/taskpacks/M85.md).
- [x] M86 sipariş ingestion: source/local timestamp ayrımı ve bounded order normalization eklendi; shop/order idempotency korunuyor (docs/taskpacks/M86.md).
- [x] M87 sipariş istisna güvenliği: bounded/redacted exception kayıtları ve invariant cancel/return detection eklendi; restock onay/stale/idempotency korunuyor (docs/taskpacks/M87.md).
- [x] M88 sync recovery: atomic cancellation state eklendi; cancelled işler restart recovery/retry ile yeniden dispatch edilmiyor (docs/taskpacks/M88.md).
- [x] M89 automation reliability: lease token ownership ile stale worker completion/failure overwrite engellendi (docs/taskpacks/M89.md).
- [x] M90 API health: cancellation ayrı state, Retry-After/reset/remaining header validation ve shop-scoped backoff görünümü (docs/taskpacks/M90.md).
- [x] M91 audit/support: audit araması mağaza/marketplace alanlarını kapsıyor; destek ve audit redaction zincirinde e-posta/telefon PII maskeleniyor (docs/taskpacks/M91.md).
- [x] M92 backup/recovery: manifest dosya SHA-256 doğrulaması restore öncesinde zorunlu; staging/safety backup sonrası eski veri rollback noktası korunuyor (docs/taskpacks/M92.md).
- [x] M93 data quality: duplicate tespitleri trim/sıralı ve bağlamlı; yayın öncesi görsel URL preflight'ı host/userinfo doğrulamasıyla Error seviyesinde bloklayıcı (docs/taskpacks/M93.md).
- [x] M94 release artifact: self-contained win-x64 çıktısı için sürüm/runtime ve sıralı SHA-256 manifest üreticisi eklendi (docs/taskpacks/M94.md).
- [x] M95 Etsy listing mapping: resmi draft listing geçiş parametresi ve materyal/tag doğrulaması eklendi; preview/onay/stale ve canlı-write blokları korundu (docs/taskpacks/M95.md).
- [x] M96 Etsy dispatch: SyncJob ürün/mağaza/kanal bağlamı dispatch öncesi doğrulanıyor; preview, onay, stale ve idempotency korumaları korunuyor (docs/taskpacks/M96.md).
- [x] M97 Etsy order recovery: eşit veya daha eski kaynak zaman damgalı tekrar alımlar mevcut siparişi overwrite etmiyor; sayfalama, shop scope ve istisna/preview stok kuralları korunuyor (docs/taskpacks/M97.md).
- [x] M98 operator flow: veri kalite kayıtlarında tür/kanal/kaynak bağlamına göre ilgili ürün, XML veya bağlantı ekranına tek adım navigasyon eklendi (docs/taskpacks/M98.md).
- [x] M99 soak/stability: local-only deterministik tekrar ölçümleri için allocation, elapsed ve working-set metrikli StabilityProbe eklendi; marketplace çağrısı yapmaz (docs/taskpacks/M99.md).
- [x] M100 final RC: production readiness kalite kontrolü karar öncesi güncel preflight taraması çalıştırıyor; stale kalite kaydıyla publish edilebilir görünme engellendi (docs/taskpacks/M100.md).
- [x] M101 legacy reconciliation: eski task-pack checklist iddialarını PARTIAL, yeni gerçek-teslimat formatını REAL_DELIVERABLE ve biçimsiz kayıtları UNVERIFIED raporlayan read-only audit helper eklendi (docs/taskpacks/M101.md).
- [x] M102 test altyapısı: repo içine gerçek MSTest projesi ve geçici durum gerektirmeyen redaction/stability testleri eklendi (docs/taskpacks/M102.md).
- [x] M103 parity: global arama indexine sanitized audit olayları ve diagnostics route hedefleri eklendi; parity referansı eksik olduğu için doğrulanmamış akış uydurulmadı (docs/taskpacks/M103.md).
- [x] M104 product ops: katalog sayfalı aramaya trim/NOCASE kaynak kimliği filtresi eklendi; mevcut seçim-preview-stale-transaction akışları korunuyor (docs/taskpacks/M104.md).
- [x] M105 data ingestion: Excel Apply ve ApplyWithUndo artık preview hata listesini ve desteklenmeyen döviz değerlerini ortak doğrulamayla reddediyor; hatalı veri kataloğa yazılmıyor (docs/taskpacks/M105.md).
- [x] M106 order/stock: iptal-iade restock preview kimlikleri trim/kanonik mağaza-kaynak bağlamıyla oluşturuluyor; Apply kimlik doğrulaması ve mevcut onay/stale/idempotency kapıları korunuyor (docs/taskpacks/M106.md).
- [x] M107 ops/settings: Dashboard artık açık veri kalite kritik/hata kayıtlarını ilgili kalite merkezine aksiyon bildirimi olarak taşıyor (docs/taskpacks/M107.md).
- [x] M108 capability audit: tüm katalog kanalları için HTTPS dokümantasyon, blocked-state ve duplicate kayıt denetimi eklendi; doğrulanmayan kanallar LIVE_API_BLOCKED kalıyor (docs/taskpacks/M108.md).
- [x] M109 Etsy readiness: satış hazırlığı sonucu artık SATIŞA HAZIR/EKSİK/LIVE_API_BLOCKED ayrımı yapıyor; shop-scoped read/update payload fake HTTP contract testi eklendi (docs/taskpacks/M109.md).
- [x] M110 major connectors: eBay ağ timeout/cancel hataları credential sızdırmadan sınıflandırıldı; Amazon/Hepsiburada doğrulanmamış sözleşmeleri LIVE_API_BLOCKED kaldı (docs/taskpacks/M110.md).
- [x] M111 secondary connectors: Ozon/Allegro/Joom/Wish/Fruugo/Navlungo için read-only PARTIAL veya LIVE_API_BLOCKED durum matrisi eklendi; doğrulanmamış kanallar HTTP çağrısından korunuyor (docs/taskpacks/M111.md).
- [x] M112 disaster drill: geçici SQLite veri klasöründe yedek manifest/hash, restore ve pre-restore rollback dizini gerçek testle doğrulandı (docs/taskpacks/M112.md).
- [x] M113 security red-team: destek export’unda audit secret redaction ve geçici DB temizliği negatif fixture ile doğrulandı; canlı write yapılmadı (docs/taskpacks/M113.md).
- [x] M114 final freeze: P0/P1 blocker ve doğrulanmış self-contained artifact yoksa V1_READY kararı verilmiyor; mevcut soak/safety/backup/connector test suite’i 9/9 geçti (docs/taskpacks/M114.md).
- [x] M115 home tile dashboard: gerçek local store sayaçları ve dinamik hızlı erişim kartları eklendi; kartlar klavye focus ve AutomationProperties adı taşıyor (docs/taskpacks/M115.md).
- [x] M116 product list parity: ürün dashboard/list erişimi yoğun kart ve hızlı navigasyonla genişletildi; mevcut SQLite-side filtre, sayfalama, async/cancel ve deferred guard’lar korundu (docs/taskpacks/M116.md).
- [x] M117 product form parity: ürün editörü gerçek alan bağlarını koruyan Genel, Görsel/Açıklama, Pazaryeri, XML/provenance ve Sipariş raporu sekmelerine ayrıldı (docs/taskpacks/M117.md).
- [x] M118 XML UI parity: aynı XML kaynağına ait import işlemleri kaynak kimliği bazında serialize edildi; mevcut preview/mapping/provenance güvenlikleri korundu (docs/taskpacks/M118.md).
- [x] M119 dynamic marketplace product UI: ürün kanal özeti ortak connection/mapping/capability/health metadata’sından dinamik üretiliyor; iki mağazalı wrong-shop fixture doğrulandı (docs/taskpacks/M119.md).
- [x] M120 Etsy product surface: ürün, Etsy şablonu, credential/shop ve listing mapping’i birleştiren read-only readiness kararı ve wrong-shop negatif testi eklendi (docs/taskpacks/M120.md).
- [x] M121 supporting screen parity: operasyon sayaçları için ortak gerçek-snapshot özeti eklendi ve dashboard durum satırı bu sözleşmeyi kullanıyor (docs/taskpacks/M121.md).
- [x] M122 final screen parity: merkezi navigation route audit’i eksik paneli BLOCKED raporlıyor ve mevcut rotaların tam sözleşmesini test ediyor (docs/taskpacks/M122.md).
- [x] M123 preflight merkezi: yerel Etsy readiness kanıtlarını CODEX_READY/PARTIAL/BLOCKED kararında birleştirip başlangıç loguna ekledi (docs/taskpacks/M123.md).
- [x] M124 onboarding hazırlığı: credential içermeyen mağaza bağlantı metadata’sı ve yerel preflight kararı birlikte kullanılıyor; canlı onboarding doğrulanmadı (docs/taskpacks/M124.md).
- [x] M125 sync operasyonları: retry edilebilir failed işler için güvenli önizleme, açık onay ve stale kimlik/sürüm kontrolü eklendi (docs/taskpacks/M125.md).
- [x] M126 DB sağlık merkezi: catalog.db için salt-okunur quick_check, schema/WAL/foreign-key özeti ve missing/blocked/error sınıflaması eklendi (docs/taskpacks/M126.md).
- [x] M127 audit/support: retention’a tabi audit kayıtları için cursor tabanlı, tekrar etmeyen sayfalama eklendi; redaction/export güvenliği korunuyor (docs/taskpacks/M127.md).
- [x] M128 offline/degraded: network, auth ve rate-limit durumlarını ayıran bağlantı snapshot’ı ve online olmayan dispatch engeli eklendi (docs/taskpacks/M128.md).
- [x] M129 workspace: kayıtlı route/shop/page/filter state için bounded JSON codec ve bozuk/eski state fallback’i eklendi (docs/taskpacks/M129.md).
- [x] M130 yerel kültür: tr-TR/invariant amount parse ayrımı, TRY formatı ve UTC→Türkiye yerel saat dönüşümü eklendi (docs/taskpacks/M130.md).
- [x] M131 DPI/erişilebilirlik: kayıtlı pencere geometrisini geçerli çalışma alanına clamp eden ve bozuk değerleri güvenli varsayılana çeviren yardımcı eklendi (docs/taskpacks/M131.md).
- [x] M132 ölçek fixture’ı: 100k sentetik ürün üzerinde filtreleme ve sayfalama latency/allocation/working-set metriği ölçülüyor (docs/taskpacks/M132.md).
- [x] M133 update kanalı: yalnız HTTPS ve dosya SHA-256 eşleşmesi doğrulanınca VERIFIED kararı veren, kaynak yoksa NOT_CONFIGURED kalan kontrol eklendi (docs/taskpacks/M133.md).
- [x] M134 güvenlik tehdit modeli: severity sınıflı bulguları sanitize edip P0/P1 bulgularında BLOCKED release değerlendirmesi yapan negatif sözleşme eklendi (docs/taskpacks/M134.md).
- [x] M135 yardım/runbook: gerçek route’larla sınırlı context-help başlıkları, ön koşul ve recovery bilgileri eklendi; ölü/deferred özellik referansı yok (docs/taskpacks/M135.md).
- [x] M136 seller rehearsal: onboarding→import→preflight→sync/order/stock→fault recovery→backup/restart zinciri için deterministik sentetik rapor eklendi; canlı write yok (docs/taskpacks/M136.md).
- [x] M137 GA gate: önceki kanıt, artifact, Release test/publish ve P0/P1 bulgularını birlikte değerlendiren V1_READY/NOT_READY kabul modeli eklendi (docs/taskpacks/M137.md).
- [x] M138 manuel sipariş arşivi: fiziksel silme yapmadan mağaza/sipariş kimliğiyle arşivle-geri al store’u ve gerçek SQLite fixture’ı eklendi (docs/taskpacks/M138.md).
- [x] M139 sipariş transferi: izinli yerel sipariş alanları için invariant XML export/import ve malformed-root güvenlik kontrolü eklendi (docs/taskpacks/M139.md).
- [x] M140 tanımsız çözüm: SKU/barkod/isim önerileri, fingerprint dedup ve shop-scoped açık onay kapısı eklendi; ambiguous/wrong-shop otomatik bağlanmıyor (docs/taskpacks/M140.md).
- [x] M141 Trendyol pilot sözleşmesi: resmi Product V2 batch sınırı/listPrice≥salePrice ve sendInvoiceLink seller/package/HTTPS/invoice format önizleme doğrulamaları eklendi (docs/taskpacks/M141.md).
- [x] M142 metadata şablonları: kanal+mağaza kategori profillerinde default/override önceliği, stale ve wrong-channel izolasyonu eklendi (docs/taskpacks/M142.md).
- [x] M143 medya sağlık: URL duplicate/fingerprint, missing/404/http/wrong-content/oversize sınıflandırması ve secret redaction eklendi (docs/taskpacks/M143.md).
- [x] M144 dönüşüm kuralları: sınırlı numeric/text/default operasyonları, invariant çıktı ve satır bazlı ERROR sonucu eklendi; arbitrary eval yok (docs/taskpacks/M144.md).
- [x] M145 bildirim merkezi: local SQLite bildirimleri severity/fingerprint dedup, redaction, acknowledge ve restart persistence ile eklendi (docs/taskpacks/M145.md).
- [x] M146 iade merkezi: mağaza/sipariş/line/sku bazlı partial return restock preview ve fingerprint eklendi; wrong-shop/missing-SKU/stale otomatik bloklanıyor (docs/taskpacks/M146.md).
- [x] M147 rekabet gözlemi: kanal+shop+product scope, duplicate offer dedup, stale durumu ve yerel fiyat farkı/yüzde hesaplayan read-only evaluator eklendi (docs/taskpacks/M147.md).
- [x] M148 rapor şablonları: izinli kolon whitelist’i, filtrelenmiş satır CSV render, invariant sayı/tarih ve secret alan dışlama eklendi (docs/taskpacks/M148.md).
- [x] M149 ürün kalite skoru: versioned deterministic breakdown/fix-list ve capability BLOCKED alt maddesi eklendi; skor canlı write yapmıyor (docs/taskpacks/M149.md).
- [x] M150 fatura merkezi: Trendyol fatura linki preview/idempotency/scope/stale/HTTP sınıflandırması ve NES/FAST LIVE_API_BLOCKED kapısı eklendi (docs/taskpacks/M150.md).
- [x] M151 stok mutabakatı: kaynak bazlı hareket karşılaştırması, duplicate/negative/jump tespiti ve onaylı versioned yerel correction audit’i eklendi (docs/taskpacks/M151.md).
- [x] M152 ürün değişiklik günlüğü: alan/provenance/version journal, diff, retention ve güvenli stale guarded yerel rollback preview/audit eklendi (docs/taskpacks/M152.md).
- [x] M153 tedarikçi maliyet izleme: kaynak snapshot farkı, eşik/stale uyarıları, culture-safe filtre ve async batch evaluator eklendi; otomatik repricing yok (docs/taskpacks/M153.md).
- [x] M191 satın alma planlama: days-of-cover, min/target stok, supplier maliyet/lead-time, stale uyarısı, filtre ve CSV draft export eklendi; ERP order yok (docs/taskpacks/M191.md).
- [x] M192 çoklu depo stok: location snapshot, toplam/görünür ayrımı, provenance filtresi, timeline ve scope anomaly tespiti eklendi; transfer/fulfillment yok (docs/taskpacks/M192.md).
- [x] M193 kârlılık simülasyonu: SKU/kanal maliyet, KDV, komisyon, kargo/işlem katkısı, stale/marj uyarıları ve filtreli simülasyon eklendi; repricing yok (docs/taskpacks/M193.md).
- [x] M194 döviz/fiyat dönüşümü: kur provenance/stale, manuel override audit, culture-safe parse, yuvarlama ve canlı yazma blokajı eklendi (docs/taskpacks/M194.md).
- [x] M195 alan sahipliği: source-of-truth önceliği, ürün field-lock, default policy, APPLY/SKIP/BLOCKED preview, bulk apply ve stale version guard eklendi (docs/taskpacks/M195.md).
- [x] M196 dropshipping anomali guard: feed delta, mass zero-stock/price/taxonomy riskleri, supplier threshold profili, fail-closed apply ve override audit eklendi (docs/taskpacks/M196.md).
- [x] M197 kanal içerik profilleri: shop/channel/locale profile, inherit/override, official limit validation, stale/scope guard ve onaylı clone eklendi (docs/taskpacks/M197.md).
- [x] M198 source-missing quarantine: first/last seen, grace period, warning/pending action, listing preservation, recovery audit ve local deactivate preview eklendi (docs/taskpacks/M198.md).
- [x] M199 dropship stok güvenliği: safety stock, max displayed, text availability, shop/channel policy, stale block, formula açıklaması ve bulk preview eklendi (docs/taskpacks/M199.md).
- [x] M200 dropship fiyat formülleri: çarpan/yüzde/sabit/KDV/kur/yuvarlama/psychological preview, minimum marj/overflow/bulk guard eklendi; otomatik repricing yok (docs/taskpacks/M200.md).
- [x] M201 kanal ücret kataloğu: doğrulanmış provenance, effective date/stale lookup, komisyon+sabit ücret hesabı, import/export ve audit history eklendi (docs/taskpacks/M201.md).
- [x] M202 Etsy gap audit: official-only capability manifest, inventory migration path assertion, readiness binding ve blocked/preview write matrisi eklendi; üçüncü taraf kodu kopyalanmadı (docs/taskpacks/M202.md).
- [x] M203 Etsy OAuth readiness: scope eksikliği reauthorize, user/shop mismatch block, app-type durumu ve 401/403/408/429/5xx operator sınıflandırması eklendi (docs/taskpacks/M203.md).
- [x] M204 Etsy listing lifecycle: state validation, field diff/readiness preview, ownership guard, destructive confirmation ve delete receipt idempotency eklendi (docs/taskpacks/M204.md).
- [x] M205 Etsy metadata: taxonomy/property cache, section/shipping/return/processing profilleri, physical/digital validation, stale history ve READY approval gate eklendi (docs/taskpacks/M205.md).
- [x] M206 Etsy medya: image/digital/video rank/hash preview, physical/digital guard, video contract blokajı, retry ve destructive approval gate eklendi (docs/taskpacks/M206.md).
- [x] M207 Etsy inventory kapsam koruması: DEFERRED_BY_USER varyant write guard ve HTTP’siz regresyon testi eklendi; varyant motoru açılmadı (docs/taskpacks/M207.md).
- [x] M208 Etsy batch drift: official 100 ID chunking, remote/local field diff, drift classification, stale ve wrong-scope guard eklendi; legacy includes kullanılmadı (docs/taskpacks/M208.md).
- [x] M209 Etsy sipariş: receipt/transaction dedup, status normalization, unknown item queue, checkpoint ve cancel/refund local stock preview/onay guard eklendi (docs/taskpacks/M209.md).
- [x] M210 Etsy seller cockpit: auth/shop/scope/listing/order/sync/drift/dead-letter aggregate readiness, dry-run ayrımı ve operator gate eklendi (docs/taskpacks/M210.md).
# M211: ortak varyant/seçenek domain çekirdeği `DEFERRED_BY_USER` guard ile ertelendi; write/migration yok.
# M212: XML varyant mapping/import `DEFERRED_BY_USER` guard ile ertelendi; normal XML akışı korunuyor.
# M213: Excel varyant workflows `DEFERRED_BY_USER` guard ile ertelendi; workbook apply yok.
# M214: marketplace varyant eşleme `DEFERRED_BY_USER` guard ile ertelendi; publish yok.
# M215: paket/set/bundle `DEFERRED_BY_USER` guard ile ertelendi; stok hareketi yok.
# M216: hızlı satır içi düzenleme `DEFERRED_BY_USER` guard ile ertelendi.
# M217: kritik stok ve kampanya allocation policy local preview olarak eklendi; dış write yok.
# M218: kritik/akıllı/tarih bazlı fiyat `DEFERRED_BY_USER` guard ile ertelendi.
# M219: kanal bazlı update mode planner local preview olarak eklendi; dış write yok.
# M220: iade batch shop izolasyonu ve duplicate suppression eklendi; dış write yok.
# M221: shipping label preview ve duplicate tracking guard eklendi; carrier write yok.
# M222: Fulfillment Core `DEFERRED_BY_USER` guard ile ertelendi; provider request yok.
# M223: Hakediş/Mutabakat Core `DEFERRED_BY_USER` guard ile ertelendi; payment write yok.
# M224: message template placeholder validation eklendi; kanal write yok.
# M225: competition gözleminde provenance yokluğu UNAVAILABLE ayrımı eklendi; scraping/write yok.
# M226: ürün compliance metadata local validator ile eklendi; dış write yok.
# M227: duplicate tracking anomaly detector eklendi; dış write yok.
# M228: linked source graph priority/health selection eklendi; feed write yok.
# M229: Entegra parity family matrix kod içine alındı; deferred ve live API blokları açık tutuldu.
# M230: release artifact SHA-256/length verification eklendi; deferred domainler açık tutuldu.
# M231: ürün workspace P0 acceptance gate kanıt yoksa BLOCKED kalıyor.
# M232: dashboard freshness NO_DATA/STALE/FRESH ayrımı eklendi; sahte KPI yok.
# M233: rapor seçili satır count/total/average aggregate footer eklendi; dış write yok.
# M234: dependency-free bounded paging ile DataGrid virtualization ilkesi adapte edildi; üçüncü taraf kopyası yok.
# M235: repo-local safety/parity/performance/release skills ve read-only reviewer agent eklendi.
# M236: altı senaryolu agent/skill evaluation matrix ve KEEP/MERGE raporu eklendi.
