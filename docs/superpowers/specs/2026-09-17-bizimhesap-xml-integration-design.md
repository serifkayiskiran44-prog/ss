# BizimHesap XML ürün entegrasyonu tasarımı

## Amaç

MonoBridge Desktop, tedarikçi XML kataloglarını mevcut güvenli içe aktarma akışıyla yerel ürün kataloğuna alacak ve seçilen ürünleri BizimHesap hesabıyla kontrollü biçimde eşleştirecek. İlk teslimat; bağlantı ayarları, salt okunur ürün/depo/stok erişimi, ürün eşleştirme ve onaylı yeni ürün oluşturmayı kapsar. Mevcut ürünün fiyatını veya depo stoğunu yazma, yalnız BizimHesap sözleşmesi ve davranışı doğrulandıktan sonra açılır.

## Doğrulanmış API yüzeyi

Sağlanan Postman koleksiyonunda aşağıdaki uçlar bulunur:

- `GET /api/b2b/products?page={page}&size={size}`: ürün listesi
- `GET /api/b2b/warehouses`: depo listesi
- `GET /api/b2b/inventory/{warehouseId}`: depo stoku
- `POST /api/b2b/addproduct`: ürün oluşturma

İsteklerde `Token` başlığı kullanılır. Kullanıcı tarafından sağlanan FirmID/API Key bilgisi bağlantı ayarlarında ayrı saklanır; her endpoint için hangi konumda gerektiği doğrulanmadan gönderilmez. Koleksiyondaki Cloudflare `Cookie` başlığı uygulamaya alınmaz.

`addproduct` örnek gövdesi:

```json
{
  "title": "Test ürün",
  "taxRate": 20,
  "id": "TEST-SKU-001",
  "price": 100,
  "currency": "TL",
  "productType": 1,
  "unit": "adet",
  "quantity": 5,
  "barcode": "TEST-BARKOD-001"
}
```

Kullanıcı testinde aynı `id` ikinci bir ürün oluşturmuş, `id` ürün listesindeki `code` alanına yazılmamış ve 1 TL fiyat `%20` KDV ile 1,20 TL okunmuştur. BizimHesap bu bildirimden sonra sunucu düzenlemesi yapıldığını, güncellemenin 24–48 saat içinde yayımlanacağını belirtmiştir. Bu nedenle `addproduct` şimdilik create-only kabul edilir; idempotent update olarak kullanılmaz.

## Mimari

### Bağlantı ayarları

Yeni `BizimHesapConnection` modeli aşağıdaki alanları taşır:

- görünen bağlantı adı ve mağaza/hesap kimliği
- FirmID
- Token
- seçili depo kimliği
- etkinlik ve son bağlantı testi bilgisi

FirmID ve Token mevcut `CredentialStore`/DPAPI yaklaşımıyla Windows kullanıcısına özel şifrelenir. Düz metin değerler SQLite, JSON, audit, log, test fixture, destek paketi veya Git içine yazılmaz. Hata metinleri mevcut redaction zincirinden geçirilir.

### API istemcisi

`BizimHesapClient`, `HttpClient` üzerinden aşağıdaki işlemleri sunar:

- sayfalı ürünleri okuma
- depoları okuma
- seçili depo stoklarını okuma
- yeni ürün oluşturma için immutable istek hazırlama ve gönderme

İstemci yalnız `https://bizimhesap.com/api/b2b/` taban adresini kabul eder; yönlendirmelerde kimlik bilgisi başka hosta taşınmaz. Timeout, iptal, 401/403, 429, 5xx ve geçersiz JSON ayrı hata sınıflarına çevrilir. Yanıt gövdesi loglanmaz; yalnız maskeli durum bilgisi saklanır.

### Ürün eşleştirme

Yerel ürün ile BizimHesap ürünü arasında kalıcı bir eşleme kaydı tutulur:

- yerel ürün kimliği ve SKU
- BizimHesap ürün kimliği
- bağlantı/hesap kimliği
- eşleştirme yöntemi: `Manual`, `Barcode`, `StockCode`, `Created`
- eşleştirme anındaki barkod, stok kodu ve yerel ürün sürümü
- oluşturulma ve son doğrulama zamanı

Otomatik eşleştirme önceliği:

1. Aynı bağlantıda daha önce kaydedilmiş BizimHesap ürün kimliği
2. Tekil ve boş olmayan barkod eşleşmesi
3. Tekil ve boş olmayan stok kodu/SKU eşleşmesi

Birden fazla aday, boş anahtar veya barkod/SKU çelişkisi otomatik eşleştirilmez. Kullanıcıya `AMBIGUOUS` veya `CONFLICT` gösterilir. Ürün adı otomatik kimlik olarak kullanılmaz.

### Oluşturma önizlemesi ve güvenlik kapısı

Kullanıcı ürünleri seçtiğinde sistem, her satır için `CREATE`, `MATCHED`, `SKIP`, `CONFLICT` veya `ERROR` kararı üretir. Önizleme; hedef hesap, SKU, barkod, ürün adı, KDV oranı, gönderilecek net fiyat, para birimi, miktar ve yerel ürün sürümünü gösterir.

Canlı `addproduct` isteği için:

1. kullanıcı değişmez önizlemeyi görür,
2. açıkça onay verir,
3. hedef FirmID/hesap yeniden doğrulanır,
4. ürün sürümü ve alanlar önizlemeyle karşılaştırılır,
5. aynı bağlantı + yerel ürün + payload fingerprint için başarılı receipt olmadığı kontrol edilir,
6. istek gönderilir ve maskeli receipt kaydedilir,
7. ürün listesi tekrar okunarak yeni BizimHesap ürün kimliği barkod/SKU üzerinden tekil biçimde bulunur.

Read-back sonucu tekil değilse işlem başarı olarak işaretlenmez; `NEEDS_RECONCILIATION` durumuna alınır ve ikinci kez otomatik oluşturma yapılmaz.

### Fiyat ve KDV

İlk sürümde `addproduct.price`, kullanıcının gözlemine dayanarak KDV hariç satış fiyatı kabul edilir. Gönderilecek tutar önizlemede “KDV hariç”, beklenen okuma değeri “KDV dahil” olarak ayrı gösterilir:

`beklenenBrüt = netFiyat × (1 + kdvOranı / 100)`

Bu varsayım bağlantı testiyle doğrulanmadan toplu canlı gönderim açılmaz. Yuvarlama iki ondalık basamakta ve deterministik yapılır. TRY girdisi API örneğine uygun olarak `TL` değerine dönüştürülür; bilinmeyen para birimi reddedilir.

### Stok ve fiyat güncelleme sınırı

Bağımsız update/set-stock endpointi henüz doğrulanmamıştır. İlk sürüm:

- BizimHesap stok ve fiyatını salt okunur karşılaştırır,
- farkları gösterir,
- güncelleme işi üretmez ve `LIVE_API_BLOCKED` durumunu açıkça gösterir.

`addproduct` aynı `id` ile güncelleme yapıyor görünse bile bu davranış; iki çağrı, ürün sayısı, dönen ürün kimliği, fiyat, stok ve KDV read-back sonuçlarıyla doğrulanmadan update yolu olarak kullanılmaz. Seçili depoya mutlak stok eşitleme endpointi ayrıca doğrulanmadan `quantity` alanı mevcut ürün güncellemek için kullanılmaz.

## Kullanıcı arayüzü

Mevcut mağaza bağlantıları merkezine `BizimHesap` kartı eklenir. Kart, credential varlığı, son salt okunur bağlantı testi, seçili depo ve write capability durumunu gösterir.

Ürün yönetiminde BizimHesap sekmesi şu bölümleri içerir:

- bağlantı ve depo seçimi
- BizimHesap ürünlerini yenileme
- eşleştirme adayları ve çakışmalar
- yeni ürün oluşturma önizlemesi
- onaylı gönderim ve read-back sonucu
- stok/fiyat farkları ve `LIVE_API_BLOCKED` açıklaması

Arayüz hiçbir zaman credential değerini geri göstermez. Program girişi veya yeni kullanıcı hesabı eklenmez.

## Test stratejisi

Otomatik testler gerçek BizimHesap hesabına yazmaz. Geçici SQLite ve sahte `HttpMessageHandler` ile aşağıdakiler doğrulanır:

- Token başlığı ve izin verilen HTTPS hostu
- ürün/depo/stok yanıtlarının ayrıştırılması ve sayfalama
- 401/403/429/5xx, timeout, iptal, bozuk JSON ve gizli bilgi redaction
- barkod ve SKU ile tekil eşleştirme; ambiguous/conflict durumları
- aynı payload için duplicate create engeli
- yanlış hesap, stale ürün sürümü ve onaysız gönderim engeli
- başarılı create sonrası read-back ve kalıcı BizimHesap ürün kimliği
- belirsiz read-back durumunda `NEEDS_RECONCILIATION` ve otomatik tekrar engeli
- KDV hariç/dahil hesaplama ve para birimi dönüşümü
- stok/fiyat write capability doğrulanmadığında HTTP yazma yapılmaması

Canlı kabul testi ayrı ve manuel yürütülür. Benzersiz bir test SKU/barkodu kullanılır; ilk oluşturma ve aynı `id` ile ikinci çağrı önceden kullanıcıya gösterilir. Her yazma için ayrı açık onay alınır. Ürün sayısı, kimlik, code, fiyat ve depo stoku çağrı öncesi/sonrası okunur. Sonuçlar endpoint sözleşmesine dönüştürülmeden üretim otomasyonu açılmaz.

## Teslimat sırası

1. Bağlantı modeli, şifreli credential store ve salt okunur API istemcisi
2. Ürün/depo/stok okuma testleri ve bağlantı ekranı
3. Kalıcı ürün eşleştirme ve çakışma ekranı
4. Create preview, onay, stale/idempotency/receipt ve read-back
5. Fake HTTP + geçici SQLite test paketi, Release test ve self-contained publish
6. Kullanıcı onaylı tek ürün canlı kabul testi
7. BizimHesap tarafından doğrulanmış update/set-stock sözleşmesi gelirse ayrı tasarım ve uygulama

## Başarı ölçütleri

- XML ürünü yerel kataloğa mevcut güvenlik kurallarıyla alınır.
- BizimHesap ürünleri ve depoları credential sızdırmadan okunur.
- Barkod/SKU çakışmaları yeni ürün oluşturmadan kullanıcıya gösterilir.
- Aynı yerel ürün için ağ tekrarında veya yeniden başlatmada duplicate create yapılmaz.
- Yeni ürün yalnız immutable preview ve açık onayla oluşturulur.
- Oluşturulan ürün read-back ile tekil BizimHesap kimliğine bağlanır.
- Doğrulanmamış fiyat/stok güncellemesi hiçbir HTTP yazma çağrısı üretmez.
- Tüm Release testleri ve self-contained win-x64 publish başarılıdır.
