# CASE WHEN ve otomatik TL/USD fiyatlandırma

**Amaç:** Kullanıcının verdiği CASE WHEN formülünü aynen x=TL alış fiyatına uygulamak; çıkan TL satışını seçilen TCMB kuru veya elle girilen 1 USD karşılığı TL'ye bölmek.
**Mimari:** .NET8 WPF mevcut kaynak ekranı; güvenli decimal ifade ayrıştırıcı (SQL çalıştırılmaz); TCMB sınırlı XML okuyucu; kalıcı kaynak kur/formül ayarları. Eski kaynaklar Simple fiyat modunda aynı hesaplamayı sürdürür.

- [x] PriceFormula.Compile/Evaluate: aritmetik öncelik, CASE WHEN, AND/OR, tembel dal değerlendirme, sınırlar. SQL oracle ve sınır testleri.
- [x] TcmbRates.FetchAsync/Parse: yayımlanma tarihi ve döviz alış/satış çeşidi, Unit normalizasyonu, eski/eksik/sıfır kurda hata; otomatik fiyatlama sessiz eski kur kullanmaz.
- [x] CatalogPricing: Formula modu TL→formül→dövize bölme, son aşamada iki hane; Simple modu geriye uyum. Fiyat kilitlerinin para birimi/audit alanları korunur.
- [x] WPF formül kutusu, örneği yükle, hesap test alanı, AutoFx/manuel kur ve tarih gösterimi; kaynak önizlemesinde ve zamanlayıcıda kuru önce yenile. Formül/kur değişirse eski önizleme kaydedilemez.
- [x] Hazır Etsy kodları ve Entegra resmi belgeleri araştırma notu; SDK ile tam uygulama ayrımı, lisans/bakım bulguları.
- [x] .NET testler, gerçek WPF formül/önizleme akışı, Release EXE ve kullanım belgesi.

İlke: CASE eşiklerini veya parantezlerini değiştirme. Formül çıktısı negatife düşerse sıfıra gizlice çevirmeden hata göster. Bilinen eski kullanıcı verilerini otomatik yeni formüle geçirme. Canlı Etsy yazması yapılmaz.

