# Marketplace.zip karşılaştırma raporu

## İncelenen kaynaklar

- Eski paket: `C:\Users\USER\Downloads\Marketplace.zip`
- Güncel proje: `C:\Users\USER\OneDrive\Desktop\Claude.ai\xml-bizimhesap`
- İnceleme tarihi: 21 Eylül 2026

## Paketin yapısı

`Marketplace.zip` 118.790.460 bayt ve 13.417 arşiv girdisi içeriyor. Paket hem çalıştırılabilir uygulamayı ve bağımlılıklarını hem de kurtarılmış/decompile edilmiş görünen kaynakları içeriyor.

- Ana proje: `MarketplaceRecovery.csproj`
- Hedef: .NET Framework 4.7.2, WPF, x64
- Arşiv genelinde yaklaşık 8.214 C# dosyası bulunuyor.
- Ana uygulama bölümünde 1.610 C# dosyasının 1.022 tanesi anlamsızlaştırılmış sınıf/dosya adları taşıyor.
- Çok sayıda dosya SOAP/WCF servislerinden üretilmiş veri sözleşmesi ve istemci sınıfı.
- Güncel proje .NET 8, modern `HttpClient`, SQLite işlem güvenliği, önizleme/onay ve testlerle farklı bir mimari kullanıyor.

Bu nedenle eski proje bütünüyle birleştirilemez. Yararlı iş akışları güncel mimaride yeniden uygulanmalı; eski API modelleri yalnızca alan ve davranış referansı olarak kullanılmalı.

## Özellik karşılaştırması

| Alan | Marketplace.zip | Güncel proje | Sonuç |
|---|---|---|---|
| Trendyol sipariş okuma | Var | Var; kimlik, fatura, ürün, KDV, kargo ve durum alanları daha açık modellenmiş | Güncel korunmalı |
| Sipariş kartı | Çok kapsamlı fakat büyük ve anlamsızlaştırılmış kod | Sekmeli, düzenlenebilir, NES ve stok işlemleriyle bağlantılı | Güncel korunmalı |
| Excel sipariş içe/dışa aktarma | Var | Var; önizleme, alan profili, seçili satır, günlük ve geri alma mevcut | Güncel daha güvenli |
| XML ürün aktarımı | Çok kapsamlı eski form | Tanım, alan eşleme, önizleme ve çalışma kaydı mevcut | Güncel korunmalı |
| NES e-Fatura/e-Arşiv | Gönderme, zamanlayıcı, durum ve numara sorgulama var | Hazırlama, önizleme, gönderme, PDF, mutabakat, Trendyol'a yükleme ve kalıcı sıralı kuyruk var | Güvenli sıra motoruyla uyarlandı |
| Fatura numarası eşleştirme | Zamanlayıcıyla sorguluyor | NES giden faturaları güvenli biçimde manuel eşleştiriyor | Otomatik zincire çevrilmeli |
| Toplu sipariş işlemleri | Gerçek fatura/kargo/yazdırma işlemleri var | Seçili siparişler için NES önizleme, açık onay ve kalıcı işlem kuyruğu eklendi | Fatura zinciri uyarlandı; diğerleri aşamalı |
| Kargo entegrasyonları | Aras, Yurtiçi, MNG, Sürat, UPS, PTT, Hepsijet ve başka istemciler var | Siparişte kargo alanları var; genel canlı kargo istemcisi yok | Kullanıcı talebiyle Aras kapsam dışı |
| Barkod/etiket/fatura yazdırma | Şablon editörü ve yazdırma altyapısı var | Gerçek yazdırma motoru yok | Yeniden uygulanmalı |
| İade/iptal/claim | Pazaryerine özgü çok sayıda işlem var | Yerel iade/iptal görünümü ve stok geri alma var | Canlı API işlemleri eksik |
| Rekabet analizi | Birden fazla pazaryeri için parçalar var | Trendyol rekabet verisi, fiyat önerisi ve güvenli önizleme var | Hepsiburada daha sonra eklenebilir |
| Sipariş arşivleme | Ayrı SQLite arşivleme sınıfı var | Yok | Veri büyüdüğünde eklenebilir |
| Otomasyon | Birçok bağımsız eski zamanlayıcı var | Lease, tekrar deneme ve çalışma pencereli güvenli zamanlayıcı var | Güncel motor üzerinde zincir kurulmalı |
| Diğer fatura sağlayıcıları | Uyumsoft, İzibiz, Foriba, EDM, Digital Planet, eLogo vb. | NES | Şu an gereksiz |
| Pazaryeri kapsamı | Çok sayıda eski kanal | Trendyol, Hepsiburada, Etsy ve mevcut hedef kanallar | Kullanılmayanlar eklenmemeli |

## Güncel projeye alınması önerilen işler

### 1. NES otomatik işlem zinciri

Eski `nesbilgi.cs` içinde şu davranışlar bulunuyor:

- fatura gönderme zamanlayıcısı,
- fatura durumunu sorgulama,
- UUID üzerinden fatura numarası alma,
- e-Fatura ve e-Arşiv durumlarını ayrı izleme.

Güncel projede bu işlevlerin API temeli zaten mevcut. Eksik olan, tek sipariş için aşağıdaki adımların sırayla çalışmasıdır:

1. Siparişi doğrula.
2. NES belgesini hazırla ve gönder.
3. Sonucu ve UUID'yi kaydet.
4. Fatura numarası kesinleşene kadar kontrollü aralıklarla sorgula.
5. PDF'i indir.
6. İlgili pazaryerine yükle.
7. Sipariş ve işlem geçmişini güncelle.
8. Bu sipariş tamamlanınca sıradaki siparişe geç.

Eski bağımsız timer yapısı kopyalanmamalı. Güncel `AutomationStore` lease ve retry mekanizması üzerinde tekil, sıralı bir kuyruk kurulmalı.

### 2. Toplu sipariş işlem merkezi

Güncel Siparişler ekranındaki seçili satırlar için gerçek işlemler eklenmeli:

- seçilileri NES'e gönder,
- fatura numaralarını yenile,
- faturaları pazaryerine yükle,
- kargo etiketi/fatura yazdır,
- sipariş durumunu güncelle,
- başarısız satırları ayrı listele ve yalnız onları yeniden dene.

Her işlem önizleme, kullanıcı onayı, tekilleştirme anahtarı ve işlem geçmişi kullanmalı.

### 3. Yazdırma merkezi

Aras Kargo entegrasyonu kullanıcı talebiyle kapsam dışıdır. İkinci taramada `RawPrinterHelper.cs`, sipariş raporu modelleri ve farklı barkod sonuç sözleşmeleri görüldü. Eski ham yazıcı kodu doğrudan alınmamalı; güncel uygulamada önizlemeli bir yazdırma merkezi kurulmalı:

- kargo etiketi,
- fatura,
- ürün barkodu,
- şablon seçimi,
- yazıcı seçimi ve tekrar yazdırma kaydı

eklenmeli.

### 4. Canlı iptal ve iade yönetimi

Güncel uygulama iptal/iade durumunu gösteriyor ve yerel stok geri alma yapabiliyor. Eksik olan pazaryeri API işlemleridir:

- tedarik edememe bildirimi,
- iptal nedeni gönderme,
- iade talebini okuma,
- iade onay/red işlemi,
- işlem sonucunu tekrar okuyarak doğrulama.

### 5. Sipariş arşivi

Eski `OrderArchiver.cs` fikri kullanılabilir; kodu mevcut veritabanı şemasına uygun biçimde yeniden yazılmalı. Aktif listeyi hızlandırmak için teslim edilmiş ve kapanmış eski siparişler arşiv veritabanına taşınabilir. Taşıma önce sayım/doğrulama yapmalı ve geri alınabilir olmalı.

### 6. Rekabet analizini genişletme

Trendyol rekabet analizi güncel projede zaten daha güvenli biçimde bulunuyor. Hepsiburada rekabet verisi resmi ve yetkili API üzerinden sağlanabiliyorsa aynı önizleme ve sınır mekanizmasıyla eklenebilir. Otomatik fiyat gönderimi varsayılan olarak kapalı kalmalı.

## Doğrudan alınmaması gereken parçalar

- Anlamsızlaştırılmış sınıflar ve şifrelenmiş metin çözücüleri
- Eski .NET Framework 4.7.2 uygulama kabuğu
- Güncelliği doğrulanmamış SOAP servis istemcileri
- Kullanılmayan pazaryeri ve ERP bağlantıları
- Eski kullanıcı arayüzü formları
- Eski bağımsız timer yapısı
- Pakette bulunan derlenmiş DLL ve EXE dosyaları

## Önerilen uygulama sırası

1. ~~NES sıralı otomatik kuyruk ve otomatik fatura numarası mutabakatı~~ — tamamlandı
2. ~~Sipariş ekranında gerçek toplu NES fatura işlemleri~~ — tamamlandı
3. ~~Önizlemeli barkod, kargo etiketi ve fatura yazdırma merkezi~~ — tamamlandı
4. ~~Trendyol canlı iptal ve iade işlemleri~~ — tamamlandı
5. ~~Sipariş arşivleme ve geri yükleme~~ — tamamlandı
6. ~~Hepsiburada rekabet analizi~~ — resmi canlı rakip uç noktası doğrulanana kadar sınırlandırılmış manuel analiz eklendi

## Uygulanan tasarım iyileştirmeleri

- Ana pencere önce görünür hale geliyor; ürün havuzu, arama dizini ve modül hazırlığı kullanıcı arayüzü açıldıktan sonra başlıyor.
- Yazdırma merkezi Code 128 barkod, kargo etiketi, fatura özeti, yazıcı/kopya seçimi ve yazdırma geçmişi sağlıyor.
- Sipariş arşivi geri yüklenebilir ve arşivlenen kayıtlar API yenilemesi sırasında etkin listeye kendiliğinden dönmüyor.
- Trendyol tedarik edememe, iade okuma, iade kabul ve ret işlemleri gerçek satır kimliği ve açık kullanıcı onayı gerektiriyor.

## İkinci tarama sonucu

Arşiv yeniden özellik ve ekran davranışı açısından tarandı. Sisteme değer katacak sonraki parçalar şunlardır:

- Yazdırmadan önce belge türü, yazıcı, kopya sayısı ve sayfa önizlemesi; başarılı/başarısız tekrar yazdırma geçmişi.
- Sipariş arşivinde tarih, mağaza ve son durum ölçütleriyle önizleme; sayım doğrulaması ve geri yükleme.
- İade/iptal merkezinde pazaryerinden gelen neden, karar süresi, ürün satırı ve stok geri alma kararının tek ekranda gösterilmesi.
- Rekabet analizinde yalnız resmi API verisi, min/maks fiyat sınırı ve gönderimden önce değişiklik özeti.
- Toplu işlemlerde hazır, engelli, geçici hata ve mutabakat gerekli satırlarının ayrı sayaçlarla gösterilmesi.

Eski `RawPrinterHelper` ve üretilmiş servis sınıfları güvenlik, güncellik ve bakım nedenleriyle kopyalanmayacaktır. Davranışlar .NET 8 üzerinde testli ve geri alınabilir biçimde yeniden yazılacaktır.
