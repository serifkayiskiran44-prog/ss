# eBay OAuth bağlantısı

Bu sürüm, kendi geliştirici anahtarlarınızla eBay OAuth onayı alır ve Account API üzerinden satıcı kayıt durumunu okur. Ürün yayınlama, sipariş çekme, stok güncelleme ve ödeme kurulumu bu sürümde yoktur.

1. eBay Developers hesabınızda hedef ortam için App ID (Client ID), Cert ID (Client Secret) ve OAuth etkin RuName oluşturun. Production gerçek hesap; Sandbox ayrı test hesabı kullanır.
2. RuName kabul URL'sini size ait HTTPS sayfasına ayarlayın. URL sorgu ve fragment içermemeli; sayfa OAuth dönüşündeki `code` ve `state` parametrelerini adres çubuğunda korumalıdır. Dönüş URL'sini analitik, erişim günlükleri veya üçüncü kişilere aktarmayan bir sayfa kullanın. MonoBridge bu sayfayı barındırmaz.
3. Pazaryeri bağlantı hazırlığı → eBay bölümüne anahtarları, RuName ve aynı kabul URL'sini girin. Bilgiler Windows CurrentUser DPAPI ile `%LOCALAPPDATA%/MonoBridgeDesktop/ebay.bin` dosyasına şifreli kaydedilir. Bu kişisel anahtar kullanım modelidir; Cert ID içeren ayarları veya kullanıcı profilini dağıtmayın.
4. “eBay OAuth onayını aç” düğmesine basın ve eBay'in onay ekranını tamamlayın. Tarayıcının yönlendiği tam URL'yi dönüş alanına yapıştırıp “Dönüşü doğrula ve token al” düğmesine basın. Kod kısa ömürlüdür; hemen tamamlayın. Uygulama isteği 10 dakika sonra geçersiz sayar ve her kod alışverişini yalnızca bir kez dener. Hata olursa yeni onay başlatın.
5. “Bağlantı / satıcı durumunu doğrula” düğmesine basın. Yalnızca gerçek API yanıtı alındığında, kontrol saatiyle birlikte doğrulandı yazılır. Satıcı kayıt bayrağı false ise satıcı panelindeki kayıt adımları tamamlanmalıdır. Token kaydı veya OAuth başarısı tek başına bağlantı doğrulaması sayılmaz.

Token süresi dolmak üzereyse doğrulama sırasında yenilenir. Değişen uygulama/ortam ayarları mevcut token'ı yeniden kullanmaz. Uygulama yeniden açıldığında önceki kontrol sonucu bağlı olarak gösterilmez. “Yerel bağlantıyı sil” yerel dosyayı kaldırır; eBay üzerindeki OAuth iznini kaldırmaz.

Bu geliştirme sırasında canlı App ID/Cert ID ve satıcı onayı mevcut olmadığından gerçek eBay bağlantısı test edilmedi. Satıcı kaydı alanı Payoneer bağlantısını veya bir ilanın yayınlanabilirliğini kanıtlamaz.

Kaynaklar:
- [eBay OAuth yetkilendirmesi](https://developer.ebay.com/develop/guides/sell/authorization)
- [Resmî Account OpenAPI v3](https://developer.ebay.com/api-docs/master/sell/account/openapi/3/sell_account_v1_oas3.json): GET `/sell/account/v1/privilege`; `sell.account.readonly` kapsamı.
- [Satıcı kayıt durumu](https://developer.ebay.com/api-docs/sell/static/seller-accounts/ht_get-selling-limits.html)
