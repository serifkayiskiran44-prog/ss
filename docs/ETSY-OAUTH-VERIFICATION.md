# Etsy OAuth doğrulaması (M47)

Etsy'nin güncel resmi Open API dokümanına göre uygulama:

- Authorization URL: `https://www.etsy.com/oauth/connect`
- OAuth token URL: `https://api.etsy.com/v3/public/oauth/token`
- Her istekte `x-api-key: keystring:shared_secret`
- Scoped isteklerde Bearer OAuth 2.0 token
- PKCE S256, tek kullanımlık state ve HTTPS callback
- Ürün/ilan için `listings_r` ve `listings_w`, mağaza için `shops_r`, satış/sipariş okuma için `transactions_r`

`EtsyOAuth` token endpoint'i güncel `api.etsy.com` adresine düzeltildi. Credential değerleri yalnız DPAPI store'a yazılır; audit/log/support export'a alınmaz. Eksik credential veya scope durumunda read/write çağrısı başlatılmaz. Gerçek mağaza doğrulaması kullanıcı credential'ı gerektirdiği için fake HTTP contract testleri ile sınırlandırılmıştır.

Kaynak: https://developers.etsy.com/documentation/essentials/authentication/
