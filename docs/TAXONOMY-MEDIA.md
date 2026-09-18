# Kategori, marka ve ürün görselleri

## Eğitim kaynakları
Tam Türkçe otomatik altyazıları incelendi:
- [Kategori menüsü](https://entegrasyon.com.tr/?arama/kategori/entegra-kategori-menu-kullanimi): kategori listesi, üst kategoriye bağlama, ürün sayıları.
- [Kategori ekleme](https://entegrasyon.com.tr/?arama/kategori/entegra-kategori-ekleme): üst/alt ağaç, kategoriye bağlı pazaryeri karşılığı.
- [Kategori Excel](https://entegrasyon.com.tr/?arama/kategori/entegra-excel-kategori-ekleme): tek sütundan kategori ağacı.
- [Kanal içerik şablonları](https://entegrasyon.com.tr/?arama/kategori/entegra-platform-bazinda-urunlere-sablon-olusturma): başlık ön/son eki, sabit açıklama, ek açıklamalar, kategori veya tüm kategoriler.
- [Kategori eşleştirme](https://entegrasyon.com.tr/?arama/kategori/entegra-kategori-bazli-kategori-eslestirme): kanal kategori ID/yolu ve zorunlu/isteğe bağlı özellikler.
- [Markalar](https://entegrasyon.com.tr/?arama/markalar-kullanimi/entegra-markalar-menusu): ekleme, aktiflik, kanal marka ID/ad karşılıkları.

## Kategoriler ve Markalar
Ürün yönetiminin hemen altında ayrı sol menü girişleri bulunur. Ürünlerdeki kategori/markalar sözlüğe alınır. Ana ekran eğitimdeki gibi çoklu seçimli tablodur: ID, aktiflik, kategori ağacı/adı, alt kategori sayısı, ürün sayısı ve pazaryeri karşılıkları sütunlarda gösterilir. Ctrl/Shift veya Tümünü seç ile çoklu seçim yapılır. Üst kategori seçerek ekleme/taşıma yapılabilir. Yeniden adlandırma aynı işlem içinde bağlı ürünleri ve alt yolları günceller; kayıt/eşleme ID'leri korunur. Alt kategorisi veya kullanımı olan kayıt silinmez.

Seçilenleri silme, aktif/pasif yapma, kategoriye bağlama, kategori yollarını onarma, ürünleri tekrarsız sayma ve Excel dışa aktarma araç çubuğundadır. Kullanımda olan kayıt veya eski seçim varsa toplu yazma tümüyle geri döner. Üst ve alt kategori birlikte seçildiğinde taşıma bir kez uygulanır. Düzenleme aktif ile ad, aktiflik ve pazaryeri karşılıkları hücrelerde düzenlenir; değişiklikler topluca önizlenerek tek işlemde kaydedilir. Başka kaydın kanal eşlemesi üzerine yazılmaz.

Pazaryeri ve mağaza seçilerek kategori/marka ID ya da ad karşılığı kaydedilir. Bunlar yerel tanımlardır: canlı pazaryeri kategori/özellik kataloğunu indirme, OpenCart/ERP eşitleme ve dış platforma yayın bu ekranda yapılmaz.

İçerik şablonları kanal ve mağazaya özeldir. {Name}, {Sku}, {Brand}, {Description}, {Description2}, {Description3} yer tutucuları desteklenir. Marka karşılığı ve Ad=Değer özellikleri tanımlanabilir; zorunlu özellik boşsa kaydetme engellenir. Önizleme ana ürün metnini değiştirmez; XML başlık/açıklama kilitleri korunur. Sonuç Excel'e aktarılabilir. Şablon onayla seçilen kayıtlara veya tüm kategorilere tek transaction ile kopyalanabilir.

Sipariş modülünün yeniden yapılması kullanıcı tarafından kapsamdan çıkarılmıştır; mevcut menü ve sipariş ekranı korunur.

## Görsel saklama
Ürünlerin kaynak URL'leri korunur. Uygulama açıkken katalogdaki görseller arka planda indirilip veri dizini içindeki ProductImages klasörüne ürün kimliğine göre ayrı kopyalar olarak kaydedilir. Varsayılan veri dizini %LOCALAPPDATA%/MonoBridgeDesktop, test/özel çalışma alanında MARKETPLACEHUB_DATA_DIR değeridir. Bu bir uzak sunucu yayını değildir; programın çalıştığı bilgisayardadır.

Ulaşılamayan/uygunsuz bağlantılar işlem geçmişinde sayılır; ürün kaydı kaybolmaz. Başarılı yerel kopya sonraki ürün önizlemesinde kullanılabilir. Aynı URL iki üründe kullanılıyorsa her ürünün ayrı kopyası vardır.

Ürün silinince katalog kaydı, ProductImageCopies ve ProductMedia bağlantıları ile ürüne ait yerel kopyalar temizlenir. Harici orijinal dosyalara ve kaynak sunucudaki görsellere dokunulmaz. Temizlik hatası kalıcı kuyruğa bırakılır ve yeniden denenir. Excel/genel geri alma ile oluşan sahipsiz kopyalar da sonraki taramada temizlenir. Mevcut yayınlanmış Etsy ürünlerini silme engeli korunur.

## Desi ve Excel
Desi ürün kartında düzenlenebilir ve ürün listesinde ayrı sütundur. Excel alan tikleri kaldırılmıştır: eşlenen dolu hücre güncellenir, boş hücre korunur; sıfır geçerli değerdir. Barkod ürün güncellemesi için zorunlu değildir.
