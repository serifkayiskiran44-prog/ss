# İlk kurulum ve bağlantı sihirbazı (M39 / Issue #56)

İlk açılışta opsiyonel olarak görünen WPF sihirbazı temel mağaza metadata'sını, XML kaynağını, varsayılan stok/fiyat politikasını ve Excel profilini adım adım hazırlar. Kullanıcı sihirbazı atlayabilir; tamamlanan adımlar `onboarding.db` içinde yalnız metadata olarak saklanır ve yeniden açıldığında son adıma dönülür.

```mermaid
flowchart LR
  A[İlk açılış] --> B{Sihirbazı aç?}
  B -->|Atla| C[Normal panele geç]
  B -->|Başla| D[Mağaza metadata]
  D --> E[XML kaynak + önizleme ekranı]
  E --> F[Stok policy]
  F --> G[Fiyat policy]
  G --> H[Excel profili]
  H --> I[Sağlık özeti + hızlı geçiş]
```

Credential, token, parola veya API secret alanı sihirbazın state'ine yazılmaz. Kanalı doğrulanmamış bağlantılar `NOT_CONFIGURED` veya `LIVE_API_BLOCKED` olarak gösterilir; salt yerel kurulum adımları marketplace'e write isteği göndermez. XML adresine kullanıcı bilgisi/query secret'ı gömülmesi reddedilir. Her policy kaydı mevcut optimistic version ve formül doğrulamasını kullanır.
