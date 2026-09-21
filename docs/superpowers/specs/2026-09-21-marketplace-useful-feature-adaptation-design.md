# Marketplace Yararlı Özellik Uyarlama Tasarımı

**Tarih:** 21 Eylül 2026  
**Durum:** Onaylanan sohbet tasarımının yazılı şartnamesi  
**Kaynak inceleme:** `C:\Users\USER\Downloads\Marketplace.zip`  
**Hedef uygulama:** `TrMarketplaceHubDesktop` (.NET 8 WPF)

## Amaç

Eski Marketplace paketinde bulunan yararlı iş akışlarını güncel uygulamanın güvenli mimarisine uyarlamak. Aras Kargo entegrasyonu kapsam dışındadır. Eski .NET Framework, anlamsızlaştırılmış sınıflar, derlenmiş dosyalar ve doğrulanmamış servis istemcileri hedef projeye taşınmayacaktır.

## Başarı ölçütleri

1. Kullanıcının seçip onayladığı siparişler tekil ve sıralı NES işlem kuyruğunda çalışır.
2. Aynı sipariş için aynı fatura ikinci kez oluşturulamaz veya gönderilemez.
3. NES gönderimi, numara mutabakatı, PDF indirme ve Trendyol yükleme adımları kalıcı durumlarla izlenir.
4. Toplu işlemlerde her sipariş bağımsız sonuç üretir; bir hata diğer siparişlerin kayıtlarını bozmaz.
5. Yazdırma işlemleri önizleme ve açık kullanıcı seçimi olmadan yazıcıya gönderilmez.
6. Canlı iptal ve iade işlemleri pazaryeri cevabı doğrulanmadan yerel stok değiştirmez.
7. Arşivleme sayım, bütünlük doğrulaması ve geri yükleme desteğiyle yapılır.
8. Rekabet analizi yalnız resmi ve yetkili API verisini kullanır; otomatik fiyat gönderimi varsayılan olarak kapalıdır.
9. Mevcut sipariş, Excel, XML, NES ve Trendyol davranışları geriye dönük testlerle korunur.

## Kapsam

### Dahil

- NES sıralı otomatik işlem kuyruğu
- Sipariş ekranında gerçek toplu fatura işlemleri
- Fatura, ürün barkodu ve genel kargo etiketi yazdırma altyapısı
- Trendyol canlı iptal ve iade işlemleri
- Sipariş arşivleme ve geri yükleme
- Hepsiburada rekabet analizi için resmi API tabanlı güvenli iş akışı
- İşlem geçmişi, hata ayrıntıları, tekrar deneme ve tekilleştirme

### Hariç

- Aras Kargo API entegrasyonu
- Eski paketteki DLL/EXE dosyalarının projeye eklenmesi
- Eski SOAP/WCF istemcilerinin doğrudan kullanılması
- Eski bağımsız timer yapısının kopyalanması
- Uyumsoft, İzibiz, Foriba, EDM, Digital Planet veya eLogo entegrasyonları
- Kullanılmayan pazaryerlerinin eklenmesi
- Onaysız otomatik fatura veya fiyat gönderimi

## Yaklaşım seçenekleri ve karar

### Seçenek A — Güncel mimaride aşamalı yeniden uygulama

Her özellik küçük servisler, SQLite durum kayıtları, önizleme/onay ve testlerle yeniden uygulanır. Eski paket yalnız davranış ve alan referansı olur.

**Karar:** Bu yaklaşım kullanılacaktır. Güncel güvenlik ve veri bütünlüğü korunur; her aşama bağımsız doğrulanabilir.

### Seçenek B — Eski kodu doğrudan taşıma

Reddedildi. Eski kod .NET Framework 4.7.2, büyük ölçüde anlamsızlaştırılmış ve eski servis bağımlılıkları taşıyor. Mevcut .NET 8 veri modeli ve güvenlik kurallarıyla uyumlu değil.

### Seçenek C — Tek büyük otomasyon servisi

Reddedildi. Fatura, yazdırma, iade ve arşivleme birbirinden bağımsız hata ve yetki sınırlarına sahip. Tek servis değişiklikleri riskli ve testleri zor hale getirir.

## Mimari ilkeler

- Her dış işlem kalıcı bir işlem kimliği ve tekilleştirme anahtarı kullanır.
- Her API çağrısı zaman aşımı, iptal belirteci, yönlendirme engeli ve temizlenmiş hata mesajı kullanır.
- Kimlik bilgileri mevcut korumalı ayar depolarında kalır; günlük ve veritabanına açık anahtar yazılmaz.
- Kullanıcı onayı, dış sistemde değişiklik yapan komuttan hemen önce alınır.
- Önizleme ile gönderim arasında mağaza, sipariş veya ürün değişirse işlem durur.
- Dış sistem sonucu belirsizse kayıt `Unknown` olur; otomatik yeniden gönderilmez, önce uzaktan mutabakat yapılır.
- Veritabanı değişiklikleri işlem içinde uygulanır ve sürüm/lease kontrolü kullanır.
- Arka plan işleri aynı anda aynı siparişi çalıştıramaz.

## Alt sistem 1: NES sıralı işlem kuyruğu

### Bileşenler

- `NesQueueStore`: Kuyruk öğelerini ve adım durumlarını SQLite'ta saklar.
- `NesQueueProcessor`: Tek seferde bir kuyruk öğesini lease ile işler.
- `NesQueuePlanner`: Seçili siparişleri doğrular, yapılacak adımları ve engelleri önizler.
- `NesQueuePanel`: Siparişler ekranında toplu ekleme, başlatma/durdurma, yeniden deneme ve ayrıntı gösterir.
- Mevcut `NesConnection`, `NesDocumentStore`, `NesOrderReconciler` ve `TrendyolInvoiceClient` yeniden kullanılır.

### Kalıcı durum modeli

Her kuyruk kaydı aşağıdaki alanları taşır:

- Kuyruk kimliği
- Pazaryeri, mağaza, sipariş ve paket kimliği
- Belge türü
- Sipariş anlık görüntü parmak izi
- Tekilleştirme anahtarı
- Geçerli adım
- Genel durum
- Deneme sayısı ve sonraki deneme zamanı
- Lease belirteci ve lease bitişi
- NES UUID ve fatura numarası
- Yerel PDF yolu ve SHA-256 özeti
- Pazaryeri yükleme sonucu
- Temizlenmiş son hata
- Oluşturma ve güncelleme zamanları

### Durumlar

`Planned`, `Preparing`, `Sending`, `AwaitingNumber`, `DownloadingPdf`, `UploadingMarketplace`, `Completed`, `RetryWaiting`, `Blocked`, `Unknown`, `Cancelled`.

### İşlem akışı

1. Kullanıcı Siparişler ekranından bir veya daha fazla sipariş seçer.
2. Sistem fatura türü, alıcı kimliği, satırlar, KDV, toplam, NES ayarları ve paket kimliğini doğrular.
3. Sistem her sipariş için adım önizlemesi ve engel listesini gösterir.
4. Kullanıcı açık onay verir.
5. Siparişler tekilleştirme anahtarıyla kuyruğa eklenir.
6. İşleyici ilk uygun kaydı lease ile sahiplenir.
7. Var olan NES belgesi veya uzaktaki belge mutabakatla aranır.
8. Belge yoksa mevcut güvenli hazırlama/önizleme/gönderme akışı çalışır.
9. UUID kaydedilir; numara kesin değilse kontrollü aralıklarla durum sorgulanır.
10. Numara kesinleşince sipariş kaydına bağlanır.
11. PDF indirilir, PDF imzası ve boyut sınırı doğrulanır.
12. Trendyol siparişiyse ve plan bunu içeriyorsa PDF paket API'sine yüklenir.
13. İşlem geçmişi yazılır ve öğe tamamlanır.
14. Sonraki kuyruk öğesi ancak bu öğe tamamlandıktan, bloke olduktan veya tekrar deneme zamanına alındıktan sonra başlar.

### Hata davranışı

- Doğrulama hatası: `Blocked`, otomatik tekrar yok.
- 401/403: `Blocked`, bağlantı ayarı düzeltilmeli.
- 429/5xx/zaman aşımı: sınırlı üstel gecikmeli `RetryWaiting`.
- Gönderim sırasında bağlantı sonucu bilinmiyorsa: `Unknown`, önce uzaktan mutabakat.
- Trendyol PDF yüklemesi başarısızsa fatura yeniden kesilmez; yalnız yükleme adımı yeniden denenir.
- Kullanıcı durdurduğunda çalışan HTTP isteği iptal edilir, kalıcı kayıt güvenli adıma döner.

## Alt sistem 2: Toplu sipariş işlem merkezi

Siparişler ekranında seçim temelli komutlar bulunur:

- NES kuyruğuna ekle
- NES fatura numaralarını yenile
- Fatura PDF'lerini indir
- Pazaryerine faturaları yükle
- Yazdırma önizlemesi oluştur
- Hatalı veya bloke kayıtları göster
- Yalnız 429, 5xx ve zaman aşımı nedeniyle `RetryWaiting` durumuna geçen kayıtları yeniden dene

Her komut önce seçili satırları ve yapacağı değişiklikleri gösterir. Sonuç penceresi sipariş başına başarılı, atlandı, bloke ve başarısız durumlarını gösterir. Aynı işlem kimliğiyle yinelenen komut dış sisteme tekrar gönderilmez.

## Alt sistem 3: Yazdırma altyapısı

### Belge türleri

- NES fatura PDF'i
- Ürün barkod etiketi
- Genel kargo etiketi dosyası

### Davranış

- Şablonlar sürümlü JSON olarak saklanır.
- Ürün etiketi şablonunda barkod, SKU, ürün adı, fiyat ve adet alanları bulunur.
- Yazdırmadan önce sayfa önizlemesi ve adet gösterilir.
- Kullanıcı yazıcıyı açıkça seçer.
- PDF ve raster önizleme dosyaları geçici, güvenli klasörde oluşturulur.
- Yazdırma sonucu işlem geçmişine yazılır; tekrar yazdırma ayrı olaydır.
- Yazıcı hatası fatura veya sipariş durumunu değiştirmez.

Aras Kargo veya başka bir kargo sağlayıcısına canlı gönderi oluşturma bu tasarımın parçası değildir. Harici kaynaktan elde edilmiş geçerli etiket dosyası varsa genel yazdırma motoru bunu yazdırabilir.

## Alt sistem 4: Trendyol canlı iptal ve iade

### İptal

- Kullanıcı siparişi ve satırı seçer.
- Güncel Trendyol durumu tekrar okunur.
- API'nin izin verdiği nedenler uzaktan alınır veya güncel sözleşmeden doğrulanmış sabit liste kullanılır.
- Kullanıcı neden ve miktarı onaylar.
- İstek tekilleştirme anahtarıyla gönderilir.
- Başarıdan sonra sipariş tekrar okunur; kırmızı iptal görünümü doğrulanmış duruma göre uygulanır.

### İade

- İade talepleri API'den okunur ve yerel istisna kaydıyla eşleştirilir.
- Onay veya red işlemi kullanıcı onayı gerektirir.
- Stok geri alma yalnız pazaryeri sonucu başarılı ve ürün eşleşmesi tekil olduğunda uygulanır.
- Aynı satır ikinci kez stoğa eklenemez.

Canlı API sözleşmesinde desteklenmeyen işlem arayüzde sunulmaz.

## Alt sistem 5: Sipariş arşivleme

- Yalnız tamamlanmış, teslim edilmiş, iptal edilmiş veya iadesi kapanmış ve belirlenen tarihten eski siparişler adaydır.
- Açık NES kuyruğu, belirsiz fatura, açık iade veya bekleyen stok işlemi olan sipariş arşivlenemez.
- Önizleme sipariş, ürün satırı, sevkiyat, olay, fatura ve stok makbuzu sayılarını gösterir.
- Kayıtlar ayrı SQLite arşivine işlem içinde kopyalanır.
- Kaynak silinmeden önce hedef sayıları ve içerik özetleri doğrulanır.
- Arşiv dosyası bütünlük kontrolünden geçmezse kaynak kayıtlar korunur.
- Geri yükleme çakışmaları önizlenir; mevcut daha yeni kayıtların üzerine yazılmaz.

## Alt sistem 6: Hepsiburada rekabet analizi

- Uygulama başlamadan önce güncel resmi API'nin mağaza hesabı için bu veriyi sağladığı doğrulanır.
- Ürün eşleştirmesi yalnız kayıtlı Hepsiburada mağaza ürünü/barkodu üzerinden yapılır.
- Satıcı adı veya rakip ayrıntısı API'de yoksa uydurulmaz.
- Minimum fiyat, maksimum fiyat, fark ve minimum kâr sınırı uygulanır.
- Sonuç yalnız öneri üretir; otomatik fiyat gönderimi kapalıdır.
- Kullanıcı seçili satırları onayladığında mevcut fiyat önizleme/gönderim altyapısı kullanılır.
- API bu özelliği sağlamıyorsa ekran yanlış veri göstermeden `Desteklenmiyor` durumunu açıkça bildirir.

## Veri ve geçiş stratejisi

- Her alt sistem kendi tablolarını `CREATE TABLE IF NOT EXISTS` ve geriye uyumlu sütun geçişleriyle oluşturur.
- Var olan sipariş ve fatura tablolarının kimlikleri değiştirilmez.
- Geçişler açılışta tekrar çalıştırılabilir ve veri silmez.
- Yeni özellikler başlangıçta pasif olur; kullanıcı ekranından etkinleştirilir.
- Eski paketten veritabanı veya kimlik bilgisi ithal edilmez.

## Kullanıcı arayüzü

- Siparişler ekranındaki mevcut düzen korunur.
- Toplu işlem açılır listesine gerçek komutlar eklenir.
- Kuyruk için ayrı bir pencere açılır; sipariş listesi gereksiz sütunlarla genişletilmez.
- Satır durumları kısa Türkçe ifadeler ve renklerle gösterilir.
- Ayrıntılı teknik hata kullanıcıya temizlenmiş biçimde sunulur; gizli bilgi gösterilmez.
- Yazdırma, iptal, iade ve fiyat işlemlerinde son onay somut önizleme üzerinden alınır.

## Test stratejisi

Her alt sistem test önce geliştirilir.

### NES kuyruğu

- Sıralı çalışma ve tek etkin lease
- Yinelenen siparişin ikinci kez eklenmemesi
- Var olan faturanın tekrar kesilmemesi
- Belirsiz gönderimin mutabakat olmadan yeniden gönderilmemesi
- PDF yükleme hatasında yalnız yükleme adımının tekrarı
- Uygulama yeniden başladığında kuyruğun kaldığı güvenli adımdan devam etmesi

### Toplu işlemler

- Seçim olmadan işlem yapılmaması
- Karma sonuçların sipariş bazında raporlanması
- Bir sipariş hatasının diğer kayıtları bozmaması

### Yazdırma

- Geçersiz şablon ve barkodun reddedilmesi
- Önizleme olmadan yazdırmanın engellenmesi
- Yazıcı hatasında sipariş/fatura durumunun değişmemesi

### İptal ve iade

- Uzak durum yenilenmeden komut gönderilmemesi
- Başarısız API yanıtında stok hareketi oluşmaması
- Aynı iadenin iki kez stoğa alınmaması

### Arşiv

- Açık işlemi bulunan siparişin arşivlenmemesi
- Sayım/özet uyuşmazlığında kaynak silinmemesi
- Çakışmasız geri yükleme ve çakışmada güvenli durma

### Rekabet

- Eşleşmeyen ürün için öneri üretilmemesi
- Kâr ve fiyat sınırlarının dışına çıkılmaması
- API desteği yokken gönderim düğmesinin kapalı kalması

Her aşamada hedefli testlerden sonra tüm `MarketplaceHub.Tests` paketi Release yapılandırmasında çalıştırılır ve uygulama Release olarak derlenir.

## Uygulama aşamaları

1. NES sıralı kuyruk ve toplu fatura merkezi
2. Yazdırma altyapısı
3. Trendyol canlı iptal ve iade
4. Sipariş arşivleme ve geri yükleme
5. Hepsiburada rekabet analizi

Her aşama ayrı uygulama planı, test döngüsü ve doğrulama kapısı taşır. Sonraki aşama önceki aşamanın testleri geçmeden başlamaz.
