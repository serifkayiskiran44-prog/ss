# Navlungo Express bağlantı hazırlığı

Bu modül uluslararası Navlungo Express içindir; Domestic API kullanılmaz. Normal kullanıcı hesabı API istemci kaydının yerini tutmaz. Navlungo ekibinden ortamınıza uygun client_id/client_secret ve kayıtlı callback tanımı gerekir. QA hesabı/istemcisi canlı ortamdan ayrıdır.

Uygulama client_id, callback ve ortamı Windows CurrentUser DPAPI ile `%LOCALAPPDATA%/MonoBridgeDesktop/navlungo.bin` içine kaydeder. Şifre/secret istenmez. Durum her zaman bağlantının henüz yetkilendirilmediğini belirtir. `NavlungoConnection.Begin` belgelenen standart Base64(SHA256) challenge ve rastgele state ile yalnızca bellek içi yetkilendirme hazırlığı üretir. UI bu eksik akışı başlatmaz.

Canlı token alışverişi, callback doğrulaması ve token yenileme henüz uygulanmadı. Sağlayıcı uygulama kaydı ve masaüstü kullanım onayı sonrası tamamlanmalıdır. Belgelenen token adresi `POST https://api.navlungo.com/v1/oauth/token` (QA: `api-qa.navlungo.com`), form encoded gövde; authorization_code için client_id, client_secret, code, code_verifier ve en az openid offline_access kapsamları. Secret masaüstü dağıtımına gömülmemelidir. Client credentials yalnızca sağlayıcının onayladığı sunucudan sunucuya senaryo içindir.

Mevcut Express teklif belgesi `POST stores/v2/{store_id}/orders` işlemini tanımlar; sipariş yaratır. Bu nedenle salt okuma gibi çağrılmaz; bu sürüm hiçbir teklif/sipariş/sevkiyat/ödeme yazma işlemi yapmaz. Eski GenerateQuoteRequest şeması için belgede çağrılabilir bağımsız rota bulunmadığından endpoint tahmin edilmedi.

Resmi kaynaklar (10 Eylül 2026 kontrolü):
- [Express ve yetkilendirme](https://github.com/Navlungo/public-api-docs)
- [Token](https://github.com/Navlungo/public-api-docs/blob/main/token.md)
- [Teklif işlemi](https://github.com/Navlungo/public-api-docs/blob/main/quote.md)

WPF bağlama: içerik alanına `NavlungoPanel.Create()` eklenir. Otomatik bağlantı/test çağrısı yoktur.
