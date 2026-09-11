# Formül ve otomatik döviz dönüşümü

Yeni program: `Windows-TekEkran/TrMarketplaceHubDesktop.exe`. Açık eski sürümde işinizi kaydedip kapatın, sonra bu EXE'yi çalıştırın. Klasörü bütün olarak tutun. .NET 8 pakete dahildir.

1. XML yönetimi → kaynak seçin veya yeni kaynak açın.
2. Fiyat / stok kuralları bölümünde formül modunu seçin. Eski kayıtların fiyat yöntemi kendiliğinden değiştirilmez.
3. “Gönderdiğim CASE WHEN formülünü yükle” düğmesi verdiğiniz ifadeyi aynen yerleştirir. Kutuyu düzenleyerek kendi formülünüzü de girebilirsiniz.
4. Alış para birimi TRY, hedef Etsy mağazanızın para birimi olmalıdır. USD için otomatik TCMB seçeneğini açık bırakın veya manuel olarak **1 USD kaç TL** değerini girin.
5. “Formülü ve kuru test et” ile örnek alış fiyatının sonucunu görün. XML önizlemesinde alış, formül sonucu TL, kullanılan kur ve satış fiyatını kontrol ederek havuza alın.

Hesap sırası: **TL alış → CASE WHEN → TL satış → kura bölme → hedef minimum fiyat → iki ondalık haneye yuvarlama**. Formül modunda eski kâr yüzdesi, sabit tutar ve kur çarpanı ayrıca uygulanmaz.

10 Eylül 2026 canlı kontrolde 1 USD = 48,4941 TL döviz satış kuru okundu: 100 TL alış → 509,615384… TL formül sonucu → **10,51 USD**. Bu örnek tarihli kurdur; sonraki hesapta güncel kur kullanılır. Kaynak: [TCMB günlük XML](https://www.tcmb.gov.tr/kurlar/today.xml).

Formülde `x` alış fiyatıdır. Ondalık ayırıcı nokta olmalıdır. `CASE WHEN`, `THEN`, `ELSE`, `END`, karşılaştırmalar, `AND/OR`, parantez ve dört işlem desteklenir; SQL sorgusu çalıştırılmaz. Verdiğiniz eşikler ve işlem önceliği korunmuştur: `x+100/1.20`, `(x+100)/1.20` değildir. Hiçbir koşul tutmazsa `ELSE` uygulanır.

Otomatik kur önizleme, hesap testi ve zamanlı XML alımında yenilenir. Kur alınamazsa işlem durur; eski kura sessizce geçilmez. Fiyat kilitli ürünlerin fiyatı ve ona ait kur kaydı korunur. Program açıkken zamanlı işlem yerel havuzu günceller; mevcut Etsy ilanlarını otomatik fiyatlandırıp yayınlamaz.

Görsel/varyant gönderimi, mevcut Etsy ilanlarının stok/fiyat senkronizasyonu ve Excel ile toplu kural düzenleme henüz tamamlanmadı. Hazır kod ve Entegra incelemesi: [Araştırma notu](docs/ETSY-HAZIR-KOD-ARASTIRMA.md).

