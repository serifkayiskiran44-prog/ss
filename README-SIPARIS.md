# Siparişler ve kargo takibi

`Siparişler ve kargo` sekmesi, ürün havuzundan bağımsız sipariş kayıtları tutar. Kayıtlar Windows kullanıcı profilindeki `%LOCALAPPDATA%\MonoBridgeDesktop\orders.db` dosyasındadır; ürün veritabanı değiştirilmez. Başlangıçta örnek sipariş eklenmez.

## Kullanım

1. `+ Yerel sipariş` ile pazaryeri, mağaza ve sipariş numarası girin. Ürün adı, SKU ve adet ekleyebilirsiniz.
2. `+ Paket ekle` ile her gönderiyi ayrı tutun. Taşıyıcı ve takip numarasını girin. İsterseniz HTTPS takip bağlantısı ekleyin.
3. Kargo durumunu **Hazırlanıyor**, **Gönderildi**, **Yolda**, **Teslim edildi**, **Teslimat sorunu** veya **İade** seçip **Yerel kaydı / gözlemleri kaydet** düğmesine basın. Bu, kullanıcı gözlemidir; taşıyıcıdan doğrulanmış durum olarak gösterilmez. Paket ayrıntısında kayıt zamanı ve kaynak görünür.
4. Listede sipariş, mağaza, ürün, SKU ve takip numarasıyla arayın; kargo durumu filtresini kullanın. Çok paketli sipariş, ilgili paketlerden biri filtreye uyuyorsa listelenir.

## Etsy'den sipariş alma

Etsy API sekmesindeki mevcut OAuth bağlantısıyla **Etsy'den yenile** düğmesine basın. İstekler salt okunurdur; pazaryerine sipariş/kargo durumu yazılmaz. `transactions_r` izni gerekir. Tüm sayfalar başarıyla okunmadan hiçbir yeni API kaydı yazılmaz; hatada ve iptalde mevcut kayıtlar korunur. Alım 100 sayfa / 10.000 kayıtla ve üç dakikalık süreyle sınırlıdır. Daha büyük mağazada eksik sonuç kaydedilmez. Otomatik arka plan takip servisi yoktur; yenileme kullanıcı tarafından başlatılır.

Etsy receipt yanıtı, siparişin ham durumu, ödeme göstergesi, toplam/para birimi, ürünler ve gönderim bilgilerini sağlar. `completed` veya `is_shipped` **teslim edildi anlamına gelmez**. Taşıyıcının hareket geçmişi bu uç noktadan sağlanmadığından yolda/teslim gözlemleri manueldir. Yeni Etsy alımı, aynı pakete kaydedilen manuel durum ve takip bilgilerini korur. Eski tarihli API anlık görüntüsü yeni kaydı geriye götürmez. Aynı durum tekrar kaydedilirse geçmişte kopya gözlem oluşturulmaz.

10 Eylül 2026 doğrulamasında mevcut hesapla mağaza okuması başarılı, sipariş okuması HTTP 403 döndü. Sipariş izniyle bağlantının yeniden yetkilendirilmesi ve Etsy uygulama erişiminin kontrol edilmesi gerekir. Bu sürüm bu durumu açıkça bildirir; erişim olmamasını boş sipariş listesi gibi göstermez.

Ozon ve Navlungo için doğrulanmış otomatik sipariş/taşıyıcı takip bağlantısı bu sürümde yoktur. Bu kaynakların siparişleri yerel olarak eklenip takip edilebilir.

Kayıtlarda alıcı adresi, e-posta, telefon veya API yanıtının tamamı tutulmaz. Yerel sipariş veritabanı şifrelenmez; Windows kullanıcı profili erişim izinlerini koruyun. API kimlik bilgileri mevcut Windows korumalı kayıtta kalır.

Resmi API sözleşmesi: https://www.etsy.com/openapi/generated/oas/3.0.0.json — getShopReceipts, ShopReceipt.
