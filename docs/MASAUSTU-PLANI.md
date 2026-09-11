# Masaüstü geçiş planı
Amaç: .NET 8 WPF Windows uygulaması; tarayıcı açılmaz.
Referans: Paylaşılan Kur marketplace entegrasyon sistemi konuşması ve C:\Entegra-2 XML/veri formatları. Önceki API/menülerin çoğu işlevsizdi.
- [ ] WPF menülerini gerçek ekran navigasyonuna bağla; çalışmayan işlevleri açıkça belirt.
- [ ] Etsy API key/secret/token/shop ID saklama: Windows DPAPI. Gerçek read-only mağaza testi, HTTP hata sınıfları. Sahte başarı yok.
- [ ] Ürün havuzunun masaüstünde görülebilmesi; XML dosya seçimi; arama; SQL sadece okuma.
- [ ] Testler, Release EXE, gerçek pencere kontrolü.
Sonraki ana işler: WPF XML eşleştirme/kurallar, kalıcı pazaryeri eşleştirme, OAuth yenileme, ürün/sipariş/kargo/fatura akışları.
Canlı hesap doğrulaması için kullanıcıya ait Etsy uygulama erişimi gerekir. Öncelik Etsy, eBay/Joom ve global kanallar; Türkiye son.
