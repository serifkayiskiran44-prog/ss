# Ürün Listesi + Ürün Kartı tam çalışma alanı paritesi — 2026-09-11

## Amaç
Kullanıcının paylaştığı Entegra ekran görüntüleri, `ENTEGRA-SCREEN-PARITY.md`, `ENTEGRA-WORKFLOW-PARITY.md`, Entegra-3 yetkili statik analiz notları ve Entegra resmi eğitim/sürüm notları birlikte referanstır. Hedef ürün yönetiminin günlük kullanım akışını, bilgi yoğunluğunu, sekme/filtre/toplu işlem davranışını ve sağ-tık erişimini mümkün olduğunca aynı mantıkta sunmaktır. Entegra marka/ikon/metin/kaynak kodu kopyalanmaz; özgün WPF bileşenleri kullanılır.

## A. Ürün Listesi — birincil çalışma ekranı
1. Ürünler açıldığında ilk görülen ekran bilgi yoğun gerçek DataGrid olmalı; kart/marketing görünümü değil.
2. Üst hızlı arama: ID, ürün adı, SKU/stok kodu, barkod, GTIN, MPN, marka, kaynak, listing ID/URL.
3. Hızlı arama Enter ile çalışır; temizle/yenile; debounce; cancel; önceki sonuç ekranda donmaz.
4. Kategori ağacı: hiyerarşik, sayılı, ara, seçili kategori + alt kategoriler dahil/hariç seçeneği.
5. Detaylı Arama paneli Entegra mantığında ayrı açılır/kapanır çalışma alanı olmalı.
6. Filtre grupları: aktif/pasif/tümü; simple/variant/bundle; marka; kategori; supplier/source; para birimi; KDV; stok aralığı; fiyat aralığı; maliyet aralığı; minimum/kritik stok; kritik fiyat; açıklama/görsel var-yok; barkod/GTIN/MPN var-yok; raf no dolu/boş; menşei/compliance eksik; XML bağlı/bağlı değil; marketplace bağlı/bağlı değil; listing state; sync state; error state; source-missing; duplicate; field-lock; only-price/only-stock update mode; readiness.
7. Pazaryeri filtresi connector registry'den dinamik oluşur; Etsy/Trendyol/Amazon/HB vb. hard-code edilmez.
8. Kayıtlı filtreler/görünümler; son kullanılan görünüm; varsayılana dön.
9. Grid kolonları en az: seçim, durum, görsel thumbnail, ID, parent/group, varyant, SKU, barkod, GTIN, MPN, ürün adı, alt başlık, marka, kategori, kaynak/tedarikçi, alış fiyatı, maliyet dövizi, satış fiyatı, satış dövizi, KDV, stok, minimum/kritik stok, raf, ağırlık/desi/ölçüler, güncellenme zamanı, XML/source, Etsy listing, kanal durumları, readiness, son sync, hata.
10. Kolon seçici; kolon sırası; genişlik; sabitleme/freeze; sort; multi-sort; görünüm profilinde kalıcı saklama.
11. Satır yoğunluğu compact/normal; zebra/selection okunabilirliği; 125/150/175/200% DPI'da bozulmama.
12. Çoklu seçim checkbox + shift/ctrl; seçili sayısı ve toplam/filtreli sayısı her zaman görünür.
13. Sayfalama: ilk/önceki/sonraki/son, sayfa boyutu, sayfa no, toplam kayıt; büyük katalog belleğe tamamen alınmaz.
14. 100k ürün hedefinde SQLite-side filter/sort/page + virtualization + async/cancellation.
15. Satır çift tık/Enter ürün kartını aynı ürün ve mevcut shop/channel context ile açar.
16. Ürün ekle, detaylı ürün ekle, varyantlı ürün ekle, bundle/set ekle, linkten/hızlı ürün ekle giriş noktaları capability/kapsama göre görünür.
17. Toplu İşlemler paneli ayrı sekme/panel: durum, kategori, marka, source, fiyat, stok, alan lock, channel update mode, marketplace plan/publish, XML dahil-hariç, export ve güvenli izinli aksiyonlar.
18. Toplu işlemde hedef küme açıkça `seçili ürünler` veya `mevcut filtre sonucu` olarak seçilir; yanlışlıkla tüm katalog yok.
19. Her bulk iş önce READY/SKIP/ERROR/BLOCKED preview, etkilenen alan eski→yeni diff, sayım ve reason verir.
20. Apply sonrası receipt/audit/undo desteklenen local işlemlerde bulunur; live channel write ayrı explicit approval kapısından geçer.
21. Sağ tık menüsü Entegra günlük kullanım mantığına yakın ana operasyonları sunar: ürünü aç, kopyala, aktif/pasif, kategori/marka, fiyat/stok, kritik fiyat/tarih bazlı fiyat, channel-only price/stock modes, marketplace eşleme/durum, XML/source, export, geçmiş/audit. Desteklenmeyen connector eylemi gizlenir veya NOT_SUPPORTED.
22. Context menu seçili satır/çoklu seçim bağlamını kaybetmez; destructive/riskli işlemler confirmation/preview ister.
23. Satırda marketplace durum rozetleri: linked/unlinked, active/draft/inactive/error/stale/blocked/unknown; tooltip'te shop, listing ID, last sync, error.
24. Veri kalite/readiness göstergesi ürün listesinde görünür ve tıklayınca eksik alanlara gider.
25. Source-missing/anomaly/price-stock riskleri listede belirgin ama normal kullanım engellenmeden filtrelenebilir.
26. Export current view / selected / filtered; kolon profilini ve kültürü korur.
27. Empty/loading/error/offline states gerçek; sahte başarı veya sonsuz spinner yok.

## B. Ürün Kartı / Ürün Formu — ana düzen
1. Ürün kartı tek ürünün merkez çalışma alanıdır; popup bilgi kartı değil.
2. Üst identity strip: ürün ID, ürün adı, SKU, aktiflik, ürün tipi, source, dirty/stale durumu; Kaydet/Vazgeç/Yenile; önceki/sonraki ürün navigasyonu güvenli context ile.
3. Sekmeler en az: Genel; Fiyatlar/Stok; Görseller ve Açıklamalar; Seçenek-Varyant; Bağlı Paketler/Bundle; Pazaryerleri; XML Detayları/Bağlantılı XML'ler; Platform Güncelleme Durumları; Uyumluluk/Özellikler; Rekabet; Diğer Detaylar; Ürün Sipariş Raporu; Geçmiş/Audit.
4. Genel: aktiflik, tip, kategori, marka, SKU/stok kodu, ürün/ilan adı, alt başlık, barkod, GTIN, MPN, fatura adı, raf, menşei, min/max satış adedi, son kullanma tarihi ve normal metadata.
5. Ölçüler/kargo: ağırlık, desi, en/boy/derinlik, paket adedi/tipi ve desteklenen shipping metadata.
6. Fiyatlar: alış fiyatı, alış KDV durumu, maliyet, para birimi, KDV, satış fiyatları, kanal fiyat preview, minimum/kritik fiyat, tarih bazlı fiyat, akıllı fiyat policy, kârlılık simülasyonu ve stale fee/kur uyarısı.
7. Stok: toplam stok, location/depo snapshot, minimum/kritik stok, safety stock, campaign allocation, sellable stock preview ve son stock movements.
8. Görseller: thumbnail listesi, sıralama, ana görsel, source URL/provenance, marketplace media status; açıklama/SEO normal alanları aynı sekmede veya alt panelde.
9. Varyant: parent-child, option/value, SKU/barcode/GTIN/MPN, fiyat/stok/media override, marketplace option mapping ve validation.
10. Bundle: component SKU + adet, hesaplanan satılabilir stok, maliyet/fiyat preview, circular dependency engeli.
11. Pazaryerleri ana sekmesi connector registry'den dinamik oluşur; platform sırası kullanıcı tarafından düzenlenebilir ve saklanır.
12. Her marketplace alt paneli: shop, listing ID/URL, remote state, category, attributes, channel title/content, price/stock, update mode, readiness, last sync/error, capability-specific fields.
13. Trendyol gibi kanal özel alanlar yalnız resmi metadata/contract ile; Etsy taxonomy/shipping/return/readiness/media/inventory alanları kendi capability panelinden gelir.
14. `Sadece fiyat`, `sadece adet`, `content/full` update modes ürün kartında görünür ve bulk/list ile aynı modele bağlıdır.
15. XML Detayları: hangi supplier/source'tan geldi, source field values, mapping/profile, last import, source-missing, field provenance.
16. Bağlantılı XML'ler: bir ürüne çok kaynak, include/exclude, preferred supplier, fallback, source health.
17. Field source-of-truth/lock: her önemli alanın manual/XML/Excel/channel kaynağı ve kilidi görülebilir; dış kaynak kilitli alanı ezmez.
18. Platform Güncelleme Durumları: pending/succeeded/failed/blocked, payload diff özeti, retry/dead-letter linki, timestamp.
19. Uyumluluk/Özellikler: category specs, normal attributes, manufacturer/importer/responsible party, menşei, safety/care info, completeness.
20. Rekabet: yalnız official/read-permitted capability ile snapshot/buybox/price comparison; varsayılan live repricing yok.
21. Ürün Sipariş Raporu: bu SKU/variant için order lines, quantity, revenue, cancel/refund/return, stock movement; shop/channel/date filters.
22. Geçmiş/Audit: alan eski→yeni, kaynak, zaman, operation/shop context; rollback desteklenen local işlemlerde.
23. Save pipeline: validate → diff → optimistic version/stale check → local transaction → audit → optional sync plan. Kaydetmek otomatik marketplace write değildir.
24. Unsaved changes ile sekme/ürün/pencere değiştirmede açık davranış; sessiz veri kaybı yok.
25. Form 1366x768 minimum kullanılabilir; 1920x1080 bilgi yoğun; DPI/keyboard/tab order/accessibility testleri.

## C. Entegra resmi davranışlarından özellikle korunacak alanlar
- Ürünler Listesi: genel kullanım, toplu işlemler, detaylı arama, sağ tık dört ana günlük akıştır.
- Ürün listesi ve ürün formunda tarih bazlı fiyat erişimi vardır.
- Ürün formunda minimum stok vardır.
- Ürün formunda Bağlantılı XML'ler vardır ve XML bazlı dahil etme davranışı vardır.
- Pazaryeri sekmelerinde kanal özel alanlar ve `Sadece Adet/Sadece Fiyat` benzeri update modes bulunabilir.
- Platformların ürün formundaki sırası düzenlenebilir.
- Ürün listesinde para birimi boş, kritik fiyatı olanlar, raf no dolu gibi operasyon filtreleri zaman içinde eklenmiştir.

## D. Kabul kriteri
- Sadece alanların bulunması parite değildir. Kullanıcı ekran görüntülerindeki günlük akış sırası, bilgi yoğunluğu, filtre/bulk/right-click erişimi ve ürün kartı sekme mantığı çalışmalıdır.
- Screenshot-driven smoke: referans ekranlarda görülen her izinli kontrol `implemented / intentionally adapted / not supported with reason` olarak eşlenir.
- Entegra proprietary asset/code/metin kopyalanmaz; fonksiyon ve interaction parity hedeflenir.
- 100k ürün performans, stale/wrong-shop/idempotency, variant/bundle/source, bulk preview/undo, DPI/navigation ve restart testleri final kabulün parçasıdır.
