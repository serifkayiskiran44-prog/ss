# MonoBridge içinde gömülü Odoo tasarımı

## Amaç

MonoBridge kullanıcısı muhasebe ve Odoo işlemlerini ayrı bir tarayıcıdan açmadan mevcut .NET 8 WPF masaüstü uygulamasının içinde kullanabilmelidir. Kullanıcı masaüstünde tek MonoBridge uygulaması görür; Odoo ve PostgreSQL yerel servisleri arka planda çalışır.

Bu çalışma mevcut ürün, XML, Excel, sipariş, Trendyol, Etsy ve BizimHesap ekranlarını değiştirmez. MonoBridge uygulamasına yeni bir giriş veya üyelik sistemi eklenmez. Odoo'nun yerel yönetici oturumu gömülü tarayıcı profilinde korunur.

## Kullanıcı deneyimi

- Sol menüde `YÖNETİM` grubuna **Muhasebe / Odoo** sayfası eklenir.
- Sayfa seçildiğinde MonoBridge önce `http://127.0.0.1:8069/web/login` sağlık kontrolünü yapar.
- Odoo çalışıyorsa sayfa gömülü WebView2 içinde açılır.
- Odoo çalışmıyorsa MonoBridge yerel çalışma bildirimini okuyup PostgreSQL ve Odoo'yu arka planda başlatır.
- Başlatma sürerken Türkçe durum ekranı gösterilir. Başarılı olursa Odoo görünümü otomatik açılır.
- Başlatma başarısız olursa kullanıcıya sade hata, günlük konumu ve **Yeniden dene** düğmesi gösterilir.
- İlk Odoo girişinden sonra WebView2 kullanıcı verisi `%LOCALAPPDATA%\MonoBridgeDesktop\WebView2\Odoo` altında kalır. Odoo oturum çerezi geçerli olduğu sürece yeniden parola sorulmaz.
- Odoo bağlantıları gömülü görünümde kalır. Harici internet bağlantıları varsayılan tarayıcıda açılır.

## Mimari

### 1. Masaüstü görünümü

`OdooWorkspacePanel` bir WPF `UserControl` olur. Panel üç görünüm arasında geçiş yapar:

1. Başlatılıyor/durum görünümü
2. Gömülü WebView2 görünümü
3. Hata ve yeniden deneme görünümü

Panel yalnız kullanıcı arayüzünden sorumludur. Süreç başlatma, çalışma bildirimi ve sağlık kontrolü ayrı servislerde tutulur.

### 2. Yerel çalışma bildirimi

Odoo kurulumu `%LOCALAPPDATA%\MonoBridgeOdoo\runtime.json` dosyası üretir. Dosyada yalnız gizli olmayan çalışma bilgileri bulunur:

- Şema sürümü
- Odoo adresi
- Proje/kurulum kökü
- Başlatma ve durdurma betiği yolları
- Günlük dosyası yolu
- PostgreSQL servis adı

Parola, API anahtarı, oturum çerezi veya veritabanı sırrı bu dosyaya yazılmaz. Mevcut `%LOCALAPPDATA%\MonoBridgeOdoo\runtime-secrets.json` WPF tarafından okunmaz.

### 3. Süreç yönetimi

`OdooRuntimeService` şu sırayı uygular:

1. Çalışma bildirimini yükle ve yolları doğrula.
2. `/web/login` için kısa zaman aşımlı HTTP sağlık kontrolü yap.
3. Sağlıklı değilse kayıtlı başlatma betiğini gizli PowerShell penceresinde çalıştır.
4. Sabit uyku yerine en fazla 60 saniye koşula bağlı sağlık kontrolü yap.
5. Başarı veya güvenli hata sonucu döndür.

Aynı anda birden fazla başlatma isteği tek görevde birleştirilir. MonoBridge kapanırken Odoo zorla durdurulmaz; böylece XML ve muhasebe işleri yarıda kalmaz. Kullanıcı isterse ayrı yönetim düğmesiyle servisi durdurabilir.

### 4. WebView2 sınırları

- Yalnız `http://127.0.0.1:8069` ve `http://localhost:8069` adresleri panel içinde kalır.
- Yeni pencere isteyen aynı köken bağlantıları aynı WebView2 içinde açılır.
- Farklı kökenler Windows varsayılan tarayıcısına yönlendirilir.
- Kamera, mikrofon ve konum izinleri varsayılan olarak reddedilir.
- Dosya yükleme ve indirme Odoo işlevleri için açık kalır; indirmeler kullanıcının seçtiği normal Windows konumuna gider.
- Geliştirici araçları Release yayında kapalı tutulur.

## Giriş ve güvenlik

MonoBridge'e yeni uygulama parolası eklenmez. Odoo ayrı bir yerel yetkilendirme sınırı olarak kalır. İlk kullanımda kullanıcı `admin` hesabıyla bir kez giriş yapar; parola masaüstündeki güvenli kopyalama aracıyla panoya alınır ve kod/repo/günlük içine yazılmaz.

Genel Odoo üyeliği kapalı kalır. `/web/signup` kayıt formu sunmaz. Gömülü panel Odoo parolasını okumaz, forma enjekte etmez veya kalıcı düz metin olarak saklamaz.

## Paketleme ve kurulum

- WPF projesine sabit sürümlü `Microsoft.Web.WebView2` paketi eklenir.
- MonoBridge yayını WebView2 yükleyici dosyasını sessizce kurmaya çalışmaz. Açılışta runtime yoksa kullanıcıya Microsoft WebView2 Runtime gerektiğini belirten Türkçe hata gösterir.
- Odoo projesinin bootstrap adımı `runtime.json` üretir.
- Mevcut çalışan Odoo kurulumu korunur; PostgreSQL veri dizini yeniden oluşturulmaz.
- Yayın klasörü yine self-contained `win-x64` olur ve mevcut `Windows-Excel-Final` sürümünün üzerine yazılmaz. Yeni yayın `Windows-Odoo-Embedded` dizinine alınır.

## Hata davranışı

- Bildirim yok: “Odoo yerel kurulumu bulunamadı” ve beklenen konum gösterilir.
- Başlatma betiği yok/geçersiz: işlem yapılmaz, düzeltilebilir hata gösterilir.
- PostgreSQL servisi erişilemez: yükseltilmiş yetki gereksinimi açıkça belirtilir; parola veya komut çıktısı gösterilmez.
- 60 saniyede sağlık kontrolü geçmez: günlük bağlantısı ve yeniden deneme sunulur.
- WebView2 runtime yok: kurulum gereksinimi gösterilir; sistem tarayıcısına sessiz geri dönüş yapılmaz.
- Navigasyon farklı bir kökene giderse gömülü görünüm terk edilmez; bağlantı dış tarayıcıya açılır.

## Test yaklaşımı

### Otomatik testler

- Geçerli/geçersiz/eksik `runtime.json` ayrıştırma testleri
- Gizli alanların bildirim modelinde bulunmadığını doğrulayan sözleşme testi
- Sağlıklı Odoo'da süreç başlatılmadığını doğrulayan servis testi
- Sağlıksız Odoo'da tek başlatma ve koşula bağlı bekleme testi
- Eşzamanlı çağrıların tek başlatmaya birleşmesi testi
- İzin verilen ve harici URI yönlendirme testleri
- Navigasyon ve ekran paritesi testi: `odoo` rotası sol menüde bulunur
- Mevcut tüm Release testleri

Testlerde gerçek PostgreSQL servisi, gerçek Odoo veritabanı veya canlı mağazalar değiştirilmez. HTTP ve süreç çalıştırıcıları test doubles üzerinden sınanır.

### Yayın doğrulaması

1. Release testleri eksiksiz çalıştırılır.
2. `Windows-Odoo-Embedded` self-contained yayını oluşturulur.
3. Yayınlanan EXE açılır.
4. Muhasebe / Odoo sayfasında yerel Odoo giriş ekranı görünür.
5. Odoo oturumu açıldıktan sonra MonoBridge kapatılıp yeniden açılır; geçerli oturumun korunduğu doğrulanır.
6. Tarayıcı penceresi veya Odoo üyelik ekranı açılmadığı görsel olarak kontrol edilir.

## Kapsam dışı

- Odoo ekranlarının WPF ile baştan yazılması
- Odoo Community kaynak kodunun MonoBridge EXE içine tek dosya olarak sıkıştırılması
- Odoo yetkilendirmesini kaldıran otomatik giriş arka kapısı
- Mevcut SQLite katalog verisinin bu adımda Odoo'ya taşınması
- NES veya pazaryeri canlı yazım davranışlarının değiştirilmesi

Bu sınırlar masaüstü kabuğunu güvenli ve küçük tutar. MonoBridge ile Odoo arasındaki ürün, stok, sipariş ve muhasebe veri eşitlemesi ayrı uygulama aşamalarında mevcut güvenlik kapılarıyla geliştirilir.
