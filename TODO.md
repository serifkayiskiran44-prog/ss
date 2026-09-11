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
