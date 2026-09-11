# Entegra-benzeri kullanım paritesi — davranış referansı

Bu belge kullanıcı tarafından sağlanan eğitim transkriptleri, statik analiz/decompile notları ve veritabanı bulgularından çıkarılmış **davranışsal referanstır**. Entegra kaynak kodu, marka varlıkları, metinleri, ikonları veya ekranlarının birebir kopyası değildir. MonoBridgeDesktop özgün WPF mimarisini korur.

## Kaynak ve güven sınırı

- Kullanıcı tarafından sağlanan `Entegrasyon-160-Transkript(1).txt` günlük kullanım akışlarının ana davranış kaynağıdır.
- Kullanıcı tarafından sağlanan `ANALYSIS(1).md` decompile çıktısının .NET Framework/WPF olduğunu ve statik inceleme yapıldığını belirtir; decompile çıktı özgün kaynak kod değildir.
- `DATABASE_FINDINGS(1).md` ürün, sipariş, XML/Excel, mağaza, mesaj, fiyat/stok ve kategori cache tablolarının varlığını gösterir; tablo adı tek başına işlem semantiği sayılmaz.
- Resmi marketplace API sözleşmesi doğrulanmayan hiçbir endpoint/scope uydurulmaz; ilgili capability `LIVE_API_BLOCKED` / `NOT_CONFIGURED` kalır.

## Hedef günlük kullanım omurgası

### 1. Ürünler listesi

Hedef akış:
- SKU/stok kodu, barkod ve ürün adıyla hızlı arama.
- Detaylı aramada kategori, marka, kaynak, aktif/pasif durum, stok/fiyat durumu, kanal/mağaza bağlı/bağlı değil durumu, açıklama/görsel durumu ve mükerrer SKU/barkod filtreleri.
- Çoklu ürün kodu/barkod ile toplu arama ve sayfalama.
- Ürün satırından ürün kartına, kaynak/XML/Excel geçmişine, kanal planına ve son sync/hata kaydına hızlı geçiş.

Referans eğitim başlıkları: Ürün Listesi Genel Kullanım, Detaylı Arama, Toplu İşlemler.

### 2. Güvenli toplu ürün işlemleri

Hedef akış:
- Seçili veya filtrelenmiş ürün kümesinde toplu normal alan, kategori/marka, kanal planı, stok/fiyat policy ve export işlemleri.
- Uygulama öncesi READY/SKIP/ERROR önizlemesi.
- Stale ürün sürümü, yanlış mağaza ve duplicate tetikleme engeli.
- Atomik uygulama + audit/receipt + rollback bilgisi.

Canlı marketplace write yalnız somut preview + açık kullanıcı onayı + stale/idempotency kapısıyla yapılır.

### 3. Kategoriler, markalar ve normal özellik eşleme

Hedef akış:
- Yerel kategori/marka sözlüğü.
- Kanal+mağaza bazında harici kategori/marka ID eşleme.
- Ürün bazlı override ile kategori bazlı toplu eşleme ayrımı.
- `MAPPED / MISSING / STALE / INVALID` görünümü.
- Çok ürün için aynı kategoriye ait normal zorunlu özellikleri yalnız dolu alanları kopyalayarak güvenli toplu uygulama.
- Eşleme geçmişi ve yanlış mağaza izolasyonu.

### 4. XML tedarikçi merkezi

Normal ürün XML akışı Entegra'ya aşina kullanıcı için şu sırada olmalı:

1. Kaynak ekle: ad, URL/yerel dosya, aktif/pasif, yenileme planı.
2. Test/indir ve XML ağacını göster.
3. Tekrarlayan ürün düğümünü seç/öner.
4. Alan eşleme: ürün kimliği/SKU, barkod, ad, açıklama, stok, fiyat, para birimi, KDV, görseller, marka, kategori, GTIN/MPN ve desteklenen normal alanlar.
5. Varsayılan KDV/para birimi ve sabit değer desteği.
6. Stok dönüşümü: kaynak stoktan güvenlik stoğu düşme veya metin tabanlı var/yok dönüşümü gibi normal kurallar.
7. Fiyat formülü: kaynak fiyatı doğrudan alma veya kontrollü çarpan/toplama ile mağaza fiyat planı üretme.
8. Kaynak alanı için `ilk yüklemede al, sonraki XML güncellemelerinde kilitle` türü seçici update policy.
9. Kategori aktar veya mevcut yerel kategoriyle eşleştir.
10. Preview: CREATE/UPDATE/SKIP/ERROR; explicit apply.
11. Çalışma geçmişi: create/update/skip/error sayaçları, son çalışma, hata ve retry.
12. Zamanlama: interval veya günlük saat; aynı kaynağın paralel ikinci çalışması engellenir.
13. Sipariş stok hareketleri XML yenilemesi tarafından kör biçimde ezilmez.

`XML VARYANT MAPPING` kapsam dışıdır.

### 5. Excel merkezi

Hedef akış:
- Esnek kolon eşleme; kolon sırası sabit olmak zorunda değildir.
- Kaydedilebilir import/export profilleri ve alias'lar.
- Normal ürün create/update, stok ve fiyat güncelleme için preview.
- KDV dahil/hariç, sayı/tarih kültürü ve varsayılan değerler.
- CREATE/UPDATE/SKIP/ERROR satır kararı.
- Atomik import + undo journal + hata raporu.
- Filtrelenmiş ürünleri veya siparişleri seçilebilir alanlarla Excel'e aktarma.
- Export alan seçimini şablon olarak kaydetme.

Varyant Excel akışları kullanıcı kararıyla kapsam dışıdır.

### 6. Sipariş listesi

Hedef akış:
- Sipariş numarası ve müşteri adıyla arama.
- Sipariş tarihi, yazdırılma tarihi, kargoya gönderim tarihi ve yerel eklenme tarihine göre filtre.
- Kargo firması, kanal/mağaza, sipariş durumu/kaynak ve yerel işlem durumlarına göre filtre.
- Yazdırılmış, ödeme bilgisi bulunan, tanımsız/mapping sorunu bulunan veya notlu siparişleri hızlı ayırma.
- Çoklu sipariş no ve ID aralığı ile arama.
- Güvenli toplu export/rapor ve desteklenen read-only/yerel işlemler.
- Eksik SKU, duplicate order/event, stale sipariş ve yanlış mağaza durumları istisna merkezine gider.
- Satış stok düşümü idempotent; iptal/iade kör otomatik restock yapmaz, preview + açık karar ister.

Fulfillment ve hakediş/mutabakat çekirdeği kapsam dışıdır.

### 7. Mağaza ve bağlantı ayarları

Hedef akış:
- Tek panelde kanal+mağaza etkinliği, display name, capability, auth durumu, son bağlantı testi, son hata.
- Credential değerleri şifreli store'da; UI'da maskeli.
- Read-only bağlantı testi ve API sağlık/rate-limit durumu.
- Kanal bazlı normal ürün adı/açıklama/fiyat/stok alanı seçimi mümkünse ortak policy üzerinden yapılır.
- Çok mağaza kimliği tüm mapping, sync, order ve audit kayıtlarında korunur.

### 8. Genel ayarlar

Kaynaklarda görülen ve bizim ürüne uygun normal karşılıklar:
- Varsayılan KDV ve para birimi.
- Otomatik yedek zamanı ve hedefi.
- Yerel log/audit etkinliği ve retention.
- Sipariş geldiğinde yerel stok düşümü policy'si.
- Kanal bazlı ürün adı/açıklama alanı seçimi.
- Kaydedilmiş görünüm/kolon tercihlerini sıfırlama.

Fatura/ERP/fulfillment/hakediş gibi ayrı ürün alanları bu belgeyle otomatik kapsam kazanmaz.

### 9. Mesaj, hata ve işlem merkezi

Hedef akış:
- Mesaj/taslak/şablon kayıtları mağaza ve sipariş bağlamına bağlanır.
- Sync/XML/Excel/connector hataları tek merkezde sınıflandırılır.
- Secret redaction zorunludur.
- Kayda tıklayınca ilgili ürün, sipariş, mağaza veya XML kaynağına gidilir.

### 10. Günlük navigasyon

Kullanıcı Entegra eğitim başlıklarına aşinaysa bizim özgün menüde şu karşılıkları tek bakışta bulabilmeli:
- Ürünler
- Siparişler
- Kategoriler
- Markalar
- XML
- Excel
- Mağazalar / Entegrasyonlar
- Mesajlar
- Ayarlar
- Sync / Hatalar / Audit

UI Entegra'nın piksel, ikon, renk, marka veya metin kopyası olmayacak; yalnız iş akışı tanıdık ve hızlı olacak.

## Açıkça kapsam dışı / kullanıcı tarafından ertelenmiş

Aşağıdaki alanlar bu parite belgesi nedeniyle açılmaz:
- Variant sistemi
- Paket / set / bundle ürün
- Hızlı satır içi düzenleme
- Kritik fiyat / akıllı kritik fiyat
- XML variant mapping
- Fulfillment Core
- Hakediş / Mutabakat Core

## ChatGPT paralel denetim hattı

Codex aktif task-pack branch'ini geliştirirken ChatGPT aynı branch'e paralel kod yazmaz. Tamamlanan paketler bu belgeye göre read-only denetlenir. Parite boşluğu veya P0/P1 hata bulunursa ayrı `CHATGPT_PARITY` issue'su açılır; aktif Codex işini bölmeden hedefli düzeltme planlanır. Final release integration aşamasında bu parite boşlukları kapatılmadan günlük kullanım kabulü tamamlanmış sayılmaz.
