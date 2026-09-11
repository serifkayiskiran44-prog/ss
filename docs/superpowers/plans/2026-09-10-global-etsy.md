# Global Etsy ve XML masaüstü uygulama planı

> **For agentic workers:** Use subagent-driven-development for independent services; root integrates and verifies the native UI.

**Goal:** Kullanıcının EtsyXmlBridge şablonundaki XML → havuz → Etsy taslak akışını .NET 8 Windows uygulamasına taşımak.
**Architecture:** WPF ekranları; yerel SQLite kaynak ve ürün havuzu; ayrı XML kuralları ve Etsy HTTP servisleri. Şifreler Windows DPAPI ile korunur. Program kapalıyken arka plan servisi varmış gibi davranılmaz.
**Tech Stack:** .NET 8, WPF, Microsoft.Data.Sqlite 8.0.7, xUnit.
**Spec:** Kullanıcı pasted-text.txt gereksinimleri, Entegra-Inceleme/INCELEME-RAPORU.md ve önceki EtsyXmlBridge şablonu.

## Global Constraints
- Tarayıcı paneli değil Windows EXE; yalnız Etsy OAuth onayı için tarayıcı açılabilir.
- Etsy/global öncelikli; Türkiye pazaryerleri sonraki aşama.
- Entegra veritabanına yazılmaz. Eski proje/veriler silinmez.
- Canlı Etsy doğrulaması yapılmadan canlı çalışıyor iddiası yok. Otomatik yayınlama yok.

## Uygulama ve doğrulama
- [x] Catalog/*.cs: XML kaynaklarını SQLite JSON olarak sakla; XPath eşleştirme, önizleme, fiyat/stok kuralları, tedarikçi bazlı kimlik, kalıcı alan kilitleri. CatalogTests: tekrar import, farklı kaynak aynı SKU, kilitli fiyat/ad, hatalı sayı ve çakışmada atomik işlem.
- [x] XmlSourceReader.cs: dosya/HTTPS, 25 MB sınırı, XML encoding, DTD reddi, Basic auth DPAPI. XmlSourceReaderTests: dosya okuma, DTD ve HTTP yönlendirme reddi, auth header.
- [x] EtsyOAuth.cs / EtsyShopClient.cs: HTTPS callback, PKCE/state, tek kullanım, token yenileme; mağaza ilanlarını sayfalı oku. OAuth ve HTTP sözleşmeleri sahte ağ yanıtlarıyla sınanır.
- [x] EtsyDrafts.cs: global listeleme şablonu, kategori/kargo/hazırlık profili ve üretici bilgisi; gerçek API taslak oluşturma, dönen ilan kimliği saklama. Para birimi uyuşmazlığında POST yok; yayınlama yok.
- [x] MainWindow.xaml/.cs: kaynak listesi, eşleştirme, kurallar, seçili önizleme import, ürün düzenleme ve kilitler, API bağlantısı, Etsy ilanları ve taslak ekranı. Önizleme sonrası ayar değişirse yeniden önizleme zorunlu.
- [x] Otomatik XML: yalnız kullanıcı etkinleştirirse program açıkken çalışır; aynı anda çalışan işlemler engellenir; hatalı XML havuzu değiştirmez.
- [x] `dotnet test outputs/MonoBridgeDesktop.Tests/MonoBridgeDesktop.Tests.csproj` ve `dotnet publish outputs/MonoBridgeDesktop/TrMarketplaceHubDesktop.csproj -c Release -r win-x64 --self-contained true -o outputs/MonoBridgeDesktop/Windows-Global`.
- [x] EXE aç; XML örneğiyle eşleştirme/önizleme/havuza aktarım/alan kilidi akışını görsel kontrol et. Kullanıcı API anahtarlarıyla dış değişiklik yapma.

## Bu teslimde açık kalacak kapsam
Varyantların Etsy inventory API ile yazılması, görsel/video yükleme, sipariş-stok rezervasyon defteri, Navlungo/ETGB/Fastfatura ve program kapalıyken Windows servis otomasyonu ayrıca uygulanmalı. Eski şablonda da canlı Etsy testi yapılmamış.

