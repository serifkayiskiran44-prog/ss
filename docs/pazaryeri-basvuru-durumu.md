# Pazaryeri başvuru durumu — 10 Eylül 2026

## eBay — doğrulanan güncel durum

- Hesap Google girişiyle oluşturuldu. Kullanıcı adı `er874328` iken `monohandmade` olarak değiştirildi; yeniden giriş sonrası profil ekranından doğrulandı. Sonraki ad değişikliği tarihi: 10 Ekim 2026.
- E-posta ve telefon doğrulandı. Kullanıcının verdiği adres kaydedildi.
- Ücretsiz Seller Hub etkinleştirildi.
- Mevcut `Magaza/logo-acik.png` logosu ve Mono hakkında İngilizce açıklama profile kaydedildi; ekran görüntüsüyle doğrulandı.
- Kapaklı Store düzenleyicisi abonelik seçimine yönlendiriyor. Abonelik satın alınmadı.
- Satıcı ödeme kaydı ve işletme dönüşümü tamamlanmadı. Gelir alma ve ödeme yöntemi bulunmuyor; ödeme yöntemi ekleme penceresi boş yüklendi.
- Navlungo eBay OAuth izinleri onaylandı; Navlungo dönüşünde “İzin vermiş olduğunuz hesabınıza ait bir mağaza bulunamadı” hatası verdi. Başarılı bağlantı yok. Mağaza aboneliğinin bu hatanın kesin sebebi olduğu doğrulanmadı.
- eBay geliştirici ana sayfası ve kayıt sayfası tarayıcıda “Our server is down” hatası verdi. Geliştirici hesabı / API anahtarları oluşturulmadı. MonoBridge eBay canlı API bağlantısı yok.
- Canlı ürün yayımlanmadı. Etsy örnek ürünü test taslağıdır; gerçek eBay ilanı olarak yayımlanmamalıdır.

## Kullanıcıdan alınmış işletme bilgileri

Şahıs işletmesi; resmi unvan Şerif Kayışkıran; vergi dairesi Bayrampaşa. El yapımı ürünler, 3 yıl deneyim, yaklaşık 1000 farklı ürün ve yaklaşık 200 bin USD yıllık ciro kullanıcı beyanıdır. Ana mevcut platform Trendyol. Marka tercihi Mono; ülke adı marka adına eklenmeyecek. Vergi numarası, banka/Payoneer bilgileri ve resmi belgeler bu notta bulunmaz.

## Diğer kanallar

| Kanal | Durum |
|---|---|
| Etsy | Önceden API bağlantısı ve tek test taslağı doğrulandı. |
| Navlungo–Etsy | Önceden panel bağlantısı doğrulandı; MonoBridge–Navlungo doğrudan API bağlantısı tamamlanmadı. |
| Fruugo | Kullanıcı telefonda kaydı tamamladığını bildirdi. Yerel oturum ve satıcı onayı doğrulanmadı. |
| Wish | Davet formu incelendi; gönderilmiş başvuru doğrulanmadı. |
| Joom | Başvuru formu incelendi; gönderilmiş başvuru doğrulanmadı. Gerçek fiyat aralığı ve gerekli kanal ayrıntıları eksik. |
| Allegro | Ticari kayıt formu incelendi; kayıt tamamlanmadı. |
| Ozon | Türkiye kayıt akışı incelendi; kayıt tamamlanmadı. |

## Sonraki adımlar

1. eBay satıcı ödeme/işletme kayıt akışını tamamlamak; gerekirse gerçek ürün bilgileriyle ilerlemek. Resmi rehber: https://export.ebay.com/en/first-steps/how-to-create-seller-account/
2. Geliştirici kayıt sayfası erişilebilir olduğunda ücretsiz API başvurusunu tamamlamak: https://www.developer.ebay.com/signin?tab=register
3. Navlungo mağaza bulunamadı hatasının gerçek nedenini belirleyip bağlantıyı doğrulamak.
4. API kimlik bilgileri hazır olduğunda programın eBay OAuth ve ürün/sipariş istemcisini uygulamak ve doğrulamak. Mevcut pazaryeri kurulum kartları canlı entegrasyon değildir.

Parolalar, kimlik numarası ve erişim anahtarları bu belgeye veya kaynak koda kaydedilmez.

## Son oturum doğrulaması

- Kullanıcı mevcut Payoneer hesabına giriş yaptı. Para çekmek için banka hesapları ekranında Garanti Bankası TRY hesabı Aktif olarak görüldü. IBAN bu rapora kopyalanmadı; yeni banka hesabı eklenmedi.
- eBay–Payoneer eşleştirmesi tamamlanmadı. /startpayments, /sh/fin/dlp bilgi ekranına yönlendiriyor; kayıt düğmesi yok.
- Son tekrar: www.developer.ebay.com/signin?tab=register ve www.edp.ebay.com/signin tarayıcıda /n/error?statuscode=500 sayfasına yönlendi. Bu, denenen oturumun sonucu; genel kesinti doğrulanmış değildir.
- Kullanıcı Trendyol 78172080 numaralı MEMEME FASHION ürününü örnek seçti. Beden standart, tekli, stok 1 beyan edildi. Trendyol satış fiyatının alış maliyeti olarak alınması istendi. Sayfada asıl fiyat ve fotoğraf yüklenmedi; önerilen diğer ürünlerin fiyatları kullanılmadı. Etiketli/etiketsiz durumu bilinmiyor. eBay kategori araması 63854 sonucuna ulaştı, ilan yayımlanmadı.
