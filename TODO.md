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
