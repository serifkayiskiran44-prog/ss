# Ürün görsel ve medya merkezi

Görsel kayıtları katalog JSON'undan bağımsız olarak `media.db` içinde tutulur. Böylece XML/Excel kaynağı, manuel giriş ve ileride doğrulanmış connector işlemleri aynı görünürlükte izlenebilir; mevcut `CatalogProduct.ImageUrls` alanı geriye dönük uyumluluk için korunur.

```mermaid
flowchart LR
  P[Ürün havuzu ImageUrls] --> I[MediaStore içe alma]
  U[Manuel HTTPS/file URL] --> N[URL normalize + duplicate kontrolü]
  I --> N
  N --> M[(ProductMedia)]
  M --> V[Dosya/HTTP doğrulama]
  V --> S{Durum}
  S -->|Ready| R[Önizleme ve ana görsel]
  S -->|404/timeout/too large/unsupported| E[Hata ve güvenli retry]
  R --> C[Connector preview/onay kapısı]
```

Doğrulama dosyayı okumakla sınırlıdır; hiçbir marketplace görseli değiştirilmez. URL'ler ürün içinde normalize edilir ve aynı URL ikinci kez kaydedilemez. Ana görsel kaldırılırsa en düşük sıra numarasındaki kayıt otomatik seçilir.
