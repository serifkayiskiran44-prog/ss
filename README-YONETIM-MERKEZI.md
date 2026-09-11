# MonoBridge Yönetim Merkezi — .NET 8

## Kullanım
TrMarketplaceHubDesktop.exe dosyasını açın. Sol menüde Ürün yönetimi, XML yönetimi, Etsy, eBay, Ozon, Joom, diğer pazaryerleri, sipariş/kargo ve ayarlar bulunur.

Ürün yönetimi merkez havuzdur. XML kaynaklarını eşleştirip önizledikten sonra havuza aktarın. Kanal ekranlarında aynı ürünü seçerek mağazaya özel kategori, fiyat, para birimi, stok ve ilan eşleştirme planı saklayabilirsiniz. Bu yerel planlar merkez ürününü değiştirmez ve pazaryerine gönderilmez. Kanal başına farklı mağaza anahtarı kullanabilirsiniz.

## Gerçek bağlantı sınırları
- Etsy: mevcut taslak/ilan ve OAuth işlevleri korunmuştur. Son canlı mağaza testi başarılı; sipariş okuma HTTP 403 verdi. Sipariş erişimi ayrıca çözülmelidir.
- eBay ve Ozon: mevcut bağlantı kontrol ekranları korunmuştur. Yeni ürün kartları yerel hazırlıktır; canlı ürün/fiyat/stok/sipariş senkronizasyonu tamamlanmamıştır.
- Joom: başvuru formu hazırlanmıştır, henüz gönderilmemiştir. Trendyol mağaza URL'si ve genel ürün fiyat aralığı eksiktir. Satıcı kabulü ve API yetkilendirmesi yoktur.
- Sipariş ekranı yerel kayıt, paket ve manuel teslim gözlemlerini saklar. Ozon/Navlungo otomatik kargo takip bağlantısı yoktur.
- XML zamanlayıcısı yalnız program açıkken çalışır.

Veriler yayın klasörü dışında, kullanıcının LocalAppData/MonoBridgeDesktop dizininde tutulur. Kanal planları ayrı channel_products.db dosyasındadır. Yeni sürüm önceki ürün havuzunu kullanır.

## Tasarım dayanağı
Entegra'nın resmi genel özelliklerindeki merkezi ürün havuzu, XML, kanal yönetimi ve sipariş/kargo iş akışlarından yararlanılarak bağımsız bir arayüz geliştirilmiştir. Entegra ile tüm işlevlerde eşitlik veya kaynak kodunun elde edildiği iddia edilmez.
https://www.entegrabilisim.com/genel-ozellikler
https://www.entegrabilisim.com/entegrasyon
