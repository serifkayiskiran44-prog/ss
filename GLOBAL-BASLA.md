# MonoBridge Global — .NET 8 Windows

Çalıştırılacak dosya: `Windows-TekEkran/TrMarketplaceHubDesktop.exe`. Klasörü bütün olarak tutun; EXE'yi DLL dosyalarından ayırmayın. .NET 8 çalışma zamanı pakete dahildir. Önce açık eski MonoBridge uygulamasında işinizi kaydedip kapatın.

## İlk XML kaynağı
1. XML yönetimi → Yeni XML kaynağı. Adı ve HTTPS XML adresini veya dosyayı seçin. Gerekiyorsa Basic Auth bilgilerini girin.
2. Entegra standart biçimindeyse XML şablonu aç düğmesinden `templates/entegra-standart-etsy.json` seçilebilir. Bu başlangıç şablonudur; alan adlarını gerçek XML'inizle kontrol edin.
3. XML'i oku / alanları bul → ürün yolu ve alan eşleştirmesini kontrol edin. Ondalık ayırıcı XML'e göre seçilir. Görseller için `Images/Image | Image` gibi birden fazla yol kullanılabilir.
4. Fiyat / stok kuralları: Yeni kaynakta CASE WHEN formülü hazır gelir. Alış TRY, hedef USD ve otomatik TCMB kuru seçilidir. Formül TL alış fiyatına uygulanır, çıkan TL satış fiyatı 1 USD karşılığı TL'ye bölünür. “Formülü ve kuru test et” ile sonucu görün. Eski kaynaklar mevcut basit kur/kâr hesabında kalır; formül moduna geçiş isteğe bağlıdır. Ayrıntılar: [Formül ve kur](FORMUL-VE-KUR.md).
5. Önizleme hesapla → satırları seç → Seçilileri havuza al. Aynı kaynak tekrar alındığında SKU, yoksa benzersiz barkod üzerinden eşleşir; farklı tedarikçiler ayrı tutulur. Çakışan kimlikler işlemi durdurur.

Ürün havuzunda başlık/açıklama/fiyat/stok/görseller düzenlenebilir. XML'in manuel değerleri ezmemesi için ilgili kilit seçilir ve kaydedilir. Aynı ürünü başka işlem değiştirmişse eski kartın kaydı reddedilir. Yenileyip tekrar düzenleyin.

Otomatik havuz güncellemesi etkinleştirilirse yalnız uygulama açıkken işler. Kaynaktan kaybolan ürünler korunur. Otomatik Etsy aktarımı bu sürümde yoktur. Hatalı XML veya sayı, sessizce sıfır stok/fiyat olarak kabul edilmez.

## Etsy
1. Etsy bağlantısı ekranına Developer keystring, shared secret ve mağaza Shop ID girilir. Şifreli kaydet ardından bağlantıyı test et kullanılır.
2. OAuth için Etsy uygulamasında kayıtlı HTTPS redirect URI girilir. Etsy yetkilendirmesini aç düğmesi yalnız hesap onayı için tarayıcıyı açar. Onay sonrası tam dönüş adresi uygulamaya yapıştırılarak tamamlanır. Access/refresh token Windows DPAPI ile şifrelenir. Süresi bilinen token gerektiğinde yenilenir.
3. Etsy ilanları ekranı active/draft/inactive/sold_out/expired durumlarını 100'lük sayfalarla gerçek API'den okur.
4. Global Etsy şablonuna mağaza dövizi, kategori, kargo ve hazırlık profili kimlikleri; gerçek üretici/üretim dönemi ve etiketler girilir.
5. Havuzdaki **kaydedilmiş** ürün seçilir, şablon kontrol edilir. Etsy taslağı oluştur düğmesi onaydan sonra gerçek API'ye taslak isteği gönderir; ilanı yayınlamaz. Önce mağza para birimi kontrol edilir. Dönen ilan ID saklanır.

Taslak oluşturma sonucunun belirsiz olduğu ağ hatasında otomatik tekrar yoktur. Etsy ilanlarını kontrol edip oluşan taslağı seçili havuz ürününe bağlayın. Böylece mükerrer oluşturma önlenir. Bu sürümde belirsiz denemeyi sıfırlayan otomatik işlem yoktur.

## Durum ve sınırlar

| İşlev | Durum |
|---|---|
| Native WPF / .NET 8 Windows EXE | Var; tarayıcı paneli değil |
| Çoklu XML dosyası/HTTPS, Basic Auth, alan eşleştirme | Var |
| XML şablonu içe/dışa aktarımı | Var; sırlar şablona alınmaz |
| Önizleme, seçili import, SQLite havuz, alan kilitleri | Var |
| Tedarikçi bazlı kur/kâr/sabit/minimum fiyat ve stok koruması | Var |
| CASE WHEN fiyat formülü ve otomatik TCMB TL→USD dönüşümü | Var; elle kur da girilebilir; hatalı/eski kurda işlem durur |
| Program açıkken zamanlı yerel havuz güncellemesi | Var |
| Etsy OAuth/refresh ve ilan okuma | Kod ve HTTP sözleşme testleri var; canlı hesap doğrulanmadı |
| Etsy fiziksel ürün taslağı | Kod ve test var; başlık/açıklama/fiyat/adet/profiller. Canlı mağazada çalıştırılmadı |
| Etsy görsel/video ve varyant/SKU envanter gönderimi | Tamamlanmadı |
| Etsy canlı yayınlama ve mevcut ilan stok/fiyat kuyruğu | Tamamlanmadı |
| Sipariş/rezervasyon stoğu, Navlungo/ETGB, Fastfatura | Tamamlanmadı |
| Excel güncelleme, kategori bazlı fiyat bantları, gelişmiş varyant eşleştirme | Bu masaüstü sürümüne henüz taşınmadı |
| Program kapalıyken Windows servisi | Yok |
| Eski havuzların otomatik veri göçü | Yok; eski dosyalar korunur |

Bu teslim tüm Entegra özelliklerinin tamamlandığı anlamına gelmez. Etsy öncelikli XML/ürün yönetimi ve bağlantı temeli uygulanmıştır.

Yerel kayıtlar: `%LOCALAPPDATA%/MonoBridgeDesktop/catalog.db`, `etsy-template.json`, `credentials.bin`, `source-auth/` ve `operations.log`. Eski `hub.db`, web `pool.db` ve önceki EtsyXmlBridge projesi değiştirilmedi. İnceleme/test ürünleri kullanıcı havuzuna eklenmedi.

Temel alınan hazır proje: `C:/Users/serif/Documents/Codex/2026-09-09/d-zelt/outputs/EtsyXmlBridge`. XML → havuz → Etsy taslak akışı incelenerek yerel servisler yazıldı; web arayüzü masaüstü diye paketlenmedi. Paylaşılan ChatGPT bağlantısındaki video/eklere bu turda okunabilir erişim sağlanamadı.

Etsy sözleşmeleri: [OAuth](https://developers.etsy.com/documentation/essentials/authentication/), [ilanlar](https://developers.etsy.com/documentation/tutorials/listings/), [API referansı](https://developers.etsy.com/documentation/reference/).

Doğrulama: 141 otomatik test geçti. Gerçek WPF penceresinde XML yol keşfi, canlı TCMB kuru ile formül/fiyat/stok önizlemesi, seçili kayıt, manuel kilit, düzenlemeyi koruyan zamanlayıcı, Etsy eksik alan kontrolü ve formül hesap ekranı olmak üzere 7 kontrol geçti. Release Windows EXE üretildi. Gerçek Etsy hesabına gönderim yapılmadı.

XML tek ekran güncellemesi: Kaynak/bağlantı solda, seçilebilir XML alanları ortada, fiyat/kur/stok sağda, ürün önizlemesi alttadır. Bölme çizgileri sürüklenerek alanlar büyütülebilir. XML okununca bilinen alanlar önerilir; eşleşmeyen alanı açılır listeden seçin. Kaydı şablon olarak dışa aktararak yeniden kullanabilirsiniz. 141 otomatik test ve güncellenen WPF akışında 11 kontrol geçti. Ekran görseli örnek test verisi ve manuel 40 TL kur içerir.

