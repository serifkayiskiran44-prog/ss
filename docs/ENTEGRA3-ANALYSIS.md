# Entegra-3 statik analiz notları

Tarih: 2026-09-11  
Kaynak: yetkili analiz/decompile çıktısı

## Kapsam ve güven sınırı

Çıktı ILSpy ile üretilmiş decompile koddur; özgün kaynak kodu değildir. Statik inceleme yapılmıştır, `entegra.exe` çalıştırılmamıştır. Obfuscation ve decompiler artefaktları nedeniyle sınıf/metot isimleri davranış kanıtı sayılmaz. Bu projeye özgün kod yazılır; orijinal marka, metin, görsel veya birebir ekran kopyalanmaz.

## Doğrulanan mimari işaretleri

- Teknoloji: .NET Framework/WPF masaüstü uygulaması.
- Decompiled C# dosyası: 12.570; sınıf içeren dosya: 11.452; UI adayı: 2.556.
- İki SQLite veritabanı var: `db.s3db` ve `db_cat.s3db`.
- `db_cat.s3db` ağırlıklı olarak marketplace kategori, attribute, attribute value ve cache tablolarını içeriyor.
- `db.s3db` ürün, marketplace ürün kopyaları/spec kayıtları, kategori/özellik/marka, sipariş, ödeme, kargo, XML/Excel, fiyat, mağaza, mesaj, hata ve rapor kayıtlarını içeriyor.
- Marketplace modeli kanal başına ayrı ürün/spec/option tablolarıyla genişletilmiş; merkezi ürün ile kanal ürünü ayrımı olduğu anlaşılıyor.

## Modül ayrımı

| Modül | Statik bulgu | Özgün masaüstü karşılığı |
|---|---|---|
| Ürün kataloğu | `product`, `product_info`, `product_description`, `product_brand`, `product_category`, `pictures` | `CatalogProduct` ve ürün kartı |
| Kanal ürünleri | Etsy, Ozon, Joom, Amazon, Trendyol ve diğer `product_*` tabloları | `ChannelProductPlan`; gerçek connector ayrı |
| Kategori/özellik | `category`, `attribute`, `attribute_value`, marketplace spec/cache tabloları | Normal kategori/marka eşleme katmanı; ertelenen varyant mapping hariç |
| Stok/fiyat | `product_quantity`, `product_price`, `product_prices`, `marketplace_quantity_settings`, `marketplace_price_settings` | Merkezi SQLite stok ve mağaza policy katmanı |
| Sipariş | `order`, `order_product`, `order_transaction`, `order_status`, ödeme tabloları | `OrdersStore`, siparişten atomik yerel stok düşümü |
| Kargo | `cargo_companies`, `cargo_fee_type`, `cargo_price`, shipment template tabloları | Navlungo/taşıyıcı adapter; gerçek API doğrulaması gerekir |
| XML/Excel | `xml`, `import_xml`, `import_excel`, `sql_to_xml`, `sql_to_excel`, `excel_template` | `XmlSource`, önizleme ve normal Excel akışı |
| Mağaza/ayar | `stores`, `settings`, `settings_info`, marketplace store settings | Şifreli credential store ve yerel mağaza ayarları |
| Mesaj/rapor | `messages`, `message_template`, `notify`, `report_design` | Henüz taşınmadı; iş kuyruğu ve hata merkezi ile bağımlı |
| Rekabet/AI | `competition_analysis_report`, `ai_product_*`, `autonomous_product_preview` | Referans aday; gerçek API/servis kanıtı yok |

## Veritabanı yorumu

Tablo listesi işlev gruplarını gösterir; ilişki ve işlem semantiği statik listeyle kesinleşmez. `db_cat.s3db` katalog cache veritabanı, `db.s3db` operasyon veritabanı gibi görünüyor; bu bir çıkarımdır. Bizim sistemde tek yerel `catalog.db` içinde atomik işlem tabloları kullanılıyor. Marketplace’e özel tabloları çoğaltmak yerine kanal kimliğiyle ortak kayıt modeli tercih edilecek.

## API ve belirsizlikler

`API_ENDPOINTS.md` dosyasında kullanılabilir endpoint kaydı yok. Bu nedenle Entegra’nın özel endpointleri, auth akışı, webhookları, rate limitleri ve servis sözleşmeleri bilinmiyor. Resmi marketplace API dokümanı olmadan endpoint yazılmayacak. Mevcut Etsy HTTP kodu yalnız Etsy resmi API sözleşmesine dayalıdır; yerel kanal planları canlı entegrasyon değildir.

## Obfuscation/decompile belirsizlikleri

- İsimler ve namespace’ler davranış kanıtı değildir.
- UI adayı dosya listesi gerçek menü/ekran eşlemesi değildir.
- Tablo adları kolon, foreign key, trigger ve transaction davranışını tek başına kanıtlamaz.
- API endpoint raporu olmadan ağ davranışı çıkarılamaz.
- `product_*` tablolarının tamamını ayrı özellik saymak duplicate model üretir.
- Görseller, formüller, hata metinleri ve marka varlıkları kopyalanmayacak.

## Tasarım kararı

MonoBridgeDesktop’ta ortak ürün havuzu, kanal planı, sipariş, XML ve şifreli credential çekirdekleri korunur. Yeni işlevler önce gerçek yerel SQLite testleriyle eklenir. Varyant sistemi, bundle/set, hızlı satır içi düzenleme, kritik fiyat, XML varyant mapping, fulfillment ve hakediş/mutabakat `DEFERRED_BY_USER` olarak kapsam dışıdır. Entegra-3 tablosu bulunan bir alan kendi ürünümüz için gereksinim kabul edilmeden önce davranış ve resmi API kanıtıyla doğrulanır.

## Sonraki uygulama sırası

1. Mağaza fiyat politikası ve güvenli önizleme
2. Normal Excel export/import önizleme ve atomik uygulama
3. Kategori/marka eşleme çekirdeği
4. Sync/hata kayıtları ve retry durumları
5. Resmi API sözleşmesi doğrulanan Etsy dikey akışı
6. Diğer marketplace adapterleri; capability yoksa açıkça desteklenmiyor gösterimi
