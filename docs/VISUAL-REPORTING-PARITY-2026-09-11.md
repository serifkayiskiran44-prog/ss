# Görsel, dashboard ve raporlama paritesi — 2026-09-11

## Mevcut durum
Ana daldaki `MainWindow.xaml` şu anda özgün MonoBridge sol menü + üst başlık + içerik paneli yapısında. Dashboard/chart kontrolü yok. `TrMarketplaceHubDesktop.csproj` içinde yalnız `ClosedXML` ve `Microsoft.Data.Sqlite` paketleri var; chart/UI kütüphanesi bulunmuyor. Bu yüzden mevcut çalışan ana dal görsel olarak Entegra dashboard/raporlama deneyimine henüz eşdeğer değildir.

## Entegra güncel resmi referanslarında doğrulanan görsel/rapor davranışları
- Eğitim merkezi 274 video / 13 kategori gösteriyor; Ürünler Listesi ve Sipariş Listesi ayrı günlük operasyon çalışma alanları.
- Ürünler Listesi için Genel Kullanım, Toplu İşlemler, Detaylı Arama, Sağ Tık ayrı eğitim akışları.
- Sipariş/Rapor ekranlarında kolon seçme-gizleme davranışı ve sipariş durumu filtreleri var.
- Raporlarda alış fiyatı, KDV'li alış fiyatı, sipariş tarihi gibi ticari kolonlar bulunuyor.
- Ürün raporlarına son 1 hafta / son 1 aya göre stok tükenme süresi (gün) kolonları eklenmiş.
- Ürün listesi/ürün formunda tarih bazlı fiyat, kritik fiyat, minimum/kritik stok gibi operasyon göstergeleri bulunuyor.
- Bildirim, rekabet analizi, iade, fulfillment, hakediş/mutabakat ve mesajlar ayrı operasyon alanları.

Hedef: Entegra marka/ikon/metin/kaynak kodunu kopyalamadan aynı bilgi yoğunluğu, drill-down, filtreleme, kolon yönetimi ve günlük karar desteğini özgün WPF ile sunmak.

## Görsel sistem hedefi
1. Ana sayfada büyük modül tile/kartları + gerçek KPI rozetleri.
2. Üst operasyon özeti: bugün sipariş, ciro, yaklaşık kâr, düşük/kritik stok, sync hatası, bekleyen fatura/iade, source anomaly.
3. Grafik kartları: satış/ciro trendi, sipariş adet trendi, kanal dağılımı, sipariş durum dağılımı, stok tükenme riski, yaklaşık kârlılık, supplier/feed health, sync/error trendi.
4. Grafik her zaman gerçek SQLite/store verisinden gelir; demo/sahte sayı yok.
5. Date range: bugün / 7 gün / 30 gün / özel aralık.
6. Channel/shop/supplier/category/brand filtreleri dashboard ve raporda ortak.
7. Grafikte tıklanan seri/nokta ilgili ürün/sipariş/hata listesine filtreli drill-down yapar.
8. Empty/loading/stale/error/offline durumları açıkça gösterilir.
9. 125–200% DPI, keyboard focus, screen-reader label ve color-only olmayan durum göstergesi.
10. Kart/kolon/grafik görünürlüğü kullanıcı profiline göre saklanabilir.

## Rapor merkezi hedefi
- Sipariş raporu: tarih, kanal, mağaza, durum, satış, indirim, vergi, alış maliyeti, yaklaşık kâr, refund/return/cancel.
- Ürün raporu: stok, satış hızı, 7/30 gün tükenme süresi, supplier, maliyet, satış fiyatı, marj, readiness, channel status.
- Kanal raporu: sipariş/ciro/kâr, listing readiness, sync error, stale listing, iade oranı.
- Supplier raporu: feed uptime, source-missing, fiyat/stok anomaly, son başarılı run, ürün sayısı.
- Operasyon raporu: sync jobs, retry/dead-letter, invoice, tracking, auth/rate-limit health.
- Kolon aç/kapat, sıra, genişlik, freeze, saved view, export current/filtered/selected.

## Güncel açık kaynak WPF referansları
### Live-Charts/LiveCharts2
- Public, MIT.
- WPF destekli; chart/map/gauge odaklı.
- 2026 itibarıyla aktif repo.
- Aday kullanım: dashboard KPI trendleri, line/bar/pie/donut benzeri veri görselleştirmeleri.
- Kod kör kopyalanmayacak; NuGet/reference spike ve self-contained publish doğrulanacak.

### lepoco/wpfui
- Public, MIT; .NET 8 dahil modern WPF hedefleri.
- Fluent controls/navigation/theme/icons sağlar.
- Tam UI migration zorunlu değil. Yalnız mevcut MonoBridge görsel sistemini bozmadan icon/theme/control primitive olarak değer katıyorsa kullanılabilir.
- Entegra görünümünü Fluent'e dönüştürmek amaç değildir; günlük bilgi yoğunluğu korunmalıdır.

### Koichi-Kobayashi/DataGridPerfLab
- WPF DataGrid için 100k satır performans davranışlarını karşılaştıran güncel referans.
- Özellikle virtualization recycling, ItemsSource replacement ve DeferRefresh yaklaşımı performans auditinde referans olabilir.
- Projeye doğrudan dependency olmak zorunda değil; davranış/performance referansı.

### macgile/DataGridFilter
- MIT, .NET 8 demo ve kolon filtre/preset yaklaşımı bulunuyor.
- Bizim ana stratejimiz SQLite-side filtre/sort/page olduğu için doğrudan replace etmeye gerek yok; header filter UX ve saved preset fikirleri davranış referansı olarak incelenebilir.

## Kabul kuralları
- Görsel olarak 'yakın' demek yalnız renk/kart benzerliği değildir; operasyonel bilgi hiyerarşisi ve drill-down çalışmalı.
- Ürün/sipariş/rapor listelerinde kolon yoğunluğu ve filtreleme korunmalı; dashboard marketing sayfasına dönüşmemeli.
- Chart library eklenirse lisans dosyası/notice ve self-contained publish kontrolü yapılmalı.
- Grafik verileri UI thread üzerinde ağır SQL/aggregation çalıştırmamalı; async + cancellation + bounded aggregation.
- Her KPI/chart için kaynağı ve veri tarihi/son güncelleme görünür olmalı.
- Entegra proprietary ikon/logo/asset/pixel-perfect klon yok; özgün MonoBridge görsel dili + çok yakın iş akışı/parite hedefi.
