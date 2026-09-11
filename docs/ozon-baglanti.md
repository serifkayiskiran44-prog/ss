# Ozon bağlantısı

Pazaryeri bağlantı hazırlığı ekranında Ozon bölümüne Client ID ve API key girin. Güvenli kaydet düğmesi Windows kullanıcı profiline DPAPI ile şifreli kayıt yapar; bağlantı durumunu doğrulamaz. Ürün ve depo kontrolleri ayrı çalışır; her biri yalnızca kendi okuma erişimini doğrular.

- Ürün: POST https://api-seller.ozon.ru/v3/product/list; visibility ALL, limit 1. Yanıttaki toplam ürün sayısı gösterilir; katalog aktarılmaz.
- Depo: POST https://api-seller.ozon.ru/v1/warehouse/list; boş JSON. Dönen FBS/rFBS depo sayısı gösterilir; depo sorguları arasında en az bir dakika beklenir. Sıfır depo geçerli API erişimi olabilir.
- Başlıklar: Client-Id ve Api-Key. UI istemcisinde yönlendirmeler kapalıdır.
- Kayıt: %LOCALAPPDATA%/MonoBridgeDesktop/ozon.bin. Anahtar loglanmaz; HTTP hata gövdeleri gösterilmez.
- Product read-only ve Warehouse izinleri ilgili sorgular içindir. Warehouse grubunun ek yöntemler içerebildiğini Ozon izin ekranından kontrol edin; bu uygulama yalnızca liste sorgusu yapar.

Ürün yayınlama, stok/fiyat, sipariş aktarımı ve teslimat kurulumu bu sürümde uygulanmadı. Satıcı/ödeme onayı ve sevkiyata uygunluk bu kontrollerin kapsamı dışındadır.

Resmî belge adresi: https://docs.ozon.ru/api/seller/ . 2026-09-10 tarihinde dokümantasyon sayfası otomatik erişimde yönlendirme döngüsü, tarayıcıda boş ekran verdi; güncel sözleşme ve canlı hesap doğrulaması tamamlanamadı. HTTP sözleşmesi mock yanıtlarla test edildi. Gerçek anahtarla kontrollerin ayrıca çalıştırılması gerekir.
