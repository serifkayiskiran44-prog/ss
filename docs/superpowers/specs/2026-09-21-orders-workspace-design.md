# Sipariş Çalışma Alanı Tasarımı

## Amaç

MonoBridge sipariş ekranını Entegra eğitimlerindeki günlük kullanım sırasına uygun, masaüstünde hızlı çalışılan bir sipariş merkezine dönüştürmek. Ana liste tam genişlikte kalacak; sipariş ayrıntısı dar bir sağ panel yerine ayrı ve geniş bir sipariş kartında açılacak.

## İncelenen kaynaklar

- Entegra siparişler genel kullanımı
- Entegra manuel sipariş ekleme
- Entegra siparişler toplu işlemler
- Entegra ayrıntılı arama ve filtreleme
- Entegra Excel ile sipariş ekleme
- Entegra iade yönetimi
- Kullanıcının üç referans ekran görüntüsü
- `SİPARİŞ ALANLARI.xlsx` içindeki 124 sütun ve dört örnek Trendyol sipariş satırı

## Kullanım akışı

### Sipariş listesi

- Sağdaki sabit ayrıntı paneli kaldırılır; tablo kullanılabilir alanın tamamını kaplar.
- Üstte hızlı arama, tarih aralığı, pazaryeri, mağaza, sipariş/kargo/ödeme durumu ve işlem işaretleri bulunur.
- `Ayrıntılı filtreler` açılıp kapanır; günlük kullanımda ekran kalabalık olmaz.
- Satıra çift tıklama veya `Sipariş kartını aç` düğmesi geniş sipariş kartını açar.
- Durum renkleri yalnız satırın sol şeridi ve durum hücresinde kullanılır; iptal/iade kırmızı, sorun turuncu, tamamlanan yeşildir.

### Sipariş kartı

- Aynı pencere salt okunur inceleme, yerel düzenleme ve manuel sipariş oluşturma kiplerinde kullanılır.
- Sekmeler: `Genel`, `Müşteri ve fatura`, `Kargo`, `Ürünler`, `Durum geçmişi`, `Notlar`.
- Genel sekmede sipariş/platform kimlikleri, kaynak, durum, ödeme ve tarihler yer alır.
- Ürünler sekmesinde SKU, barkod, ürün adı, adet, net birim fiyat, KDV, brüt toplam, indirim ve komisyon gösterilir.
- Alt/sağ toplam kutusunda net tutar, KDV, indirim, komisyon ve genel toplam ayrı gösterilir.
- KDV ve brüt toplam, kaynak değer yoksa ürün satırlarından hesaplanır. Kaynak toplamla hesaplanan toplam uyuşmazsa kullanıcıya fark gösterilir; değer sessizce değiştirilmez.
- NES fatura işlemleri ve Trendyol fatura yükleme mevcut güvenlik kapılarıyla karttan erişilir.

### Manuel ve Excel sipariş

- `Yeni sipariş` aynı kartı boş ve düzenlenebilir açar.
- Ürün satırı ekleme/çıkarma, müşteri/fatura/sevk/kargo bilgisi ve net-KDV-brüt hesapları bulunur.
- `Excel’den sipariş al` mevcut önizleme–uygula–geri al motorunu Siparişler ekranından açar.
- Entegra dışa aktarımına uygun hazır eşleme profili, bozuk Türkçe başlıkları da tanıyan normalizasyon ve 124 alanın desteklenen bölümü eklenir.
- Kimlik, ürün, müşteri, fatura, sevk, kargo, ödeme, indirim, komisyon ve durum alanları veri kaybetmeden modele alınır.

### Toplu işlemler ve iadeler

- Çoklu seçimde ayrıntılı Excel, yazdırma/fatura önizlemesi, kargo hazırlık listesi, durum/not değişikliği ve yerel stok önizlemesi sunulur.
- Pazaryeri yazan işlemler yalnız mevcut immutable önizleme, açık onay, stale/revision, hesap ve idempotency kapılarından geçer.
- İadeler Siparişler ekranından açılan birleşik kuyruğa bağlanır; özgün sipariş kartına geri dönüş bulunur.
- Resmî API sözleşmesi olmayan pazaryerinde onay/ret yalnız yerel karar olarak işaretlenir; canlı işlem yapılmış gibi gösterilmez.

## Veri modeli

`OrderSnapshot`, `OrderItem` ve `OrderShipment` geriye uyumlu yeni alanlarla genişletilir. Mevcut JSON kayıtları varsayılan değerlerle okunmaya devam eder. Hesaplanan tutarlar saf bir `OrderTotalsCalculator` sınıfında tutulur; UI ve Excel import aynı hesaplamayı kullanır.

Kişisel veriler yalnız sipariş kaydında tutulur; global arama indeksine veya tanılama kayıtlarına eklenmez. API anahtarları ve parolalar sipariş modeline girmez.

## Görsel yön

- Segoe UI, yoğun masaüstü operasyon düzeni.
- Zemin `#F5F7F8`, yüzey `#FFFFFF`, metin `#17313B`, ana eylem `#16737B`, seçili alan `#DDEFF1`, uyarı `#C77700`, hata `#C43D3D`.
- Büyük boşluklar yerine 8–12 px aralıklar; 12–14 px veri metni; 18–22 px bölüm başlıkları.
- Ana vurgu tam genişlikte, okunabilir sipariş tablosudur.

## Kabul ölçütleri

1. Ana sipariş ekranında sabit sağ ayrıntı paneli bulunmaz.
2. Çift tıkla geniş sipariş kartı açılır ve tüm ana alanlar görünür.
3. Manuel sipariş aynı karttan kaydedilebilir.
4. Excel import Siparişler ekranından açılır, önizleme/onay/geri alma korunur.
5. KDV, net ve brüt toplam hesapları testlidir; tutarsızlık görünürdür.
6. Çoklu seçim ve iade kuyruğu ana ekrandan erişilir.
7. Eski sipariş JSON kayıtları bozulmadan açılır.
8. Tüm Release testleri ve self-contained Windows yayını başarılıdır.
