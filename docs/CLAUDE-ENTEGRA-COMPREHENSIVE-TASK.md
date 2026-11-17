# Claude devir görevi — kapsamlı operasyon merkezi

Tarih: 2026-11-17

## Amaç

MonoBridge'i Entegra'nın kaynak kodunu, özel API'lerini, markasını veya görsel varlıklarını kopyalamadan; aynı günlük operasyonların tek merkezden yönetilebildiği özgün bir masaüstü uygulamasına dönüştür.

Bu dosya bir temiz-oda işlev/parite görevidir. Entegra yalnız kullanıcının kendi hesabında görülen ekranlar ile resmi eğitim merkezi, resmi geliştirme notları ve resmi pazaryeri ayrıntıları üzerinden davranış referansıdır.

## Değişmez sınırlar

- `AGENTS.md`, `PROJECT_SPEC.md`, `TODO.md` ve `IMPLEMENTATION_STATUS.md` önce okunacak.
- Yeni login, uygulama parolası veya rol/kullanıcı sistemi eklenmeyecek.
- Gerçek veri ve şifreli credential yapısı korunacak; testler geçici veri dizini kullanacak.
- Canlı pazaryeri yazımı yalnız immutable preview, açık onay, stale/revision kontrolü, idempotency/receipt ve wrong-store/account guard ile yapılabilir.
- Gerçek mağazada otomatik test yazması yapılmayacak.
- `variant`, `bundle/set`, hızlı satır içi düzenleme, kritik fiyat, XML varyant mapping, fulfillment core ve settlement/reconciliation core kapsam dışıdır (`DEFERRED_BY_USER`).
- Doğrulanmamış kanal `LIVE_API_BLOCKED` kalacak; yerel plan canlı işlem gibi raporlanmayacak.
- Mevcut servisler yeniden yazılmayacak. Önce görünmeyen paneller rotaya bağlanacak, sonra gerçek boşluklar kapatılacak.
- Entegra'nın kaynak kodu/decompile çıktısı/private API'si kullanılmayacak; bot koruması aşılmayacak.

## Gözlem durumu

- Entegra ana menüsü salt okunur gözlendi: Anamenü, Raporlar ve Ürün Skoru üst alanları; ürünler, kategoriler, Excel, XML, siparişler, ürün eşleştirme, pazaryeri kısayolları, ayarlar, bildirim/eğitim araçları görünür.
- Uygulama yönetici/UAC katmanında açıldığı için alt ekranların eksiksiz otomatik tıklama turu bu oturumda yapılamadı. UAC aşılmadı.
- Resmi Entegra eğitim merkezi ve geliştirme notları; ürün/sipariş/kategori/marka/Excel/XML/mesaj/ayar/rapor ayrımını, kolon aç-gizle, depo seçili XML export, ürün-platform bağlantısı, akıllı fiyat küsuratı ve kanal bazlı eşleşme davranışlarını doğruluyor.
- MonoBridge kod denetimi, aşağıdaki operasyon panellerinin çoğunun servis veya ekran olarak mevcut fakat ana navigasyonda görünmez/parçalı olduğunu gösteriyor.

Resmi referanslar:

- https://www.entegrasyon.com.tr/
- https://entegrasyon.com.tr/gelistirmeler.php?s=2
- https://entegrasyon.com.tr/platformlar.php

## Hedef bilgi mimarisi

Ana navigasyon şu grupları tek ve tutarlı rota ağacında göstermeli:

1. Genel Bakış
2. Ürünler
   - Ürün havuzu
   - Güvenli toplu işlemler
   - Veri kalite / özgün ürün skoru
   - Görsel ve medya merkezi
   - Kategori, marka ve içerik şablonları
3. Mağazalar ve İlanlar
   - Ürün × exact-account ilan/eşleşme matrisi
   - Yeni ürün adayları
   - Kanal bazlı çalışma alanları
   - Trendyol operasyon merkezi
4. Siparişler
   - Birleşik sipariş listesi
   - Sipariş kartı
   - İstisna, iptal ve iade karar merkezi
   - NES/BizimHesap fatura merkezi
5. Veri Merkezi
   - Excel içe/dışa aktarma
   - XML içe aktarma
   - XML dışa aktarma ve profiller
   - Site yayınlama ve doğrulama
6. Otomasyon
   - Senkronizasyon işleri ve hata merkezi
   - Otomasyon takvimi
   - API sağlık, kota ve geri çekilme
7. Stok ve Politikalar
   - Depo/konum ve transfer
   - Merkezi stok rezervasyon defteri
   - Stok/fiyat politika merkezi
8. Raporlar ve Bildirimler
   - Operasyon raporları
   - Mesaj ve bildirim merkezi
9. Yönetim
   - Ayarlar ve yerel biçimler
   - Tanılama, denetim ve destek paketi
   - Üretim hazırlığı kontrolü
   - Yedekleme/geri yükleme
   - Eski veri taşıma asistanı

Tüm rotalar klavye erişilebilir olmalı, 1366×720'de kullanılmalı ve yalnız ekran açılması canlı HTTP/write başlatmamalı.

## İş sırası

### T0 — Bilgi mimarisi ve hazır panellerin görünürleştirilmesi (P0)

**Amaç:** Kullanıcının “program çok eksik” algısına neden olan görünmez/parçalı operasyon ekranlarını ortak kabuğa bağlamak.

**Dosyalar:** `MainWindow*.cs`, mevcut panel/window sınıfları, ortak navigation/route çözümleyicileri, WPF sözleşme testleri.

**Kabul:**

- Dashboard, toplu ürün, medya, listing matrix, veri kalite, sipariş istisnaları, mesajlar, API sağlık, tanılama, readiness, migration, policy, sync, automation, backup ve depo/konum panelleri erişilebilir.
- Aynı işi yapan yinelenen rota/düğmeler birleştirilir; servis veya veri deposu kopyalanmaz.
- `ScreenParityAudit` gerçek rota setiyle uyumludur.
- Her rota gerçek başlık, kısa açıklama, breadcrumb ve geri dönüş davranışına sahiptir.
- Açılış/navigasyon sıfır canlı yazma ve sıfır otomatik HTTP çağrısıyla test edilir.

### T1 — Merkezi stok rezervasyonu ve sipariş stok defteri (P0)

**Amaç:** Online stok, fiziksel konum, sipariş rezervasyonu, manuel satış, iptal/iade ve transferleri tek açıklanabilir ledger sözleşmesinde birleştirmek.

**Kabul:**

- Exact account + order line + product + location kimliğiyle idempotent rezervasyon/serbest bırakma makbuzu.
- İlk eşitleme öncesi geçmiş sipariş stok düşmez.
- Eksik SKU, yetersiz stok, arşivlenmiş sipariş ve stale karar fail-closed.
- İptal/iade yalnız yerel stok kararını uygular; doğrulanmamış canlı kanal onay/ret çağrısı yapılmaz.
- Her stok değişimi önce/sonra, neden, kaynak ve makbuz kimliğiyle denetlenebilir.

### T2 — Ürün operasyonu ve veri kalite kapanışı (P0)

**Amaç:** Ürün havuzu, güvenli toplu işlem, kaynak sahipliği, medya ve veri kaliteyi tek bağlamda birleştirmek.

**Kabul:**

- Duplicate barkod/SKU, eksik zorunlu alan, bozuk sayı/döviz/URL, kayıp kaynak, stale veri ve mağaza adayı filtrelenebilir.
- Özgün ürün skoru yalnız deterministik ve açıklanabilir kalite kurallarından türetilir.
- Her kalite bulgusu ilgili ürün, kaynak, medya veya mağaza önizlemesine gider.
- Otomatik sessiz düzeltme yoktur; mutasyon immutable preview + onay + receipt kullanır.
- XML kaynak sahipliği Content/Price/OnlineStock alanlarında açık görünür.

### T3 — Kategori, marka, içerik ve medya merkezi (P1)

**Amaç:** Yerel taksonomi, kanal eşlemeleri, içerik şablonları ve görsel doğrulamayı tek çalışma alanında toplamak.

**Kabul:**

- Kategori ağacı ürün sayılarıyla; marka listesi mağaza eşlemeleriyle açılır.
- Stale/eksik resmi metadata, çakışan eşleme ve eşleşmeyen ürün görünür.
- Başlık/açıklama/özellik şablonu hedef kanal önizlemesi üretir.
- Görsel dosyaları ürün kimliğine bağlıdır; kaynak dosyayı silmez.
- Resmi metadata olmadan uzak kategori/özellik yazımı yapılmaz.

### T4 — Mağaza ve ilan matrisi (P1)

**Amaç:** Product × exact account binding durumunu tek tabloda yönetmek.

**Kabul:**

- Aday, bağlı, eşleşmeyen, stale, blocked, hata ve başarı durumları ayrı görünür.
- Aynı kanalın iki hesabı grid, seçim, preview, receipt ve cache içinde karışmaz.
- Son başarılı sync, son hata, bekleyen ve eşleşmeyen sayıları gerçek veriden gelir.
- Yeni aday → barkod/SKU kontrolü → binding → kanal preview akışı açıkça ayrılır.
- `LIVE_API_BLOCKED` kanal destekleniyor gibi gösterilmez.

### T5 — Trendyol exact-account operasyon merkezi (P1)

**Amaç:** Mevcut Trendyol araçlarını tek hesap kapsamlı çalışma alanında birleştirmek.

**Kabul:**

- Ürün, kategori/marka, teslimat, içerik, fiyat/stok, rekabet ve işlem geçmişi aynı exact hesap bağlamındadır.
- Ürün adayı, mevcut ürüne eşleştirme ve yeni ürün oluşturma ayrı eylemlerdir.
- Cache yaşı, mağaza kuralları, son receipt ve item-level sonuç görünür.
- Tüm yazma yollarında supplier/account wrong-store guard vardır.
- Başarısız/belirsiz batch otomatik tekrar gönderilmez.

### T6 — Sipariş ve istisna operasyon merkezi (P1)

**Amaç:** Birleşik sipariş listesi, kart, stok kararı, istisna, fatura ve işlem geçmişini bağlamak.

**Kabul:**

- Kaynak/hesap/durum/tarih/eşleşme/inceleme filtreleri gerçek store sorgusuna bağlıdır.
- Eksik SKU, yetersiz stok, mükerrer sipariş, eski go-live siparişi ve fatura engeli ayrı kuyruklardır.
- Sipariş kartından ürün, hesap, stok makbuzu ve fatura geçmişine gidilir.
- Yerel iade kararı canlı kanal sonucu sayılmaz.
- Fulfillment ve settlement/reconciliation eklenmez.

### T7 — Excel/XML veri merkezi (P1)

**Amaç:** Excel ve XML işlerini kaynak, profil, preview, uygulama, undo ve yayın receipt zincirinde birleştirmek.

**Kabul:**

- Excel profil/kolon eşleme, gerçek satır önizlemesi, mod, stale kontrolü, atomik apply ve undo görünür.
- XML import, export, depo seçimi, alan sahipliği, kategori/formül, çalışma geçmişi ve recovery ayrıdır.
- Active/Category davranışı açık kuraldır; kullanıcı pasifi sessizce yeniden aktifleştirilmez.
- Site yayınlama: hedef adı/market allowlist, immutable preview, açık onay, upload, geri indirme, SHA-256 ve ürün sayısı doğrulama makbuzu.
- Scheduler ile manuel yayın aynı idempotency anahtarını paylaşır; duplicate publish olmaz.

### T8 — İş/scheduler/API sağlık merkezi (P2)

**Amaç:** XML, connector read ve publish işlerini ortak gözlemlenebilir yürütme modelinde toplamak.

**Kabul:**

- İş türü, exact kaynak/hesap, lease, son/sonraki çalışma, süre, ilerleme, son hata ve receipt görünür.
- Pause/resume/retry güvenli; restart sonrası duplicate run yok.
- 429/5xx exponential backoff; auth/bot engeli fail-closed ve kullanıcı eylemi ister.
- API sağlık ekranından ilgili iş geçmişine, iş geçmişinden account/source ayarına gidilir.
- Zamanlayıcı yalnız capability tarafından desteklenen salt-okunur veya açıkça onaylanmış işi başlatır.

### T9 — Fiyat ve stok politika merkezi (P2)

**Amaç:** Aynı politika motorunu kanal önizlemeleri ve XML export için ortak kaynak yapmak.

**Kabul:**

- Kaynak maliyet, KDV, komisyon, kargo, güvenlik stoğu, min/max sınır ve özel küsurat sonucu açıklanır.
- Trendyol, Hepsiburada yerel planı ve XML export aynı hesaplayıcıyı kullanır.
- Para birimi ve locale parse/format ayrımı testlidir.
- Politika değişikliği revision üretir; eski preview uygulanamaz.
- “Kritik fiyat” özelliği eklenmez.

### T10 — Rapor, dashboard ve bildirim merkezi (P2)

**Amaç:** Gerçek yerel veriden operasyonel görünürlük ve drill-down sağlamak.

**Kabul:**

- Ürün, stok, sipariş, XML, sync, hata ve API sağlık sayaçları gerçek repository sorgularıdır.
- Her kart/rapor satırı ilgili filtrelenmiş ekrana gider.
- Sipariş raporu kolon profili, aç/gizle ve Excel export destekler.
- Bildirimler sahiplik, önem, zaman ve ilgili nesne bağlantısı taşır.
- Uydurma ciro/kâr/kanal verisi yoktur.

### T11 — Ayarlar, tanılama, yedek ve migration (P2)

**Amaç:** İşletim ve güvenlik yönetimini tek merkezde tamamlamak.

**Kabul:**

- Genel/locale, mağaza bağlantısı, API health, diagnostics, support export, readiness, backup ve migration ayrı fakat bağlantılı bölümlerdir.
- Secret grid/log/export/test fixture içinde görünmez.
- Yedek manifest/hash doğrulaması; restore uygulama dosya kilidinde fail-closed.
- Migration dry-run + doğrulanmış yedek + açık onay + atomik checkpoint/receipt + rollback kullanır.
- Yeni login veya rol sistemi eklenmez.

## Uçtan uca akış kabulü

Claude her görevde yalnız ekranın varlığını değil aşağıdaki çapraz akışları korumalı:

1. Tedarikçi/XML → kalite → ürün havuzu → mağaza adayı.
2. Aday → barkod/SKU denetimi → exact-account binding → gönderim preview.
3. Sipariş → eşleşme sorunu → istisna kararı → stok makbuzu.
4. Ürün → medya doğrulama → içerik hazırlığı → kanal preview.
5. XML üretme → doğrulama → onay → siteye yükleme → geri indirip hash/adet doğrulama.
6. Bağlantı testi → API sağlık → backoff → retry/pause veya fail-closed.
7. Fiyat politikası → maliyet/vergi/komisyon/kargo → kâr açıklaması → küsurat → kanal/XML preview.
8. Otomasyon → lease → çalışma → hata sınıfı → pause/resume/retry → receipt.
9. Yedek → bütünlük → restore preview → kapalı uygulamada restore.
10. Rapor metriği → filtreli ürün/sipariş/mağaza/hata ayrıntısı.

## Her görev için teslim kuralı

1. Önce mevcut servis ve ekranı bul; kopya implementasyon yazma.
2. İlgili testleri önce ekle/güncelle.
3. `dotnet test tests/MarketplaceHub.Tests/MarketplaceHub.Tests.csproj -c Release` çalıştır.
4. `dotnet publish TrMarketplaceHubDesktop.csproj -c Release -r win-x64 --self-contained true -o Windows-Current` çalıştır.
5. `TODO.md` ve `IMPLEMENTATION_STATUS.md` güncelle.
6. Görev başına ayrı commit at; secret veya gerçek veri commit etme.
7. Canlı write yapmadan kanıt raporu ver: test sayısı, publish yolu, rota/senaryo, açık kalan gerçek API/credential engeli.

## Başlangıç kararı

İlk iş **T0** olmalı. T0 tamamlanmadan yeni servis/motor yazma. T0 sonrası sıra:

`T1 → T2 → T4 → T5 → T6 → T7 → T8 → T9 → T10 → T11`, ardından T3'ün kalan kanal-metadata ayrıntıları.

Bu sıra kullanıcının önce mevcut kapasiteyi görmesini sağlar ve aynı servislerin farklı ekranlarda tekrar yazılmasını engeller.
