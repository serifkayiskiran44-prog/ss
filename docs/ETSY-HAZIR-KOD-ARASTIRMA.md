# Etsy hazır kod ve Entegra araştırması

Kontrol tarihi: 10 Eylül 2026. İnceleme salt okunurdur; paket kurulmadı, mağazaya bağlanılmadı ve canlı yazma testi yapılmadı. Aşağıdaki sonuçlar kaynak/README, paket bildirimi ve GitHub metaverisi incelemesidir; üretim güvenilirliği sertifikası değildir.

## Sonuç

Mevcut .NET 8 / WPF uygulamasını korumak uygun. İncelenen hazır projeler çoğunlukla Etsy API istemci kütüphanesidir; Entegra düzeyinde stok, kategori, XML, kur, iş kuyruğu ve masaüstü yönetim uygulamasını birlikte sağlamaz. C# tarafında sınırlı bir `HttpClient` uyarlaması ve Etsy'nin resmi API şeması, yalnızca bir JavaScript SDK kullanabilmek için ikinci çalışma ortamı eklemekten daha doğrudan bir yoldur. Bu bir mimari değerlendirmedir.

## Aday karşılaştırması

| Aday | Doğrulanan durum | Projeye uygunluk |
|---|---|---|
| [profplum700/etsy-v3-api-client](https://github.com/profplum700/etsy-v3-api-client) | TypeScript/JavaScript, API v3, PKCE, yenileme ve hız sınırlama desteği beyanı; başlangıç örneğinde `sharedSecret` var. Ana dal paket bildirimi 3.0.0 ve Node >=24. Son ana dal commit tarihi 24 Temmuz 2026; repo push tarihi 10 Eylül 2026, bunlar aynı ölçüt değildir. | Kullanıcının TS şablonuyla bağlantılı iyi bir referans; doğrudan WPF bileşeni değil. README ve paket bildirimi MIT diyor, ancak kökteki LICENSE isteği 404 ve GitHub lisans tespiti boş döndü. Bazı eski README örneklerinde sharedSecret eksik; örnekler körlemesine kopyalanmamalı. |
| [Granga/etsy-ts](https://github.com/Granga/etsy-ts) | Ana dal 8.0.1; 15 Ağustos 2026 commit. v7 geçişi sharedSecret gerektiriyor; v8, processing-profile değişimi ve güncel listing imzalarını ele alıyor. Token depolama arayüzü ve yenileme mevcut. | İncelenen TS seçenekleri arasında güncel değişiklikleri en açık belgeleyen aday. Tam uygulama değil. LICENSE dosyası MIT, package.json ISC diyor: dağıtılacak sürümün lisans metni netleştirilmeli. |
| [creharmony/node-etsy-client](https://github.com/creharmony/node-etsy-client) | Ana dal 2.1.4; 15 Nisan 2026 commit. MIT; OAuth2 hizmeti ve `keystring:shared_secret` biçimi belgeli. README'de v3 için listelenen işlev kapsamı ağırlıkla okuma; test/coverage komutları var. | Okuma ve OAuth referansı. Gerekli bütün listing/yazma işlemlerini kapsadığı varsayılmamalı; WPF için Node katmanı gerektirir. |
| [EtsyAccess 1.7.4](https://www.nuget.org/packages/EtsyAccess) | .NET Standard 2.0 / .NET Framework 4.8 hedefli; son paket 13 Nisan 2022. NuGet açıkça eski ve artık bakımı yapılmıyor diyor. Kaynak repo API isteği 404 döndü. | Yeni .NET 8 entegrasyonu için önerilmez. NuGet'in hesapladığı net8 uyumluluğu, Etsy API v3 veya 2026 kimlik doğrulama uyumluluğu anlamına gelmez. Lisans ve kaynak durumu yeniden doğrulanmadan kod alınmamalı. |

Metaveri denetimi için birincil bağlantılar: [profplum paket bildirimi](https://github.com/profplum700/etsy-v3-api-client/blob/master/package.json), [Granga paket bildirimi](https://github.com/Granga/etsy-ts/blob/master/package.json), [Granga lisansı](https://github.com/Granga/etsy-ts/blob/master/LICENSE), [node-etsy-client paket bildirimi](https://github.com/creharmony/node-etsy-client/blob/main/package.json). Repo yıldız sayısı değerlendirme ölçütü olarak kullanılmadı. Bu incelemede testler çalıştırılmadı; test dosyası bulunması başarılı test kanıtı sayılmadı.

## Etsy için güncel teknik taban

Resmi istek standardı, v3 çağrılarında `x-api-key: keystring:shared_secret` istiyor. Yetkili işlemler ayrıca Bearer access token gerektiriyor. OAuth akışında PKCE ve state doğrulaması korunmalı; access token yanıtındaki kullanıcı öneki kesilip atılmamalı. [Resmi istek standardı](https://developer.etsy.com/documentation/essentials/requests/), [resmi kimlik doğrulama](https://developers.etsy.com/documentation/essentials/authentication/).

Kullanıcının yalnızca kendi mağazası için oluşturduğu uygulamada resmi güncel giriş sayfası Seller App yolunu gösteriyor. Başka satıcıların mağazalarını kapsayan kullanım ayrı erişim kapsamına tabi. Başvuru/onay durumu kullanıcının hesabında kontrol edilmeden çalışır bağlantı sözü verilemez. [Resmi uygulama erişimi](https://developers.etsy.com/).

## Entegra tarafında bulunan resmi kanıtlar

- Entegra eğitim merkezi XML içeri/dışarı aktarımı, kategori/platform şablonları, döviz ayarları ve Etsy tekil/varyantlı ürün listeleme eğitimlerini listeliyor. [Resmi eğitim merkezi](https://entegrasyon.com.tr/index.php).
- Ocak 2025 sürüm notunda Import XML formüllerinin Excel ile güncellenmesi bulunuyor. Ağustos 2026 notunda döviz ayarlarındaki has altın/gümüş otomatik güncellemesini kapatma seçeneği bulunuyor. Bu, döviz ayarı yeteneğinin kanıtıdır; bütün dövizlerin hangi kaynaktan/hangi sıklıkla yenilendiğini kanıtlamaz. [Resmi sürüm notları](https://entegrasyon.com.tr/gelistirmeler.php?s=7).
- Resmi eski sürüm notları Import XML fiyat/formül alanları ve bazı XML modüllerinde ürün bazlı durum güncellenmesin seçeneklerini belgeliyor. [2020–2021 değişiklikleri](https://www.entegrabilisim.com/blog/entegra-yenilikler-01122020-01022021).
- Ekim 2021 notlarında kategori, marka, durum ve ölçüler için farklı platformlara özgü güncellenmesin ayarları var. Bunlar bütün alanlarda ve bütün bağlantılarda ortak kilit sistemi bulunduğu anlamına gelmez. [Ekim 2021 değişiklikleri](https://www.entegrabilisim.com/entegra-yenilikler-01102021-01112021-b-MTIw).

Kategoriye özel XML fiyat formülünün kesin sözdizimi, kur değişince tüm Etsy fiyatlarının otomatik yeniden hesaplanması ve Etsy başlık/açıklama/fiyat alan kilitlerinin kapsamı bu araştırmada erişilen resmi metinlerle doğrulanamadı. Başka entegrasyon firmalarının benzer özellikleri Entegra özelliği diye aktarılmadı. MonoBridge içinde bu davranışlar tasarlanabilir; Entegra'nın mevcut sürümündeki ayarlarla aynı oldukları söylenmemeli.

## Uygulama yönü

Entegra kaynak kayıtlarını okuyan adaptör, kategori bazlı fiyat kuralı, zaman damgalı kur kaydı, kullanıcı alan kilitleri ve Etsy'ye gönderilecek değişiklik önizlemesi ayrı tutulmalı. İlk gerçek bağlantı yalnızca mağaza/liste okumasını doğrulamalı. Sonraki yazma aşamasında tek seçili taslak üzerinde izin verilen alanlar, varyantların korunması, yenileme ve 429 davranışı doğrulanmalı. Bunlar önerilen tasarım ve doğrulama adımlarıdır; mevcut uygulamada tamamlandıkları iddia edilmemektedir.
