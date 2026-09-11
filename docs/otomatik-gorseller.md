# Otomatik pazaryeri görselleri

10 Eylül 2026 kuralları temel alınmıştır. Orijinal dosya değiştirilmez. JPEG kopyası hazırlanır; oran korunur, kırpma yapılmaz, şeffaf alan beyaza çevrilir, EXIF yönü uygulanır ve yeni dosyada metadata tutulmaz. Hareketli görselin ilk karesi kullanılır. Windows'un okuyabildiği biçimler desteklenir; WebP codec bulunmasına bağlıdır. Desteklenmeyen veya bozuk dosya hata verir.

- eBay: uygulama kısa kenarı en az 500 piksele getirir. Küçük görsel büyütülürken yeni detay oluşmadığı bildirilir. eBay politika metni uzun kenarda en az 500 piksel; yükleme yardım sayfası 500×500 ve öneri olarak 1600×1600 belirtir. Uygulama daha muhafazakâr 500×500 alt sınırını uygular.
- Etsy: 2000×2000 veya üstü önerilir, zorunlu minimum olarak uygulanmaz. Küçük fotoğraf yapay olarak 2000'e büyütülmez; kalite uyarısı gösterilir. 1 MB üzerindeki dosyalarda yükleme yavaşlığı uyarısı gösterilir; bu bir engelleme sınırı değildir.
- Uygulamanın kendi koruma sınırları: 20 MB giriş, 40 megapiksel ve kenarda en fazla 20000 piksel; çıktı en fazla 4096 uzun kenar ve 7 MB. JPEG kalite kademeleri 90–50. eBay için aşırı dar/uzun görsel kırpılmadan her iki sınırı sağlayamıyorsa açık hata verilir.

Pazaryerleri ekranındaki “Otomatik görsel hazırlama” bölümünde dosya seçilince eBay/Etsy kopyası otomatik hazırlanır. Kopyalar `%LOCALAPPDATA%/MonoBridgeDesktop/PreparedImages` altında benzersiz adla saklanır. Kaynak dosya aynı yerde kalır.

Etsy'nin mevcut “ilk görselle taslak oluştur” akışı gönderimden önce aynı dönüşümü çalıştırır; decode hatasında taslak oluşturulmaz. Başarı/kalite mesajı ekranda gösterilir. Ek fotoğraflar henüz otomatik gönderilmez. eBay'de bu sürüm dosya hazırlama sağlar; API ile ilan/fotoğraf gönderimi henüz uygulanmamıştır. Düşük seviyeli Etsy `UploadImageAsync` mevcut hazırlanmış bayt yükleme işlevini korur.

Resmî kaynaklar:
- https://www.ebay.com/help/policies/selling-policies/listing-policy?id=4370
- https://www.ebay.com/help/selling/listings/adding-pictures-listings?id=4148
- https://help.etsy.com/hc/en-us/articles/115015663347-Requirements-and-Best-Practices-for-Images-in-Your-Etsy-Shop

Doğrulama: 184 test geçti, Release derleme ve win-x64 self-contained yayın başarılı. Kullanıcının gerçek 447×447 fotoğrafı eBay için 500×500 JPEG (~71 KB) üretildi; orijinal SHA-256 eşleşmesi korundu. Yayın/hesap API çağrısı yapılmadı.
