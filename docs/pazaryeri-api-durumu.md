# Pazaryeri API hazırlık durumu

Kontrol tarihi: 10 Eylül 2026. Bu belge araştırma ve uygulama önerisidir. Hesap açıldığı, satıcı onayı alındığı veya canlı API bağlantısı kurulduğu anlamına gelmez. Hiçbir kimlik bilgisi okunmadı veya kaydedilmedi; uzak hesaplarda değişiklik yapılmadı.

| Kanal | Resmî belgede doğrulanan bağlantı yolu | Tamamlanması gerekenler |
|---|---|---|
| eBay | Geliştirici hesabından uygulama anahtarları; satıcı adına işlem için OAuth authorization code ve kullanıcı onayı. Sandbox ve production anahtarları ayrıdır. | Satıcı hesabının satışa uygunluğu, aktif geliştirici hesabı, production uygulaması, redirect/RuName yapılandırması, gerekli Sell API izinleri ve başarılı hesap sorgusu. |
| Wish | API v3 OAuth; private app için Merchant Dashboard → Settings → API, uygulama adı ve redirect URL. Client ID/secret, access ve refresh token kullanılır. | Satıcı hesabı ve uygulama erişimi, sandbox doğrulaması, production OAuth onayı ve salt okunur token testi. |
| Ozon | Resmî Seller API doküman adresi bulundu ancak bu kontrolde içerik alınamadı. Kimlik doğrulama başlıkları ve güncel sürümler resmî kaynaktan bu oturumda doğrulanamadı. | Satıcı hesabı, API erişimi ve paneldeki güncel resmî belgeler. Client-Id/Api-Key beklenen entegrasyon biçimi olarak ayrıca doğrulanmalı; doğrulanmadan uç nokta uygulanmamalı. |
| Joom | Resmî merchant portalı API koşullarına bağlantı sunuyor. Doküman sunucuları bu kontrolde içerik döndürmedi. | Satıcı kabulü, uygulama/API erişimi, güncel OAuth veya token sözleşmesinin resmî panelden doğrulanması. Eski v2 uç noktaları varsayılmamalı. |
| Allegro | OAuth authorization code, PKCE ve device flow belgelenmiş. Uygulama kaydı için aktif hesap ve iki aşamalı giriş gerekiyor; sandbox istisnası var. | Satıcı hesabı, uygulama kaydı ve uygun redirect, satış izinleri ve kullanıcı onayı. Masaüstünde PKCE akışı tercih edilebilir. |
| Fruugo | Resmî Order API belgesi HTTP Basic doğrulamasını ve XML sipariş yanıtını belirtiyor. Product API ayrı geliştirici portalında yer alıyor. | Retailer kabulü ve erişim bilgileri; ürün aktarımı için güncel feed/Product API sözleşmesi, desteklenen auth ve yetkiler ayrıca teyit edilmeli. Order API doğrulaması tüm API'lere genellenmemeli. |

## Resmî kanıtlar

- eBay: [Sell API yetkilendirme](https://developer.ebay.com/develop/guides/sell/authorization), [uygulama anahtarları](https://developer.ebay.com/api-docs/static/oauth-credentials.html).
- Wish: [API v3 OAuth](https://merchant.wish.com/documentation/api/v3/oauth), [private app kurulumu](https://merchantfaq.wish.com/hc/en-us/articles/360034132014-Guide-for-Wish-API-Integrations-Private-App). OAuth test yolu belgede `GET /api/v3/oauth/test` olarak veriliyor. Token ve secret içeren URL'ler günlüklere yazılmamalı.
- Ozon: [Seller API dokümanı](https://docs.ozon.ru/api/seller/), [satıcı portalı](https://seller.ozon.ru/). Doküman gövdesi bu kontrolde erişilemedi; teknik doğrulama bekliyor.
- Joom: [satıcı koşulları ve API koşulları bağlantısı](https://merchant.joom.com/terms), [doküman sunucusu](https://docs.merchant.joom.com/). Doküman gövdesi bu kontrolde erişilemedi; teknik doğrulama bekliyor.
- Allegro: [resmî kimlik doğrulama rehberi](https://developer.allegro.pl/tutorials/uwierzytelnianie-i-autoryzacja-zlq9e75GdIR). Client-credentials yalnızca uygulamanın erişebildiği genel kaynaklar içindir; satıcı yetkilendirmesinin yerine geçmez.
- Fruugo: [resmî Order API](https://fruugo.atlassian.net/wiki/spaces/RR/pages/66158670/Order%2BAPI?atl_f=content-tree), [Retailer API](https://developer.fruugo.com/), [satıcı destek belgeleri](https://fruugo.atlassian.net/wiki/spaces/RR/overview).

## Mevcut havuza bağlanma önerisi

Güncel `MainWindow` ürün işlemlerinde `Catalog/CatalogStore.cs` kullanıyor; `HubData.cs` eski uygulamadan kalan sınıftır. Ortak havuz `CatalogStore` olarak korunmalı; yeni, ayrı ürün veritabanı açılmamalı. `CredentialStore.cs` mevcut Windows kullanıcısına bağlı DPAPI koruması sağlıyor fakat herkese açık Save/Load sözleşmesi Etsy'ye özel.

1. Mevcut havuzun önüne `IProductPool` erişim sözleşmesi koy: ürün kimliği/SKU ile okuma ve stok sürümü. Güncel `CatalogStore` bunun tek uygulaması olsun.
2. Her kanal için `IMarketplaceConnector` tanımla: `GetCapabilitiesAsync`, `TestConnectionAsync`, `ValidateProductAsync`, `PublishAsync`, `UpdateInventoryAsync`, `ReadOrdersAsync`. Desteklenmeyen işlemler açıkça bildirilir; başarılı gibi dönülmez.
3. Ayrı ürün kopyaları yerine `MarketplaceListings` eşlemesi tut: havuz ürün kimliği, kanal, hesap, dış ilan kimliği, varyant kimliği, son aktarım sürümü ve hata. Kanal + hesap + ürün + varyant bileşiminde benzersizlik sağla.
4. Kimlik deposunu hesap ve kanal anahtarıyla genişlet; mevcut Etsy kayıtlarını koru. OAuth state, süre, refresh token ve iptal akışlarını kanala göre uygula. Uygulama sırlarını dağıtılan masaüstü binary içine gömme.
5. Stok değişikliklerini aynı havuzdan outbox kuyruğuna yaz. Her kanal ayrı deneme/backoff uygulasın; sipariş içeri alma dış sipariş kimliğiyle yinelenmesin. Stok rezervasyonu tek havuzda atomik yapılsın.
6. Arayüzde aşamaları ayır: Başvuru bekliyor → API bilgisi bekliyor → Yetkilendirildi → Hesap sorgusu doğrulandı → Ürün aktarımı doğrulandı. Bir kayıt bağlantısına tıklamak veya token kaydetmek, entegrasyon tamamlandı sayılmasın.

İlk kabul ölçütü, altı kanalın her biri için gerçek yetkili hesap üzerinde salt okunur sorgunun başarılı olmasıdır. Yayın ve stok desteği ancak test ürünü, zorunlu kategori alanları, fiyat/para birimi ve dış ilan kimliği birlikte doğrulandıktan sonra tamamlanmış sayılır.
