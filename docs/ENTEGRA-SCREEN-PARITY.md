# Entegra ekran/parite referansı — kullanıcı ekran görüntülerinden çıkarılan özgün tasarım gereksinimleri

Bu belge kullanıcı tarafından paylaşılan Entegra ekran görüntülerinden çıkarılan davranış ve yerleşim referansıdır. Amaç Entegra'nın kaynak kodunu, ikonlarını, marka varlıklarını, metinlerini veya piksel-birebir ekranını kopyalamak değildir. MonoBridgeDesktop özgün WPF bileşenleri ve görsel diliyle aynı günlük kullanım mantığını hedefler.

## 1. Ana menü / dashboard

Hedef:
- Büyük, kolay tıklanabilir tile/kart düzeni.
- Ürünler, Siparişler, Kategoriler, Markalar, XML, Excel, Raporlar, Mağazalar/Entegrasyonlar, Ayarlar, Mesaj/Hata Merkezi gibi ana modüller tek ekranda görünür.
- Kanal/mağaza hızlı erişim kartları capability durumuna göre dinamik eklenir.
- Kart üzerinde gerektiğinde sayaç/uyarı rozeti gösterilebilir: açık sipariş, hata, eksik mapping, bekleyen sync vb.
- Tasarım responsive WPF grid/wrap düzeninde olmalı; farklı DPI ve pencere boyutunda bozulmamalı.

## 2. Ürün listesi ekranı

Hedef davranış:
- Üstte hızlı arama: ürün adı, stok kodu/SKU, barkod.
- Detaylı filtreleme alanı aç/kapat yapılabilsin.
- Sol/üst bölümde kategori ağacı ve kategoriye göre filtre.
- Filtre grupları: ürün durumu, ürün tipi, kargo paket tipi, marka, kaynak, açıklama/görsel doluluk, para birimi, kanal/mağaza bağlı-bağlı değil, sync/update durumları, mükerrer kayıtlar, stok durumu ve normal izinli diğer filtreler.
- Seçili veya filtrelenmiş ürünlere uygulanabilecek güvenli toplu işlemler ayrı panelde gösterilsin.
- Toplu işlem öncesi etkilenme sayısı + READY/SKIP/ERROR önizlemesi olsun; stale/yanlış mağaza/duplicate write engellensin.
- Alt ana grid; ID, SKU/ürün kodu, barkod, GTIN, kısa açıklama/alt başlık, alış fiyatı, kaynak, marka, ürün adı, stok/fiyat ve kanal durumları gibi seçilebilir kolonlar içersin.
- Sayfalama, sayfa limiti, seçili ürün sayısı ve toplam ürün sayısı görünür olsun.
- Büyük katalogda virtualization/sayfalama/async yükleme zorunlu.
- Kullanıcı kolon görünürlüğü ve kayıtlı filtre/görünüm profillerini saklayabilsin.

## 3. Ürün detay / ürün formu

Hedef sekmeler:
- Genel
- Görseller ve Açıklamalar
- Uyumluluk / normal özellikler
- Pazaryerleri
- XML Detayları
- Platform Güncelleme Durumları
- Diğer Detaylar
- Ürün Sipariş Raporu

Not: kullanıcı tarafından ertelenmiş olan `Seçenek-Varyant`, `Hızlı Ürün Ekle`, `Bağlı Paketler`, kritik fiyat gibi alanlar gerçek özellik olarak uygulanmaz. UI'da görünmeleri gerekiyorsa kapalı/ertelenmiş olarak açıkça işaretlenir veya hiç gösterilmez.

Genel sekme alanları:
- Durum / aktiflik
- Ürün tipi (normal ürün sınırında)
- Kategori
- SKU/ürün kodu
- İlan başlığı / ürün adı
- Marka
- Barkod / GTIN / MPN ve normal alanlar
- Alt başlık/normal ek alanlar
- Ölçü/ağırlık/kargo metadata'sı mevcut modele uygunsa
- Para birimi ve KDV
- Alış fiyatı ve mağaza/kanal fiyat preview'ları
- Stok toplamı, mağaza/şube bağlamı, minimum/safety stock policy görünümü
- Kanal/mağaza bağlama seçimleri
- XML/ERP update policy: hangi normal alanlar kaynaktan güncellenebilir/güncellenemez
- Otomatik XML kaynak üyeliği / kaynak izi bilgisi

Canlı marketplace write varsa yalnız mevcut preview + explicit approval + stale/idempotency + wrong-shop kapısından geçer.

## 4. Marketplace-aware ürün sayfası

Yeni bir marketplace/connector projeye eklendiğinde ürün detay ekranına manuel kopya kod yazmak yerine ortak capability sözleşmesinden dinamik kanal paneli/sekmesi oluşmalı.

Her kanal panelinde yalnız desteklenen capability'ler gösterilir:
- listing/product read
- listing create/update/publish readiness
- stok/fiyat preview/write
- sipariş read
- mesaj/tracking yalnız doğrulanmışsa
- son sync, son hata, listing/shop ID, mapping durumu
- WORKING / PARTIAL / LIVE_API_BLOCKED / NOT_SUPPORTED durumu

Kanal özel normal alanlar yalnız resmi API sözleşmesi doğrulanmışsa gösterilir. Endpoint/scope uydurulmaz.

## 5. XML kaynak listesi

Hedef:
- XML kaynakları listesi: ID, aktif/pasif, kaynak adı, son hata, son çalışma mesajı, son başarı zamanı.
- Kaynak ekle/sil/yenile/test et/çalıştır aksiyonları.
- Alt veya detay panelinde kaynak-kategori eşlemeleri, fiyat/stok policy, son sayaçlar ve çalışma geçmişi.
- Aynı kaynak paralel ikinci kez çalışmaz.
- Hata satırından ilgili XML kaynağına ve gerekirse ürün/data-quality ekranına navigasyon.

## 6. XML kaynak formu

Genel alanlar:
- Aktif/pasif
- Modül/tedarikçi etiketi
- XML adı
- XML URL veya yerel dosya yolu
- Gerekirse güvenli credential binding
- Test/indir aksiyonu
- Ürün repeat-node seçimi
- Alan mapping: ürün ID, SKU, ürün kodu, ürün adı, açıklama, para birimi, fiyat, KDV, alt başlık, fatura adı, görseller, GTIN, MPN, marka, kategori, kategori alt seviyeleri, barkod, ağırlık, en/boy/derinlik, menşei ve mevcut normal ürün alanları
- Ondalık ayırıcı / sayı kültürü / sabit adet / prefix-suffix / varsayılan KDV / varsayılan para birimi
- Sabit marka/kategori veya mapping tabanlı marka/kategori
- Kargo paket tipi veya diğer normal metadata mevcut modele uygunsa

Sekmeler davranış olarak şu grupları kapsayabilir:
- Genel
- Fiyat/Stok/Açıklama kuralları
- Uyumluluk/normal alanlar
- Diğer ayarlar

`Seçenek Ekleme` / varyant mapping kapsam dışıdır.

## 7. Ekran tasarım ilkeleri

- Entegra'ya aşina kullanıcı için bilgi yoğunluğu ve günlük akış tanıdık olsun.
- Ancak ikon, renk, marka, metin ve piksel yerleşimi birebir kopyalanmasın.
- Eski WinForms/WPF hissini aynen klonlamak yerine modern, okunabilir, yüksek DPI uyumlu WPF düzeni kullanılsın.
- Büyük ekranlarda bilgi yoğun, küçük pencerede scroll/accordion/responsive düzen kullanılabilir.
- Her ana ekranda hata/empty/loading/cancel durumları açık olmalı.
- Riskli yazma eylemlerinde çift tıklama/replay engeli bulunmalı.

## 8. Zorunlu entegrasyon kuralı

Yeni bir marketplace connector eklendiğinde:
1. Capability matrisi kayıt edilir.
2. Mağaza bağlantı kartı oluşur.
3. Ürün detayında kanal paneli/sekmesi oluşur.
4. Ürün listesinde kanal durumu filtresi/kolonu erişilebilir olur.
5. Sync/hata merkezinde kanal işleri görünür olur.
6. Desteklenmeyen operasyon UI'da açıkça bloklu görünür; sahte buton/başarı üretme.

Bu davranış ortak adapter mimarisinden gelmeli; kanal başına kopya UI mantığı minimumda tutulmalıdır.
