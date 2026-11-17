# Entegra Esinli Operasyon Arayüzü — Doğrulama Raporu

Tarih: 2026-11-17

## Kapsam

- Kompakt MonoBridge uygulama kabuğu ve global komut alanı
- Tam operasyon navigasyonu
- Ürün Yönetimi yoğun çalışma alanı
- XML Yönetimi iki bölmeli çalışma alanı
- Uygulama ve mağaza ayarları merkezi

Bu çalışma mevcut iş akışlarını yeniden düzenler. Yeni login eklemez, credential kopyalamaz, canlı marketplace yazımı yapmaz ve `DEFERRED_BY_USER` alanlarını genişletmez.

## Otomatik doğrulama

Komut:

```powershell
dotnet test tests/MarketplaceHub.Tests/MarketplaceHub.Tests.csproj -c Release
```

Sonuç: **1491/1491 başarılı**, başarısız ve atlanan test yok.

Yayın komutu:

```powershell
dotnet publish TrMarketplaceHubDesktop.csproj -c Release -r win-x64 --self-contained true -o Windows-Entegra-Operations
```

Paketleyici ayrıca integrity manifestlerini üretti, yayın klasörünü denetledi ve gerçek MainWindow smoke'unu izole geçici veri dizininde çalıştırdı.

## Yayın sonucu

- Yayın klasörü: `Windows-Entegra-Operations`
- Paket: `Windows-Entegra-Operations.zip`
- Paket SHA-256: `8b055e32ef666ee03a08f5cecfdeb98f70650f0b9dc0ac4621630944bbe23e89`
- EXE SHA-256: `48CAC4501CC0AE44BFD2AC590CAC861A9855078FBA9A9B6E151CDC893DDE26FF`
- Paket smoke: **BAŞARILI**
- Gerçek MainWindow açılışı: **BAŞARILI**
- Görülen pencere başlığı: `MonoBridge — Yönetim Merkezi`

## Görsel kontrol durumu

Yayınlanan uygulama Windows üzerinde açıldı ve ana pencerenin varlığı doğrulandı. Bilgisayar denetimi erişilebilirlik katmanı bu WPF penceresine bağlanamadığı için 1366×720 ve büyütülmüş pencerede Ana Sayfa, Ürünler, XML, Ayarlar, Pazaryerleri ve Siparişler arasında otomatik tıklama turu tamamlanamadı. Bu nedenle ayrıntılı görsel tur **bekleyen manuel kontrol** olarak tutulur; “tamamlandı” olarak raporlanmaz.

İlgili rotaların oluşturulması, görünürlüğü ve önemli yerleşim sözleşmeleri WPF regresyon testlerinde doğrulandı. Gerçek uygulama açılışında hata veya çökme görülmedi.

## Güvenlik ve veri sınırı

- Smoke testleri geçici `MARKETPLACEHUB_DATA_DIR` kullandı.
- Gerçek kullanıcı veri dizini ve credential kasası kullanılmadı.
- Canlı marketplace HTTP çağrısı veya yazma işlemi yapılmadı.
- Ayrı kirli çalışma kopyasında kalan “Yeni ürün adayı” değişiklikleri bu dala alınmadı ve değiştirilmedi.

## Manuel son kontrol listesi

- [ ] 1366×720 pencerede ana navigasyon ve global komut alanı taşmadan görünüyor.
- [ ] Büyütülmüş pencerede Ürün Yönetimi tablosu ve düzenleme alanı doğru büyüyor.
- [ ] XML Yönetimi ayırıcısı sürükleniyor; kaynak ve tanım bölmeleri kullanılabilir kalıyor.
- [ ] Ayarlar bölümleri ve mağaza bağlantısı rotası doğru açılıyor.
- [ ] Pazaryerleri ve Siparişler rotaları mevcut çalışma alanlarına gidiyor.
